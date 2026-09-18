using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Brings a picture off the open web into the conversation. The caller mints a handle for whatever
/// comes back, so a fetched image both lands in the chat bubble and reaches the model as vision
/// input — the same path <see cref="GenerateImageTool"/> already uses for pictures it draws.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FetchImageTool : ITool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

    private readonly HttpClient _http;
    private readonly Func<Uri, bool> _allowTarget;

    public FetchImageTool(HttpClient? http = null, Func<Uri, bool>? allowTarget = null)
    {
        _http = http ?? HttpClients.Create(Timeout, browserIdentity: true);
        _allowTarget = allowTarget ?? RemoteImages.IsSafeTarget;
    }

    public string Name => "fetch_image";

    public string Description =>
        "Fetch a picture from a public http(s) URL and put it in the reply. Use it whenever the " +
        "user pastes a link to an image or to a page showing one (art sites, galleries, news, " +
        "wikis), and whenever you find such a link yourself - never paste a raw external URL and " +
        "hope it renders. A link to a submission or article page works too: the page's own preview " +
        "image is followed automatically. There is no domain allowlist here and nothing is saved " +
        "to disk. You get the picture back as a handle and you can see it, so you can describe " +
        "what is actually in it.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "url": {
              "type": "string",
              "description": "Direct image URL, or the URL of a page that shows the image"
            },
            "caption": {
              "type": "string",
              "description": "Short caption for the picture, in the user's language"
            }
          },
          "required": ["url"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("url", out var urlProp) ||
            urlProp.GetString() is not { } rawUrl ||
            string.IsNullOrWhiteSpace(rawUrl))
        {
            return ToolResult.Fail("Missing required parameter: url");
        }

        var caption = arguments.TryGetProperty("caption", out var captionProp)
            ? captionProp.GetString()
            : null;

        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var target) || !_allowTarget(target))
        {
            return ToolResult.Fail(
                "Эта ссылка не годится: нужен обычный публичный адрес http(s). " +
                "Локальные и внутрисетевые адреса не читаются.");
        }

        try
        {
            var fetched = await FetchAsync(target, followPage: true, cancellationToken);
            if (fetched is not { } payload)
            {
                return ToolResult.Fail(
                    $"По ссылке {target} нет картинки - страница не показывает изображение " +
                    "в открытом виде. Попробуй прямую ссылку на файл.");
            }

            var label = string.IsNullOrWhiteSpace(caption) ? payload.FileName : caption!.Trim();
            var attachment = ImageHelpers.FromBytes(payload.Bytes, payload.MimeType, label);
            return ToolResult.WithImages($"Изображение получено: {label}", [attachment]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Fail(Explain(ex, target));
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Не удалось скачать изображение: {ex.Message}");
        }
    }

    private sealed record Payload(byte[] Bytes, string MimeType, string FileName);

    private async Task<Payload?> FetchAsync(Uri target, bool followPage, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        RemoteImages.ApplyBrowserHeaders(request, target);

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode}",
                null,
                response.StatusCode);
        }

        var mime = response.Content.Headers.ContentType?.MediaType ?? "";

        if (mime.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
            mime.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase))
        {
            if (!followPage)
            {
                return null;
            }

            // One hop only: page -> the picture it advertises. Never a chain, so a hostile page
            // cannot walk this fetcher anywhere it likes.
            var html = await ReadCappedTextAsync(response, RemoteImages.MaxHtmlBytes, ct);
            var found = RemoteImages.ResolveImageFromHtml(html, response.RequestMessage?.RequestUri ?? target);
            if (found is null ||
                !Uri.TryCreate(found, UriKind.Absolute, out var picture) ||
                !_allowTarget(picture))
            {
                // A page pointing its preview at the LAN is exactly the attack the filter is for.
                return null;
            }

            return await FetchAsync(picture, followPage: false, ct);
        }

        if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (response.Content.Headers.ContentLength is > RemoteImages.MaxBytes)
        {
            throw new HttpRequestException(
                $"файл слишком большой ({response.Content.Headers.ContentLength / (1024 * 1024)} МБ)");
        }

        var bytes = await ReadCappedAsync(response, RemoteImages.MaxBytes, ct);
        if (bytes.Length == 0)
        {
            return null;
        }

        return new Payload(bytes, mime, FileNameFor(target));
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, int cap, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            total += read;
            if (total > cap)
            {
                throw new HttpRequestException($"файл больше {cap / (1024 * 1024)} МБ");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task<string> ReadCappedTextAsync(HttpResponseMessage response, int cap, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while (buffer.Length < cap && (read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string FileNameFor(Uri target)
    {
        var name = Path.GetFileName(target.AbsolutePath);
        return string.IsNullOrWhiteSpace(name) ? target.Host : WebUtility.UrlDecode(name);
    }

    /// <summary>
    /// Plain language, and deliberately no "stop trying" wording — a 403 on one URL is a good
    /// reason to try the direct file link, not a reason to give up on the user's request.
    /// </summary>
    private static string Explain(HttpRequestException ex, Uri target) => ex.StatusCode switch
    {
        HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
            $"{target.Host} не отдал файл без авторизации (HTTP {(int)ex.StatusCode!}). " +
            "Если есть прямая ссылка на сам файл картинки - попробуй её.",
        HttpStatusCode.NotFound =>
            $"По адресу {target} ничего нет (404).",
        HttpStatusCode.TooManyRequests =>
            $"{target.Host} ограничил частоту запросов (429). Попробуй ещё раз чуть позже.",
        _ => $"Не удалось скачать изображение с {target.Host}: {ex.Message}"
    };
}
