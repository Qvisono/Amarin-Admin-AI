namespace Amarin.Tools;

/// <summary>
/// URL checks for <c>download_file</c>.
/// AllowedDomains is a trust list (lower risk in confirmation UI), not a hard block:
/// any http(s) host can be downloaded if the user approves the confirmation dialog.
/// </summary>
internal static class DownloadValidator
{
    private static string[] _allowedDomains = new Core.DownloadOptions().AllowedDomains;

    public static IReadOnlyList<string> AllowedDomains => _allowedDomains;

    public static void ConfigureAllowedDomains(IReadOnlyList<string> domains)
    {
        _allowedDomains = domains is { Count: > 0 }
            ? domains.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).ToArray()
            : new Core.DownloadOptions().AllowedDomains;
    }

    /// <summary>
    /// Hard validation: scheme must be http or https. Domain whitelist is NOT enforced here.
    /// </summary>
    public static bool TryValidateUrl(Uri uri, out string error)
    {
        error = string.Empty;

        if (uri.Scheme is not ("http" or "https"))
        {
            error = $"URL scheme not allowed: {uri.Scheme}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "URL has no host";
            return false;
        }

        return true;
    }

    /// <summary>Legacy overload kept for callers that still pass domains (domains ignored for hard-fail).</summary>
    public static bool TryValidateUrl(Uri uri, IReadOnlyList<string> allowedDomains, out string error) =>
        TryValidateUrl(uri, out error);

    public static bool IsDomainAllowed(Uri uri) =>
        IsDomainAllowed(uri.Host);

    public static bool IsDomainAllowed(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (_allowedDomains.Length == 0)
        {
            return true;
        }

        return _allowedDomains.Any(domain =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }
}
