using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Службы окна на временной папке — для тестов, которым нужно своё окно с настоящим движком.
/// </summary>
/// <remarks>
/// <para>
/// Общее окно коллекции поднято на настройках человека из %APPDATA%, и всё, что тест сохранит
/// через него, легло бы в настоящий <c>settings.json</c>. Здесь всё пишется во временную папку.
/// </para>
/// <para>
/// Один строитель на все тесты: до 1.30.0 ту же сборку переписывали руками ещё в четырёх местах,
/// и новое поле служб приходилось добавлять в пять копий. Граф урезан намеренно — без
/// инструментов, MCP и агентов, с настройками в памяти: исполнять настоящие инструменты по
/// ответу подставного сервера тестам незачем. Полный граф программы собирает
/// <c>AppComposition</c>, и его связи проверяет <c>AppCompositionTests</c>.
/// </para>
/// </remarks>
internal static class UiServices
{
    public static AppServices Build(string root, string apiKey, HttpMessageHandler handler) =>
        Build(
            root,
            new AgentOptions
            {
                ApiKey = apiKey,
                BaseUrl = "https://api.venice.ai/api/v1",
                Model = "grok-4-6",
                MaxToolRounds = 2
            },
            new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") });

    /// <param name="loadKeys">Прочитать <c>keys.json</c> папки, как это делает запуск.</param>
    public static AppServices Build(string root, AgentOptions options, HttpClient http, bool loadKeys = false)
    {
        Directory.CreateDirectory(Path.Combine(root, "chats"));

        var download = new HttpClient { BaseAddress = new Uri("https://example.invalid/") };
        var venice = new VeniceClient(http, options);
        var settingsStore = new AppSettingsStore(root);
        var settings = settingsStore.Load();
        var profile = new ProfileScope(
            root,
            settingsStore,
            settings,
            new ApiKeyProvider(),
            new SpendLedger(root),
            new InstructionLibrary(root));
        if (loadKeys)
        {
            profile.KeyStore.Load();
        }

        return new AppServices
        {
            Options = options,
            Profile = profile,
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
