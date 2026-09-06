using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Amarin.Core;

/// <summary>
/// Shared rules for pulling a picture off an arbitrary website. Both the chat renderer and the
/// fetch_image tool go through here so a URL is vetted, disguised and resolved exactly once.
/// </summary>
internal static partial class RemoteImages
{
    /// <summary>Ceiling for a fetched picture, matching the chat renderer's own limit.</summary>
    public const int MaxBytes = 12 * 1024 * 1024;

    /// <summary>How much of an HTML page is read while hunting for its og:image tag.</summary>
    public const int MaxHtmlBytes = 512 * 1024;

    /// <summary>
    /// http(s) only, and nothing that points back inside this machine or the local network. The
    /// URL is chosen by the model, so an unfiltered fetch would be a server-side request forgery
    /// primitive pointed at the user's own LAN.
    /// </summary>
    public static bool IsSafeTarget(string? url, out Uri target)
    {
        target = null!;
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

        // A single-label name can only be an intranet host; a real site always has a dot.
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
    /// Makes the request look like a browser tab on the site itself. The Referer matters as much
    /// as the User-Agent: hotlink protection on image CDNs turns away requests without it.
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
    /// Finds the picture a page advertises about itself. A link a user pastes is almost always a
    /// submission page rather than a file — FurAffinity, DeviantArt, Reddit and the rest all put
    /// the real image URL in og:image, so following it once turns a page link into a picture.
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

        // Only the shape is settled here. Whether the address may actually be fetched is the
        // caller's call — it is the one holding the safety filter.
        return Uri.TryCreate(baseUri, text, out var absolute) &&
               (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            ? absolute.ToString()
            : null;
    }

    /// <summary>Reads one attribute out of a raw tag body, quoted with " or ' or bare.</summary>
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
