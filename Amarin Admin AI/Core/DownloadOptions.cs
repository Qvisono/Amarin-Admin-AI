namespace Amarin.Core;

public sealed class DownloadOptions
{
    /// <summary>
    /// Trusted download hosts (and subdomains). Not a hard block: other hosts can still be
    /// downloaded after the user approves a High-risk confirmation dialog.
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