using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// An image the assistant put in its answer, rendered inline between paragraphs the way a code
/// block is. Two sources are understood: a <c>data:</c> URI, which is what generated images
/// arrive as, and an ordinary http(s) URL, fetched once in the background.
/// </summary>
internal static class ImageBlockView
{
    /// <summary>Widest the picture is drawn. Beyond this it just crowds the text column.</summary>
    private const double MaxWidth = 460;

    /// <summary>Ceiling on a fetched image, so a mistyped link cannot pull down a huge file.</summary>
    private const int MaxRemoteBytes = 12 * 1024 * 1024;

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    // The live answer re-renders roughly twelve times a second, rebuilding the whole document
    // each pass. Without this every image would be re-decoded — and every remote one re-fetched —
    // on each tick. Keyed by URL; entries are frozen BitmapSources, so sharing them is safe.
    // No lock: the document is only ever built on the UI thread.
    private const int CacheCapacity = 32;
    private static readonly Dictionary<string, BitmapSource> Cache = [];
    private static readonly Queue<string> CacheOrder = new();

    /// <summary>
    /// Frames waiting on a URL that is already being fetched. A second render of the same answer
    /// joins the queue instead of starting another request — and, importantly, still gets the
    /// picture: updating only the frame that started the fetch would strand every later one on
    /// "loading" when the live re-render replaced it mid-flight.
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

        // A handle the assistant wrote to place an image it just generated.
        if (ChatImageRegistry.IsHandle(url))
        {
            if (ChatImageRegistry.Find(url) is not { } attachment ||
                DecodeBase64(attachment.Base64) is not { } generated)
            {
                frame.Child = BuildNotice(string.IsNullOrWhiteSpace(alt)
                    ? "Изображение больше недоступно."
                    : $"{alt} (изображение больше недоступно)");
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
            // Not something we can show — fall back to the caption, which at least keeps the
            // author's words in the answer.
            frame.Child = BuildNotice(string.IsNullOrWhiteSpace(alt) ? url : alt!);
            return frame;
        }

        var placeholder = BuildNotice("Загрузка изображения…");
        frame.Child = placeholder;
        BeginFetch(host, frame, target, url, alt);
        return frame;
    }

    /// <summary>
    /// Fills the frame with the picture and makes it open full size on click. The bitmap is
    /// stashed on the frame's Tag because <see cref="BeginFetch"/> replaces the child later —
    /// a handler that closed over the element built here would show a stale image.
    /// </summary>
    private static void Fill(Border frame, BitmapSource source, string? alt)
    {
        frame.Child = new Image
        {
            Source = source,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            // Keep focus out of the message body, the same way a code block does.
            Focusable = false
        };
        frame.Tag = source;
        frame.Cursor = System.Windows.Input.Cursors.Hand;
    }

    private static void AttachClick(FrameworkElement host, Border frame, string? alt)
    {
        // The body is a RichTextBox, i.e. a text-selection surface: swallow the press so a
        // drag across the picture is not read as selecting text.
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
    /// Fetches on a background thread and swaps the picture in when it lands. The frame is only
    /// updated if it is still in the visual tree — a re-render may have replaced it meanwhile.
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
                // Reported through the placeholder below.
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
                        ? "Изображение недоступно: " + url
                        : $"{alt} (изображение недоступно)");
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

    /// <summary>Copies at most <see cref="MaxRemoteBytes"/>, for servers that send no length.</summary>
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
            // OnLoad so the stream can be closed straight after. Note: no IgnoreImageCache here —
            // WPF's image cache is keyed by URI, and asking it to bypass a cache for a source
            // that has no URI throws ArgumentNullException("key").
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

    /// <summary>Decodes <c>data:image/...;base64,...</c>, which is how generated images arrive.</summary>
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
    /// http(s) only — no file://, and nothing pointing back at this machine or the local network.
    /// The URL comes out of a model's answer, so the rules live in one shared place.
    /// </summary>
    private static bool IsFetchable(string url, out Uri target) =>
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
