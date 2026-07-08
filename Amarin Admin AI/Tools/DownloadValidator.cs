namespace Amarin.Tools;

internal static class DownloadValidator
{
    public static bool TryValidateUrl(Uri uri, IReadOnlyList<string> allowedDomains, out string error)
    {
        error = string.Empty;

        if (uri.Scheme is not ("http" or "https"))
        {
            error = $"URL scheme not allowed: {uri.Scheme}";
            return false;
        }

        if (allowedDomains.Count == 0)
        {
            return true;
        }

        var host = uri.Host;
        var allowed = allowedDomains.Any(domain =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

        if (!allowed)
        {
            error = $"Domain not in whitelist: {host}. Allowed: {string.Join(", ", allowedDomains)}";
            return false;
        }

        return true;
    }
}