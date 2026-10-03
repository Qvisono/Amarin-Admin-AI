using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Картинка из ответа модели — блоком между абзацами, как блок кода. Источника два: строка
/// <c>data:</c> (так приходят нарисованные картинки) и обычный адрес http(s), который один раз
/// скачивается в фоне.
/// </summary>
internal static class ImageBlockView
{
    /// <summary>Наибольшая ширина картинки: шире она только теснит текст.</summary>
    private const double MaxWidth = 460;

    /// <summary>Потолок скачиваемой картинки: ошибочная ссылка не утянет огромный файл.</summary>
    private const int MaxRemoteBytes = 12 * 1024 * 1024;

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    // Идущий ответ перерисовывается около двенадцати раз в секунду, документ — заново целиком.
    // Без кэша каждая картинка разбиралась бы, а внешняя ещё и скачивалась бы на каждом проходе.
    // Ключ — адрес; значения — замороженные BitmapSource, делить их безопасно. Замка нет:
    // документ строится только на потоке интерфейса.
    private const int CacheCapacity = 32;
    private static readonly Dictionary<string, BitmapSource> Cache = [];
    private static readonly Queue<string> CacheOrder = new();

    /// <summary>
    /// Рамки, ждущие уже скачиваемый адрес. Повторная отрисовка того же ответа встаёт в очередь,
    /// а не шлёт второй запрос, — и картинку всё равно получает: обнови мы только рамку, начавшую
    /// загрузку, все следующие так и висели бы на «загрузке», когда перерисовка её заменит.
    /// </summary>
    private static readonly Dictionary<string, List<Border>> Pending = [];

    private static readonly Lazy<HttpClient> Http =
        new(() => HttpClients.Create(FetchTimeout, browserIdentity: true));

    public static FrameworkElement Create(FrameworkElement host, string url, string? alt)
    {
        var frame = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = MaxWidth,
            ToolTip = string.IsNullOrWhiteSpace(alt) ? url : alt
        };
        RoundedClip.SetRadius(frame, 8);
        frame.SetResourceReference(Border.BorderBrushProperty, "Border.Default");
        frame.SetResourceReference(Border.BackgroundProperty, "Bg.Card");
        AttachClick(host, frame, alt);

        if (Cache.TryGetValue(url, out var cached))
        {
            Fill(frame, cached, alt);
            return frame;
        }

        // Ссылка, которой модель ставит только что нарисованную картинку.
        if (ChatImageRegistry.IsHandle(url))
        {
            if (ChatImageRegistry.Find(url) is not { } attachment ||
                DecodeBase64(attachment.Base64) is not { } generated)
            {
                frame.Child = BuildNotice(string.IsNullOrWhiteSpace(alt)
                    ? Loc.Get("S.Image.Gone")
                    : Loc.Format("S.Image.GoneAlt", alt));
                return frame;
            }

            Remember(url, generated);
            Fill(frame, generated, alt);
            return frame;
        }

        if (TryDecodeDataUri(url) is { } inline)
        {
            Remember(url, inline);
            Fill(frame, inline, alt);
            return frame;
        }

        if (!IsFetchable(url, out var target))
        {
            // Показать нечего — остаётся подпись, чтобы слова автора не пропали из ответа.
            frame.Child = BuildNotice(string.IsNullOrWhiteSpace(alt) ? url : alt!);
            return frame;
        }

        var placeholder = BuildNotice(Loc.Get("S.Image.Loading"));
        frame.Child = placeholder;
        BeginFetch(host, frame, target, url, alt);
        return frame;
    }

    /// <summary>
    /// Ставит картинку в рамку; щелчок открывает её во весь размер. Битмап лежит в <c>Tag</c>
    /// рамки, потому что <see cref="BeginFetch"/> позже меняет ребёнка, и обработчик, замкнутый
    /// на построенный здесь элемент, показал бы устаревшую картинку.
    /// </summary>
    private static void Fill(Border frame, BitmapSource source, string? alt)
    {
        frame.Child = new Image
        {
            Source = source,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            // Фокус в тело сообщения не берёт — как и блок кода.
            Focusable = false
        };
        frame.Tag = source;
        frame.Cursor = System.Windows.Input.Cursors.Hand;
    }

    private static void AttachClick(FrameworkElement host, Border frame, string? alt)
    {
        // Тело сообщения — RichTextBox, то есть поверхность выделения: гасим нажатие, чтобы
        // протяжка по картинке не выделяла текст.
        frame.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
        frame.MouseLeftButtonUp += (_, e) =>
        {
            if (frame.Tag is BitmapSource current)
            {
                e.Handled = true;
                ImageViewerHost.Open(host, current, alt);
            }
        };
    }

    private static TextBlock BuildNotice(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 9, 12, 9)
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
        return block;
    }

    /// <summary>
    /// Скачивает в фоне и ставит картинку во все ждущие её рамки.
    /// </summary>
    private static void BeginFetch(FrameworkElement host, Border frame, Uri target, string url, string? alt)
    {
        if (Pending.TryGetValue(url, out var waiting))
        {
            waiting.Add(frame);
            return;
        }

        Pending[url] = [frame];

        _ = host.Dispatcher.InvokeAsync(async () =>
        {
            BitmapSource? decoded = null;
            try
            {
                decoded = await Task.Run(() => Download(target));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException or IOException)
            {
                // Сообщается заглушкой ниже.
            }

            Pending.Remove(url, out var frames);
            if (decoded is not null)
            {
                Remember(url, decoded);
            }

            foreach (var pending in frames ?? [])
            {
                if (decoded is not null)
                {
                    Fill(pending, decoded, alt);
                }
                else
                {
                    pending.Child = BuildNotice(string.IsNullOrWhiteSpace(alt)
                        ? Loc.Format("S.Image.Unavailable", url)
                        : Loc.Format("S.Image.UnavailableAlt", alt));
                }
            }
        });
    }

    private static BitmapSource? Download(Uri target)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        RemoteImages.ApplyBrowserHeaders(request, target);

        using var response = Http.Value.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        if (response.Content.Headers.ContentLength is > MaxRemoteBytes)
        {
            return null;
        }

        using var stream = response.Content.ReadAsStream();
        using var buffer = new MemoryStream();
        CopyCapped(stream, buffer);
        if (buffer.Length == 0)
        {
            return null;
        }

        buffer.Position = 0;
        return Decode(buffer);
    }

    /// <summary>Копирует не больше <see cref="MaxRemoteBytes"/> — для серверов, не называющих длину.</summary>
    private static void CopyCapped(Stream source, Stream destination)
    {
        var chunk = new byte[81920];
        var total = 0;
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > MaxRemoteBytes)
            {
                destination.SetLength(0);
                return;
            }

            destination.Write(chunk, 0, read);
        }
    }

    private static BitmapSource? Decode(Stream stream)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            // OnLoad — чтобы поток можно было сразу закрыть. IgnoreImageCache здесь нельзя: кэш
            // картинок WPF ведётся по адресу, и у источника без адреса просьба его обойти
            // бросает ArgumentNullException("key").
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or IOException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Разбирает <c>data:image/...;base64,...</c> — так приходят нарисованные картинки.</summary>
    private static BitmapSource? TryDecodeDataUri(string url)
    {
        const string Scheme = "data:";
        if (!url.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var comma = url.IndexOf(',');
        if (comma < 0)
        {
            return null;
        }

        var header = url[Scheme.Length..comma];
        if (!header.Contains("base64", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return DecodeBase64(url[(comma + 1)..]);
    }

    private static BitmapSource? DecodeBase64(string base64)
    {
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(base64));
            return Decode(stream);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Только http(s): ни file://, ни адресов этой машины и локальной сети. Адрес пришёл из ответа
    /// модели, поэтому правила — в одном общем месте.
    /// </summary>
    private static bool IsFetchable(string url, [NotNullWhen(true)] out Uri? target) =>
        RemoteImages.IsSafeTarget(url, out target);

    private static void Remember(string url, BitmapSource source)
    {
        if (Cache.ContainsKey(url))
        {
            return;
        }

        Cache[url] = source;
        CacheOrder.Enqueue(url);
        while (CacheOrder.Count > CacheCapacity)
        {
            Cache.Remove(CacheOrder.Dequeue());
        }
    }
}
