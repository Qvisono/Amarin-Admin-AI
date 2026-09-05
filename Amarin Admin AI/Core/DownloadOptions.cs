namespace Amarin.Core;

public sealed class DownloadOptions
{
    /// <summary>
    /// Seed for the user-managed allowlist in settings.json. Once seeded, this value is no longer
    /// consulted — the effective list is <see cref="AppSettings.DownloadAllowedDomains"/> and it is
    /// enforced: <c>download_file</c> refuses any host outside it.
    /// </summary>
    public string[] AllowedDomains { get; init; } =
    [
        "microsoft.com",
        "windows.com",
        "live.com",
        "github.com",
        "githubusercontent.com",
        "download.mozilla.org",
        "discord.com",
        "discordapp.com",
        "dl.discordapp.net",
        "discord.gg"
    ];

    public long MaxSizeBytes { get; init; } = 100 * 1024 * 1024;
}