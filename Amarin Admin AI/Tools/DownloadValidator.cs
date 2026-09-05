namespace Amarin.Tools;

/// <summary>
/// URL checks for <c>download_file</c>.
/// AllowedDomains is enforced: a host outside the list is refused outright.
/// The list is user-managed (settings.json) and can legitimately be empty, which blocks everything.
/// </summary>
internal static class DownloadValidator
{
    private static string[] _allowedDomains = new Core.DownloadOptions().AllowedDomains;

    public static IReadOnlyList<string> AllowedDomains => _allowedDomains;

    public static void ConfigureAllowedDomains(IReadOnlyList<string>? domains)
    {
        _allowedDomains = domains is null
            ? new Core.DownloadOptions().AllowedDomains
            : domains
                .Select(d => Core.DomainList.Normalize(d))
                .Where(d => d is not null)
                .Select(d => d!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    /// <summary>
    /// Hard validation: scheme must be http or https. The host allowlist is checked separately
    /// by <see cref="IsDomainAllowed(Uri)"/> so the caller can report a distinct, actionable error.
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

        return _allowedDomains.Any(domain =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }
}
