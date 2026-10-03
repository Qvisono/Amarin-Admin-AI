using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Amarin.Core;

/// <summary>
/// Общие правила скачивания картинки с произвольного сайта. Через них идут и лента чата, и
/// инструмент fetch_image — адрес проверяется, маскируется и разрешается в одном месте.
/// </summary>
internal static partial class RemoteImages
{
    /// <summary>Потолок скачиваемой картинки — тот же, что у ленты чата.</summary>
    public const int MaxBytes = 12 * 1024 * 1024;

    /// <summary>Сколько HTML-страницы читается в поисках тега og:image.</summary>
    public const int MaxHtmlBytes = 512 * 1024;

    /// <summary>
    /// Только http(s) и ничего, что указывает на эту машину или локальную сеть. Адрес выбирает
    /// модель, и скачивание без фильтра стало бы подделкой запросов (SSRF) в домашнюю сеть человека.
    /// </summary>
    public static bool IsSafeTarget(string? url, [NotNullWhen(true)] out Uri? target)
    {
        target = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsSafeTarget(uri))
        {
            return false;
        }

        target = uri;
        return true;
    }

    public static bool IsSafeTarget(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.DnsSafeHost;
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            return !IsPrivate(address);
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Имя без точки — только узел внутренней сети: у настоящего сайта точка есть всегда.
        return host.Contains('.');
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return IsPrivate(address.MapToIPv4());
            }

            var v6 = address.GetAddressBytes();
            return address.IsIPv6LinkLocal
                   || address.IsIPv6SiteLocal
                   || address.IsIPv6UniqueLocal
                   || (v6[0] & 0xFE) == 0xFC
                   || address.Equals(IPAddress.IPv6Any);
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return true;
        }

        var b = address.GetAddressBytes();
        return b[0] switch
        {
            0 => true,                                   // 0.0.0.0/8
            10 => true,                                  // 10.0.0.0/8
            127 => true,                                 // loopback
            169 when b[1] == 254 => true,                // link-local
            172 when b[1] >= 16 && b[1] <= 31 => true,   // 172.16.0.0/12
            192 when b[1] == 168 => true,                // 192.168.0.0/16
            100 when b[1] >= 64 && b[1] <= 127 => true,  // CGNAT
            >= 224 => true,                              // multicast and reserved
            _ => false
        };
    }

    /// <summary>
    /// Делает запрос похожим на вкладку браузера на самом сайте. Referer важен не меньше
    /// User-Agent: защита от чужих ссылок у CDN картинок без него отказывает.
    /// </summary>
    public static void ApplyBrowserHeaders(HttpRequestMessage request, Uri? referer = null)
    {
        var headers = request.Headers;
        headers.TryAddWithoutValidation("User-Agent", HttpClients.BrowserUserAgent);
        headers.TryAddWithoutValidation(
            "Accept",
            "image/avif,image/webp,image/apng,image/svg+xml,image/*,text/html;q=0.9,*/*;q=0.8");
        headers.TryAddWithoutValidation("Accept-Language", "ru,en;q=0.9");
        headers.TryAddWithoutValidation("Sec-Fetch-Dest", "image");
        headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
        headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");

        var origin = referer ?? request.RequestUri;
        if (origin is not null)
        {
            headers.TryAddWithoutValidation("Referer", origin.GetLeftPart(UriPartial.Authority) + "/");
        }
    }

    /// <summary>
    /// Находит картинку, которую страница заявляет о себе. Ссылка от человека почти всегда ведёт
    /// на страницу, а не на файл, — а сайты галерей кладут настоящий адрес картинки в og:image, и
    /// один переход превращает ссылку на страницу в картинку.
    /// </summary>
    public static string? ResolveImageFromHtml(string html, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        string? best = null;
        var bestRank = int.MaxValue;

        foreach (Match tag in MetaTag().Matches(html))
        {
            var attributes = tag.Groups[1].Value;
            var key = ReadAttribute(attributes, "property")
                      ?? ReadAttribute(attributes, "name")
                      ?? ReadAttribute(attributes, "itemprop");
            if (key is null)
            {
                continue;
            }

            var rank = key.Trim().ToLowerInvariant() switch
            {
                "og:image:secure_url" => 0,
                "og:image" or "og:image:url" => 1,
                "twitter:image" or "twitter:image:src" => 2,
                "image" => 3,
                _ => int.MaxValue
            };

            if (rank >= bestRank || ReadAttribute(attributes, "content") is not { } value)
            {
                continue;
            }

            if (Absolutise(value, baseUri) is { } resolved)
            {
                best = resolved;
                bestRank = rank;
            }
        }

        if (best is not null)
        {
            return best;
        }

        foreach (Match tag in LinkTag().Matches(html))
        {
            var attributes = tag.Groups[1].Value;
            var rel = ReadAttribute(attributes, "rel");
            if (rel is null || !rel.Contains("image_src", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ReadAttribute(attributes, "href") is { } href && Absolutise(href, baseUri) is { } resolved)
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? Absolutise(string value, Uri baseUri)
    {
        var text = WebUtility.HtmlDecode(value).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            text = baseUri.Scheme + ":" + text;
        }

        // Здесь решается только форма адреса. Можно ли его скачивать, решает вызывающий — у него
        // фильтр безопасности.
        return Uri.TryCreate(baseUri, text, out var absolute) &&
               (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            ? absolute.ToString()
            : null;
    }

    /// <summary>Достаёт атрибут из тела тега: в двойных, одинарных кавычках или без них.</summary>
    private static string? ReadAttribute(string attributes, string name)
    {
        var match = Regex.Match(
            attributes,
            Regex.Escape(name) + "\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s\"'>]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            return null;
        }

        return match.Groups[1].Success ? match.Groups[1].Value
            : match.Groups[2].Success ? match.Groups[2].Value
            : match.Groups[3].Value;
    }

    [GeneratedRegex("<meta\\s+([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaTag();

    [GeneratedRegex("<link\\s+([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkTag();
}
