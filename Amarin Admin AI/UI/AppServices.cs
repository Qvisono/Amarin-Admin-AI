using Amarin.Core;

namespace Amarin.UI;

internal sealed class AppServices : IDisposable
{
    public required AgentOptions Options { get; init; }

    public required AppSettingsStore SettingsStore { get; init; }

    public required AppSettings Settings { get; set; }

    public required ChatStore ChatStore { get; init; }

    public required HttpClient Http { get; init; }

    public required HttpClient DownloadHttp { get; init; }

    public required VeniceClient Venice { get; init; }

    public required VeniceModelListCache Models { get; init; }

    public required ChatEngine Chat { get; init; }

    public required ChatTitleGenerator Titles { get; init; }

    public required ConfirmationQueue Confirmations { get; init; }

    public string? StartupPrompt { get; init; }

    public void ReloadSettings() => Settings = SettingsStore.Load();

    public void Dispose()
    {
        Http.Dispose();
        DownloadHttp.Dispose();
    }
}
