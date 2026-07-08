namespace Amarin.Core;

public sealed class DownloadOptions
{
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