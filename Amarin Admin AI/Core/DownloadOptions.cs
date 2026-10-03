namespace Amarin.Core;

public sealed class DownloadOptions
{
    /// <summary>
    /// Заготовка белого списка в settings.json. После засева больше не читается: действует
    /// <see cref="AppSettings.DownloadAllowedDomains"/>, и <c>download_file</c> отказывает любому
    /// домену вне его.
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