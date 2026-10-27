using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>
/// Встроенный браузер агента (1.37.0) для прогонов: окно ставит фабрику, когда оно готово.
/// </summary>
/// <remarks>
/// По образцу <see cref="ComputerHub"/>: без фабрики (тесты, консольный прогон инструментов)
/// агент инструмента <c>browser</c> не получает вовсе.
/// </remarks>
internal sealed class BrowserHub
{
    /// <summary>
    /// Папка профиля браузера в папке профиля программы: входы на сайты, куки, кэш. В архив не
    /// едет (входы привязаны к этой машине и шифруются ею), «Удалить все данные» её стирает.
    /// </summary>
    public const string FolderName = "browser";

    /// <summary>Создаёт окно браузера. Ставит окно программы; null — его нет.</summary>
    public Func<IWebBrowser>? Factory { get; set; }

    /// <summary>
    /// Кто сейчас держит браузер: окно одно, и два агента разом открывали бы страницы друг у
    /// друга. Тот же «держатель», что у мыши, но свой экземпляр: браузер и мышь не мешают друг другу.
    /// </summary>
    public ComputerLease Lease { get; init; } = new();

    public BrowserSession? Open(bool unattended) =>
        Factory is { } factory ? new BrowserSession(factory, Lease, unattended) : null;
}

/// <summary>
/// Браузер одного прогона агента: окно открывается при первом действии и закрывается с концом
/// прогона. Входы на сайты остаются в профиле браузера и пригодятся следующему.
/// </summary>
internal sealed class BrowserSession : IDisposable
{
    private readonly Func<IWebBrowser> _create;
    private readonly ComputerLease _lease;
    private IWebBrowser? _port;
    private bool _disposed;

    public BrowserSession(Func<IWebBrowser> create, ComputerLease lease, bool unattended)
    {
        _create = create;
        _lease = lease;
        Unattended = unattended;
    }

    /// <summary>Рядом нет человека (отложенная задача, расписание): <c>handoff</c> не ждёт никого.</summary>
    public bool Unattended { get; }

    /// <summary>Последняя прочитанная страница: её элементы описывают вызовы для SynGuard.</summary>
    public BrowserPage? Page { get; set; }

    /// <summary>
    /// Поле, которое агент последним нажал или заполнил, с тем, что в нём теперь: туда уйдёт
    /// клавиша <c>press</c>. Защитник видит его, как поле с фокусом у мыши.
    /// </summary>
    /// <remarks>
    /// До 1.37.0-beta.7 защитник не видел ни набранного текста (<c>type</c> страницу не
    /// перечитывает), ни того, куда уйдёт Enter, — и на «отправь» отвечал «не проверил» или
    /// «небезопасно», а человек получал вопрос о шаге, о котором сам и просил.
    /// </remarks>
    public BrowserElement? Focused { get; set; }

    private readonly Dictionary<string, int> _visits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Сколько раз за прогон открыта эта страница, считая это открытие.</summary>
    public int CountVisit(string url)
    {
        lock (_visits)
        {
            var count = _visits.GetValueOrDefault(url) + 1;
            _visits[url] = count;
            return count;
        }
    }

    /// <summary>Браузер для действия; при первом — берёт его у других агентов и открывает окно.</summary>
    public bool TryPort([NotNullWhen(true)] out IWebBrowser? port, [NotNullWhen(false)] out string? refusal)
    {
        port = null;
        refusal = null;
        if (_disposed)
        {
            refusal = BrowserSafety.ClosedByHuman;
            return false;
        }

        if (_port is null)
        {
            if (!_lease.TryTake(this))
            {
                refusal = BrowserSafety.Busy;
                return false;
            }

            try
            {
                _port = _create();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Окно не создалось (программа закрывается, поток окна занят): отказ модели, а не
                // авария всего прогона; браузер отдаётся другим агентам.
                _lease.Release(this);
                refusal = "The agent browser could not be opened: " + ex.Message + " Do the task without the browser.";
                return false;
            }
        }

        if (_port.ClosedByHuman)
        {
            refusal = BrowserSafety.ClosedByHuman;
            return false;
        }

        port = _port;
        return true;
    }

    /// <summary>
    /// Что увидит защитник рядом с вызовами браузера: адрес страницы и цели по id. Из уже
    /// прочитанного — без нового обращения к странице. Значение поля пароля не попадает никогда.
    /// </summary>
    public string? DescribeForGuard(IReadOnlyList<JsonElement> calls)
    {
        if (Page is not { } page)
        {
            return null;
        }

        var text = new StringBuilder();
        text.Append("Page: ").Append(page.Title).Append(" | ").Append(page.Url);
        if (Focused is { Password: false } focused)
        {
            text.Append("\nField the agent last clicked or typed into (key presses go there): ")
                .Append(focused.Role).Append(" \"").Append(focused.Name).Append('"');
            if (!string.IsNullOrEmpty(focused.Value))
            {
                text.Append(", value \"").Append(TextClip.Clip(focused.Value, FieldValueLimit, 0)).Append('"');
            }
        }

        foreach (var call in calls)
        {
            if (FileToolPaths.String(call, "element") is not { Length: > 0 } id ||
                page.Elements.FirstOrDefault(element => element.Id == id) is not { } read)
            {
                continue;
            }

            // Поле, которое агент только что заполнил, — с набранным, а не с прочитанным до ввода.
            var element = Focused is { } typed && typed.Id == id ? typed : read;

            text.Append("\nTarget ").Append(id).Append(": ").Append(element.Role).Append(" \"").Append(element.Name).Append('"');
            if (!element.Password && !string.IsNullOrEmpty(element.Value))
            {
                // Тот же предел, что у строки поля: набранное агентом ничем не ограничено, а длинное
                // письмо целиком раздуло бы промпт защитника.
                text.Append(", value \"").Append(TextClip.Clip(element.Value, FieldValueLimit, 0)).Append('"');
            }

            if (!string.IsNullOrEmpty(element.Href))
            {
                text.Append(" -> ").Append(element.Href);
            }
        }

        return text.ToString();
    }

    /// <summary>Сколько знаков значения поля видит защитник.</summary>
    private const int FieldValueLimit = 600;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _port?.Close();
        _lease.Release(this);
    }
}
