using Amarin.Core;

namespace Amarin.UI;

internal sealed class AppServices : IDisposable
{
    public required AgentOptions Options { get; init; }

    // Settable, not init-only: switching profiles re-roots both stores in place.
    public required AppSettingsStore SettingsStore { get; set; }

    public required AppSettings Settings { get; set; }

    public required ChatStore ChatStore { get; set; }

    public required ProfileStore Profiles { get; init; }

    public required ProfileRegistry ProfileRegistry { get; set; }

    public required HttpClient Http { get; init; }

    public required HttpClient DownloadHttp { get; init; }

    public required VeniceClient Venice { get; init; }

    public required VeniceModelListCache Models { get; init; }

    public required ChatEngine Chat { get; init; }

    public required ChatTitleGenerator Titles { get; init; }

    public required ChatSummaryGenerator Summaries { get; init; }

    public required ConfirmationQueue Confirmations { get; init; }

    public string? StartupPrompt { get; init; }

    public void ReloadSettings() => Settings = SettingsStore.Load();

    /// <summary>
    /// Points the settings and chat stores at another profile's directory. The engine keeps
    /// reading settings through the same <c>Func&lt;AppSettings&gt;</c>, so nothing else has
    /// to be rebuilt — but the caller must persist the current chat first.
    /// </summary>
    public void UseProfile(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        Directory.CreateDirectory(Path.Combine(dataRoot, "chats"));
        SettingsStore = new AppSettingsStore(dataRoot);
        ChatStore = new ChatStore(dataRoot);
        Settings = SettingsStore.Load();
    }

    public void Dispose()
    {
        Http.Dispose();
        DownloadHttp.Dispose();
    }
}
