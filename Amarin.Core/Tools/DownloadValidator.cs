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

    /// <summary>
    /// Белый список выключен человеком (1.37.0, «Загрузка с любого сайта»): годится любой хост.
    /// </summary>
    /// <remarks>
    /// Статикой рядом со списком, по той же причине: список читают инструменты, разбор PowerShell
    /// и текст вопроса об опасном действии, и все они обязаны видеть одно и то же. Ставит
    /// <see cref="Configure"/> при запуске и смене профиля, а переключатель — сразу.
    /// </remarks>
    public static bool AllowAnySite { get; private set; }

    public static void ConfigureAnySite(bool allow) => AllowAnySite = allow;

    /// <summary>
    /// Список и «загрузка с любого сайта» — из настроек профиля, одним вызовом.
    /// </summary>
    /// <remarks>
    /// Зовут запуск и смена профиля. До этого смена профиля список не перечитывала, и до
    /// перезапуска программы загрузки шли по белому списку прежнего профиля.
    /// </remarks>
    public static void Configure(Core.AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ConfigureAllowedDomains(settings.DownloadAllowedDomains);
        ConfigureAnySite(settings.DownloadAnySite);
    }

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

        if (AllowAnySite)
        {
            return true;
        }

        return _allowedDomains.Any(domain =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }
}
