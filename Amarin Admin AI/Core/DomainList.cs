using System.Text.Json;

namespace Amarin.Core;

/// <summary>
/// Parsing and normalisation for the user-managed download allowlist.
/// Shared by the settings page, the "add domain" dialog and the confirmation guard.
/// </summary>
public static class DomainList
{
    /// <summary>Prefix of the <c>download_file</c> error the UI recognises as a blocked host.</summary>
    public const string BlockedMarker = "DOMAIN_BLOCKED:";

    /// <summary>
    /// Reduces free-form input to a bare host: strips scheme, credentials, path, query, port,
    /// a trailing dot and a leading <c>www.</c>. Returns <c>null</c> when nothing usable is left.
    /// </summary>
    public static string? Normalize(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            text = text[(scheme + 3)..];
        }

        var credentials = text.IndexOf('@');
        if (credentials >= 0)
        {
            text = text[(credentials + 1)..];
        }

        var cut = text.IndexOfAny(['/', '?', '#', '\\']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        // Port, but not an IPv6 literal.
        var colon = text.LastIndexOf(':');
        if (colon > 0 && !text.Contains(']', StringComparison.Ordinal))
        {
            text = text[..colon];
        }

        text = text.Trim().Trim('.').ToLowerInvariant();
        if (text.StartsWith("www.", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        return IsValidHost(text) ? text : null;
    }

    /// <summary>Adds a normalised host to <paramref name="list"/>, reporting why it was rejected.</summary>
    public static bool TryAdd(IList<string> list, string? input, out string error)
    {
        ArgumentNullException.ThrowIfNull(list);

        var domain = Normalize(input);
        if (domain is null)
        {
            error = "Введите домен, например github.com";
            return false;
        }

        if (list.Any(existing => string.Equals(existing, domain, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"Домен {domain} уже в списке";
            return false;
        }

        list.Add(domain);
        error = string.Empty;
        return true;
    }

    /// <summary>Reads the <c>url</c> argument of a <c>download_file</c> call and returns its host.</summary>
    public static bool TryGetHostFromToolArguments(string? argumentsJson, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("url", out var url) ||
                url.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (!Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                return false;
            }

            host = uri.Host;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidHost(string host)
    {
        if (host.Length is 0 or > 253 || host.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63 ||
                label.StartsWith('-') ||
                label.EndsWith('-'))
            {
                return false;
            }

            foreach (var c in label)
            {
                var ok = c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-' || c > 127;
                if (!ok)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
