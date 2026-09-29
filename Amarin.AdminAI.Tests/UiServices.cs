using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Службы окна на временной папке — для тестов, которым нужно своё окно с настоящим движком.
/// </summary>
/// <remarks>
/// Общее окно коллекции поднято на настройках человека из %APPDATA%, и всё, что тест сохранит
/// через него, легло бы в настоящий <c>settings.json</c>. Здесь всё пишется во временную папку.
/// </remarks>
internal static class UiServices
{
    public static AppServices Build(string root, string apiKey, HttpMessageHandler handler)
    {
        Directory.CreateDirectory(Path.Combine(root, "chats"));

        var options = new AgentOptions
        {
            ApiKey = apiKey,
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            MaxToolRounds = 2
        };

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var download = new HttpClient { BaseAddress = new Uri("https://example.invalid/") };
        var venice = new VeniceClient(http, options);
        var settingsStore = new AppSettingsStore(root);
        var settings = settingsStore.Load();

        return new AppServices
        {
            Options = options,
            SettingsStore = settingsStore,
            Settings = settings,
            ChatStore = new ChatStore(root),
            Prompts = new PromptLibrary(root),
            Instructions = new InstructionLibrary(root),
            KeyStore = new ApiKeyStore(root),
            Ledger = new SpendLedger(root),
            Keys = new ApiKeyProvider(),
            EnvironmentKey = "",
            Profiles = new ProfileStore(),
            ProfileRegistry = new ProfileRegistry(),
            Http = http,
            DownloadHttp = download,
            Venice = venice,
            Models = new VeniceModelListCache(venice),
            Balances = new BalanceBook(),
            Chat = new ChatEngine(venice, options, () => settings, new ToolRegistry([])),
            Titles = new ChatTitleGenerator(http, options, () => settings),
            Summaries = new ChatSummaryGenerator(http, options, () => settings),
            Confirmations = new ConfirmationQueue(() => settings)
        };
    }
}
