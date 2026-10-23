using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;
using Microsoft.Web.WebView2.Core;

namespace Amarin.Browser;

/// <summary>
/// Встроенный браузер агента на WebView2 (1.37.0): окно с отдельным профилем, страница — через DOM.
/// </summary>
/// <remarks>
/// <para>
/// Профиль свой, в папке профиля программы (<c>browser/</c>): с Edge человека он не делит ни
/// входов, ни истории, а входы, сделанные здесь, переживают прогон и перезапуск.
/// </para>
/// <para>
/// Читается страница скриптом: текст и элементы, которые можно нажать или заполнить; элементы
/// помечаются атрибутом с id этого чтения, и следующее чтение снимает прежние метки. Нажатия и
/// ввод идут доверенными событиями DevTools (<c>Input.*</c>): синтетический <c>click()</c> часть
/// сайтов не принимает, а событие, неотличимое от настоящего, принимают все.
/// </para>
/// <para>
/// Всё, что трогает WebView2, — на потоке окна: агент зовёт порт со своего потока, и каждый метод
/// переносит работу через диспетчер. Решений здесь нет: что открывать и куда вводить, решает
/// ядро (<see cref="BrowserSafety"/>), здесь — только исполнение и честный ответ.
/// </para>
/// </remarks>
internal sealed partial class WebViewBrowser : IWebBrowser
{
    /// <summary>Сколько ждать загрузку страницы: дальше — ответ «не ответила», а не зависший прогон.</summary>
    private static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Сколько ждать, не начнёт ли действие переход на другую страницу.</summary>
    private static readonly TimeSpan NavigationGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>Затишье после действия: скрипты страницы дорисовывают то, что оно вызвало.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(350);

    private readonly Dispatcher _dispatcher;
    private readonly string _profileFolder;
    private readonly long _maxDownloadBytes;
    private readonly Action<WebViewBrowser> _closed;
    private readonly Lock _notesGate = new();
    private readonly List<string> _notes = [];

    private AgentBrowserWindow? _window;
    private CoreWebView2? _core;
    private int _generation;
    private bool _navigating;
    private TaskCompletionSource? _navigation;
    private bool _closingOurselves;

    /// <param name="closed">Окно закрылось — хозяин убирает браузер из своего списка.</param>
    public WebViewBrowser(Dispatcher dispatcher, string profileFolder, long maxDownloadBytes, Action<WebViewBrowser> closed)
    {
        _dispatcher = dispatcher;
        _profileFolder = profileFolder;
        _maxDownloadBytes = maxDownloadBytes;
        _closed = closed;
    }

    public bool ClosedByHuman { get; private set; }

    public Task<BrowserPage> OpenAsync(Uri url, CancellationToken cancellationToken) =>
        OnUi(async () =>
        {
            var core = await EnsureAsync().ConfigureAwait(true);
            await NavigateAsync(() => core.Navigate(url.AbsoluteUri), cancellationToken).ConfigureAwait(true);
            return await ReadCoreAsync(BrowserTool.TextDefault, BrowserTool.ElementsDefault, cancellationToken).ConfigureAwait(true);
        });

    public Task<BrowserPage> ReadAsync(int maxText, int maxElements, CancellationToken cancellationToken) =>
        OnUi(async () =>
        {
            _ = await EnsureAsync().ConfigureAwait(true);
            return await ReadCoreAsync(maxText, maxElements, cancellationToken).ConfigureAwait(true);
        });

    public Task<BrowserElement?> FindAsync(string id, CancellationToken cancellationToken) =>
        OnUi(async () => (await LocateAsync(id, cancellationToken).ConfigureAwait(true))?.Element);

    public Task<bool> ClickAsync(string id, CancellationToken cancellationToken) =>
        OnUi(async () =>
        {
            if (await LocateAsync(id, cancellationToken).ConfigureAwait(true) is not { } target)
            {
                return false;
            }

            await ActAsync(async core =>
            {
                await Mouse(core, "mouseMoved", target.X, target.Y, cancellationToken).ConfigureAwait(true);
                await Mouse(core, "mousePressed", target.X, target.Y, cancellationToken).ConfigureAwait(true);
                await Mouse(core, "mouseReleased", target.X, target.Y, cancellationToken).ConfigureAwait(true);
            }, cancellationToken).ConfigureAwait(true);
            return true;
        });

    public Task<bool> TypeAsync(string id, string text, bool replace, CancellationToken cancellationToken) =>
        OnUi(async () =>
        {
            var core = await EnsureAsync().ConfigureAwait(true);

            // Поле могло пропасть после проверки: страница перерисовалась. Тогда текст ушёл бы туда,
            // где сейчас фокус, — в чужое поле.
            if (await ScriptAsync(core, FocusScript(id, replace), cancellationToken).ConfigureAwait(true) != "true")
            {
                return false;
            }

            if (text.Length > 0)
            {
                _ = await DevToolsAsync(core, "Input.insertText", JsonSerializer.Serialize(new { text }), cancellationToken)
                    .ConfigureAwait(true);
            }

            await Task.Delay(Settle, cancellationToken).ConfigureAwait(true);
            return true;
        });

    public Task<bool> SelectAsync(string id, string option, CancellationToken cancellationToken) =>
        OnUi(async () =>
        {
            var core = await EnsureAsync().ConfigureAwait(true);
            var raw = await ScriptAsync(core, SelectScript(id, option), cancellationToken).ConfigureAwait(true);
            await Task.Delay(Settle, cancellationToken).ConfigureAwait(true);
            return raw == "true";
        });

    public Task PressAsync(string key, CancellationToken cancellationToken) =>
        OnUi(async () =>
        {
            var (name, code, keyCode, text) = KeyOf(key);
            await ActAsync(async core =>
            {
                _ = await DevToolsAsync(core, "Input.dispatchKeyEvent", JsonSerializer.Serialize(new
                {
                    type = text is null ? "rawKeyDown" : "keyDown",
                    key = name,
                    code,
                    windowsVirtualKeyCode = keyCode,
                    text
                }), cancellationToken).ConfigureAwait(true);
                _ = await DevToolsAsync(core, "Input.dispatchKeyEvent", JsonSerializer.Serialize(new
                {
                    type = "keyUp",
                    key = name,
                    code,
                    windowsVirtualKeyCode = keyCode
                }), cancellationToken).ConfigureAwait(true);
            }, cancellationToken).ConfigureAwait(true);
            return true;
        });

    public Task ScrollAsync(bool down, CancellationToken cancellationToken) =>
        OnUi(async () =>
        {
            var core = await EnsureAsync().ConfigureAwait(true);
            _ = await ScriptAsync(
                core,
                $"window.scrollBy({{ top: {(down ? "" : "-")}Math.round(window.innerHeight * 0.85) }}); true",
                cancellationToken).ConfigureAwait(true);
            await Task.Delay(Settle, cancellationToken).ConfigureAwait(true);
            return true;
        });

    public Task<bool> BackAsync(CancellationToken cancellationToken) =>
        OnUi(async () =>
        {
            var core = await EnsureAsync().ConfigureAwait(true);
            if (!core.CanGoBack)
            {
                return false;
            }

            await NavigateAsync(core.GoBack, cancellationToken).ConfigureAwait(true);
            return true;
        });

    public Task<byte[]?> ScreenshotAsync(CancellationToken cancellationToken) =>
        OnUi(async () =>
        {
            var core = await EnsureAsync().ConfigureAwait(true);
            using var image = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, image)
                .WaitAsync(PageTimeout, cancellationToken).ConfigureAwait(true);
            return image.Length > 0 ? image.ToArray() : null;
        });

    public Task<bool> HandoffAsync(string message, TimeSpan limit, CancellationToken cancellationToken) =>
        OnUi(async () =>
        {
            _ = await EnsureAsync().ConfigureAwait(true);
            var window = _window ?? throw new InvalidOperationException("the window is closed.");
            var answer = window.AskHuman(message);
            try
            {
                var finished = await Task.WhenAny(answer, Task.Delay(limit, cancellationToken)).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                return finished == answer && answer.Result;
            }
            finally
            {
                window.EndHandoff();
            }
        });

    public IReadOnlyList<string> TakeNotes()
    {
        lock (_notesGate)
        {
            var notes = _notes.ToList();
            _notes.Clear();
            return notes;
        }
    }

    public void Close() =>
        _dispatcher.BeginInvoke(() =>
        {
            _closingOurselves = true;
            _window?.Close();
        });

    // ───────────────────────── окно и переходы ─────────────────────────

    private Task<T> OnUi<T>(Func<Task<T>> work) => _dispatcher.InvokeAsync(work).Task.Unwrap();

    private async Task<CoreWebView2> EnsureAsync()
    {
        if (_core is { } ready)
        {
            return ready;
        }

        if (ClosedByHuman)
        {
            throw new InvalidOperationException("the person closed its window.");
        }

        var window = new AgentBrowserWindow();
        _closingOurselves = false;
        window.Closed += (_, _) =>
        {
            ClosedByHuman |= !_closingOurselves;

            // Явно, а не сборкой мусора: иначе процессы msedgewebview2 этого профиля живут, пока
            // объект не соберут, и держат его папку.
            window.View.Dispose();
            _core = null;
            _window = null;
            _navigation?.TrySetResult();
            _closed(this);
        };

        Place(window);
        _window = window;
        window.Show();

        try
        {
            Directory.CreateDirectory(_profileFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, _profileFolder)
                .WaitAsync(PageTimeout).ConfigureAwait(true);
            await window.View.EnsureCoreWebView2Async(environment).WaitAsync(PageTimeout).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or IOException or
                                       UnauthorizedAccessException or TimeoutException)
        {
            _closingOurselves = true;
            window.Close();
            throw new InvalidOperationException(ex is WebView2RuntimeNotFoundException
                ? "Microsoft Edge WebView2 Runtime is not installed on this PC."
                : "the browser engine did not start (" + ex.Message + ").");
        }

        var core = window.View.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.SourceChanged += (_, _) => window.ShowAddress(core.Source);
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        _core = core;
        return core;
    }

    /// <summary>
    /// Справа на рабочей области, не поверх середины: человек продолжает своё, а окно агента
    /// видно сбоку.
    /// </summary>
    private static void Place(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.Width = Math.Min(window.Width, area.Width - 32);
        window.Height = Math.Min(window.Height, area.Height - 32);
        window.Left = area.Right - window.Width - 16;
        window.Top = area.Top + Math.Max(16, (area.Height - window.Height) / 2);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !BrowserSafety.AllowsNavigation(uri))
        {
            // Переход начала страница — модель о нём не просила, и узнать о запрете должна.
            e.Cancel = true;
            Note($"The browser did not follow a link to {Shorten(e.Uri)}: files, this computer and the local network are off limits.");
            _navigation?.TrySetResult();
            return;
        }

        _navigating = true;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _navigating = false;
        _navigation?.TrySetResult();
    }

    /// <summary>Новое окно страницы открывается в этом же: у агента одна страница.</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (_core is { } core && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && BrowserSafety.AllowsNavigation(uri))
        {
            core.Navigate(uri.AbsoluteUri);
        }
    }

    /// <summary>Переход, начатый вызывающим: ждём конец загрузки, но не дольше срока.</summary>
    private async Task NavigateAsync(Action start, CancellationToken cancellationToken)
    {
        var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _navigation = navigation;
        start();
        var finished = await Task.WhenAny(navigation.Task, Task.Delay(PageTimeout, cancellationToken)).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (finished != navigation.Task)
        {
            throw new TimeoutException();
        }

        await Task.Delay(Settle, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Действие, после которого страница может уйти на другую: если переход начался, ждём его
    /// конца, потом — затишья.
    /// </summary>
    private async Task ActAsync(Func<CoreWebView2, Task> act, CancellationToken cancellationToken)
    {
        var core = await EnsureAsync().ConfigureAwait(true);
        await act(core).ConfigureAwait(true);
        await Task.Delay(NavigationGrace, cancellationToken).ConfigureAwait(true);
        var waited = Stopwatch.StartNew();
        while (_navigating && waited.Elapsed < PageTimeout)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(true);
        }

        await Task.Delay(Settle, cancellationToken).ConfigureAwait(true);
    }

    private static Task<string> Mouse(CoreWebView2 core, string type, double x, double y, CancellationToken cancellationToken) =>
        DevToolsAsync(core, "Input.dispatchMouseEvent", JsonSerializer.Serialize(new
        {
            type,
            x,
            y,
            button = type == "mouseMoved" ? "none" : "left",
            clickCount = type == "mouseMoved" ? 0 : 1
        }), cancellationToken);

    /// <summary>
    /// Скрипт страницы со сроком: зависший скрипт сайта держал бы прогон агента вечно, и «Стоп» его не
    /// снял бы — сам WebView2 отмены не принимает.
    /// </summary>
    private static Task<string> ScriptAsync(CoreWebView2 core, string script, CancellationToken cancellationToken) =>
        core.ExecuteScriptAsync(script).WaitAsync(PageTimeout, cancellationToken);

    /// <summary>
    /// Метод DevTools со сроком: пока на странице открыт <c>alert()</c>, события ввода не возвращаются
    /// вовсе.
    /// </summary>
    private static Task<string> DevToolsAsync(CoreWebView2 core, string method, string json, CancellationToken cancellationToken) =>
        core.CallDevToolsProtocolMethodAsync(method, json).WaitAsync(PageTimeout, cancellationToken);

    // ───────────────────────── загрузки ─────────────────────────

    /// <summary>
    /// Файл со страницы — через тот же белый список, что у <c>download_file</c>: сайта нет в
    /// списке — человек решает, как и там. Ложится в «Загрузки» под свободным именем.
    /// </summary>
    private async void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            await DecideDownloadAsync(e).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Обработчик события без ожидающего: исключение уронило бы программу, а не загрузку.
            e.Cancel = true;
            Note("A download was stopped: " + ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task DecideDownloadAsync(CoreWebView2DownloadStartingEventArgs e)
    {
        var operation = e.DownloadOperation;
        if (!Uri.TryCreate(operation.Uri, UriKind.Absolute, out var source) ||
            !DownloadValidator.TryValidateUrl(source, out _))
        {
            e.Cancel = true;
            Note($"A download from {Shorten(operation.Uri)} was refused: only http and https.");
            return;
        }

        if (!DownloadValidator.IsDomainAllowed(source) &&
            !(await DownloadAccessBroker.RequestAsync(source.Host).ConfigureAwait(true) && DownloadValidator.IsDomainAllowed(source)))
        {
            e.Cancel = true;
            Note($"A download from {source.Host} was refused: the site is not in the download allowlist.");
            return;
        }

        var path = FreePath(DownloadPaths.DownloadsDirectory, Path.GetFileName(e.ResultFilePath));
        e.ResultFilePath = path;
        e.Handled = true;
        operation.BytesReceivedChanged += (_, _) =>
        {
            if (operation.BytesReceived > _maxDownloadBytes)
            {
                operation.Cancel();
            }
        };
        operation.StateChanged += (_, _) =>
        {
            switch (operation.State)
            {
                case CoreWebView2DownloadState.Completed:
                    Note($"Downloaded to {path}.");
                    break;
                case CoreWebView2DownloadState.Interrupted:
                    Note($"The download of {Path.GetFileName(path)} stopped: {operation.InterruptReason}.");
                    break;
            }
        };
    }

    private static string FreePath(string folder, string? name)
    {
        var file = string.IsNullOrWhiteSpace(name) ? "download" : name;
        var path = Path.Combine(folder, file);
        var stem = Path.GetFileNameWithoutExtension(file);
        var extension = Path.GetExtension(file);
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){extension}");
        }

        return path;
    }

    private void Note(string text)
    {
        lock (_notesGate)
        {
            _notes.Add(text);
        }
    }

    private static string Shorten(string? url) =>
        url is null ? "" : url.Length <= 120 ? url : url[..120] + "…";

    // ───────────────────────── скрипты страницы ─────────────────────────

    private async Task<BrowserPage> ReadCoreAsync(int maxText, int maxElements, CancellationToken cancellationToken)
    {
        var core = _core ?? throw new InvalidOperationException("the window is closed.");
        var generation = ++_generation;
        var raw = await ScriptAsync(core, ReadScript
                .Replace("%GEN%", generation.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("%MAXTEXT%", maxText.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("%MAXEL%", maxElements.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
                cancellationToken)
            .ConfigureAwait(true);
        return ParsePage(raw) ?? new BrowserPage(core.Source ?? "", core.DocumentTitle ?? "", "", [], false, false);
    }

    private async Task<Located?> LocateAsync(string id, CancellationToken cancellationToken)
    {
        if (!SafeId().IsMatch(id))
        {
            return null;
        }

        var core = await EnsureAsync().ConfigureAwait(true);
        var raw = await ScriptAsync(core, LocateScript.Replace("%ID%", id, StringComparison.Ordinal), cancellationToken)
            .ConfigureAwait(true);
        if (Unwrap(raw) is not { } json)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new Located(ElementOf(root), root.GetProperty("x").GetDouble(), root.GetProperty("y").GetDouble());
    }

    private static string FocusScript(string id, bool replace) =>
        SafeId().IsMatch(id)
            ? $$"""
              (() => {
                const e = document.querySelector('[data-amarin-id="{{id}}"]');
                if (!e) return false;
                e.scrollIntoView({ block: 'center', inline: 'center' });
                e.focus();
                if (e.isContentEditable) {
                  const range = document.createRange();
                  range.selectNodeContents(e);
                  if (!{{(replace ? "true" : "false")}}) range.collapse(false);
                  const selection = window.getSelection();
                  selection.removeAllRanges();
                  selection.addRange(range);
                } else if (typeof e.setSelectionRange === 'function') {
                  try { {{(replace ? "e.select();" : "const end = (e.value || '').length; e.setSelectionRange(end, end);")}} } catch (_) {}
                }
                return true;
              })()
              """
            : "false";

    private static string SelectScript(string id, string option) =>
        SafeId().IsMatch(id)
            ? $$"""
              (() => {
                const e = document.querySelector('[data-amarin-id="{{id}}"]');
                if (!e || e.tagName !== 'SELECT') return false;
                const want = {{JsonSerializer.Serialize(option)}};
                const o = [...e.options].find(o => o.value === want || o.text.trim() === want);
                if (!o) return false;
                e.value = o.value;
                e.dispatchEvent(new Event('input', { bubbles: true }));
                e.dispatchEvent(new Event('change', { bubbles: true }));
                return true;
              })()
              """
            : "false";

    /// <summary>Id этого браузера: «b», номер чтения, «_», номер элемента — и ничего, что попало бы в скрипт.</summary>
    [GeneratedRegex(@"^b\d{1,6}_\d{1,5}$")]
    private static partial Regex SafeId();

    private const string Describe = """
        const describe = (e, id) => {
          const tag = e.tagName.toLowerCase();
          const type = (e.getAttribute('type') || '').toLowerCase();
          const role = e.getAttribute('role') || (tag === 'a' ? 'link' : tag === 'select' ? 'select'
            : tag === 'textarea' ? 'textbox' : tag === 'input'
              ? (['checkbox', 'radio', 'submit', 'button', 'reset', 'file'].includes(type) ? type : 'textbox')
              : tag);
          const name = (e.getAttribute('aria-label') || e.innerText || e.getAttribute('placeholder') ||
            e.getAttribute('title') || e.getAttribute('alt') || e.getAttribute('name') ||
            (type === 'submit' || type === 'button' ? e.value : '') || '').trim().replace(/\s+/g, ' ').slice(0, 120);
          const password = type === 'password' || (e.getAttribute('autocomplete') || '').includes('password');
          let value = null;
          if (!password && (tag === 'input' || tag === 'textarea')) {
            value = type === 'checkbox' || type === 'radio' ? (e.checked ? 'checked' : 'unchecked') : (e.value || '').slice(0, 120);
          }
          if (tag === 'select') value = e.options[e.selectedIndex] ? e.options[e.selectedIndex].text : '';
          return { id, tag, role, name, type: type || null, value, href: tag === 'a' ? e.href : null, password };
        };
        """;

    private const string ReadScript = """
        (() => {
        """ + Describe + """
          document.querySelectorAll('[data-amarin-id]').forEach(e => e.removeAttribute('data-amarin-id'));
          const visible = e => {
            const r = e.getBoundingClientRect();
            if (r.width === 0 && r.height === 0) return false;
            const s = getComputedStyle(e);
            return s.visibility !== 'hidden' && s.display !== 'none';
          };
          const selector = 'a[href],button,input,select,textarea,summary,[role=button],[role=link],[role=checkbox],' +
            '[role=radio],[role=tab],[role=menuitem],[role=option],[role=combobox],[role=textbox],[role=switch],[contenteditable=""],[contenteditable=true]';
          const elements = [];
          let more = false;
          let n = 0;
          for (const e of document.querySelectorAll(selector)) {
            if (e.disabled || (e.type || '').toLowerCase() === 'hidden' || !visible(e)) continue;
            if (elements.length >= %MAXEL%) { more = true; break; }
            const id = 'b%GEN%_' + (++n);
            e.setAttribute('data-amarin-id', id);
            elements.push(describe(e, id));
          }
          const text = (document.body ? document.body.innerText : '').replace(/\n{3,}/g, '\n\n').trim();
          return JSON.stringify({
            url: location.href, title: document.title, text: text.slice(0, %MAXTEXT%),
            textTruncated: text.length > %MAXTEXT%, elements, elementsTruncated: more
          });
        })()
        """;

    private const string LocateScript = """
        (() => {
        """ + Describe + """
          const e = document.querySelector('[data-amarin-id="%ID%"]');
          if (!e) return null;
          e.scrollIntoView({ block: 'center', inline: 'center' });
          const r = e.getBoundingClientRect();
          return JSON.stringify(Object.assign(describe(e, '%ID%'), { x: r.left + r.width / 2, y: r.top + r.height / 2 }));
        })()
        """;

    /// <summary>Ответ <c>ExecuteScriptAsync</c> — JSON значения; у нас это строка с JSON внутри.</summary>
    private static string? Unwrap(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BrowserPage? ParsePage(string raw)
    {
        if (Unwrap(raw) is not { } json)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var elements = root.GetProperty("elements").EnumerateArray().Select(ElementOf).ToList();
            return new BrowserPage(
                Text(root, "url") ?? "",
                Text(root, "title") ?? "",
                Text(root, "text") ?? "",
                elements,
                root.GetProperty("textTruncated").GetBoolean(),
                root.GetProperty("elementsTruncated").GetBoolean());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static BrowserElement ElementOf(JsonElement e) => new(
        Text(e, "id") ?? "",
        Text(e, "tag") ?? "",
        Text(e, "role") ?? "",
        Text(e, "name") ?? "",
        Text(e, "type"),
        Text(e, "value"),
        Text(e, "href"),
        e.TryGetProperty("password", out var password) && password.ValueKind == JsonValueKind.True);

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static (string Key, string Code, int KeyCode, string? Text) KeyOf(string key) => key switch
    {
        "enter" => ("Enter", "Enter", 13, "\r"),
        "tab" => ("Tab", "Tab", 9, null),
        "escape" => ("Escape", "Escape", 27, null),
        "backspace" => ("Backspace", "Backspace", 8, null),
        "delete" => ("Delete", "Delete", 46, null),
        "space" => (" ", "Space", 32, " "),
        "up" => ("ArrowUp", "ArrowUp", 38, null),
        "down" => ("ArrowDown", "ArrowDown", 40, null),
        "left" => ("ArrowLeft", "ArrowLeft", 37, null),
        "right" => ("ArrowRight", "ArrowRight", 39, null),
        "pageup" => ("PageUp", "PageUp", 33, null),
        "pagedown" => ("PageDown", "PageDown", 34, null),
        "home" => ("Home", "Home", 36, null),
        _ => ("End", "End", 35, null)
    };

    private sealed record Located(BrowserElement Element, double X, double Y);
}
