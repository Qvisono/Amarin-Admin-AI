using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.Composition;

/// <summary>Профиль, под которым запускается программа: его выбирают до служб — на экране входа.</summary>
/// <param name="Store">Хранилище профилей (<c>profiles.json</c>).</param>
/// <param name="Registry">Прочитанный список профилей.</param>
/// <param name="DataRoot">Папка профиля.</param>
/// <param name="SettingsStore">Хранилище настроек этой папки — то, что уже прочитал запуск ради темы и языка.</param>
/// <param name="Settings">Прочитанные настройки.</param>
internal sealed record StartupProfile(
    ProfileStore Store,
    ProfileRegistry Registry,
    string DataRoot,
    AppSettingsStore SettingsStore,
    AppSettings Settings);

/// <summary>
/// Корень композиции: единственное место, где службы программы создаются и связываются.
/// </summary>
/// <remarks>
/// <para>
/// До 1.30.0 граф собирался посреди <c>Program.RunWpf</c>, вперемешку с темой, языком и экраном
/// входа, а тесты собирали свою копию руками в пяти местах. Теперь порядок до корня (компиляция
/// по профилю, отложенное стирание, конфигурация, профиль, тема, язык, вход, прогрев фона)
/// остаётся в <c>Program</c>, а всё, что от профиля зависит, собирается здесь одним вызовом.
/// </para>
/// <para>
/// Свой корень, а не контейнер (<c>Microsoft.Extensions.DependencyInjection</c>): служб около
/// сорока, время жизни у всех одно — до выхода, а профиль перекореняется на месте
/// (<see cref="ProfileScope.UseProfile"/>), чего контейнер не умеет. Ошибка зависимости здесь —
/// ошибка компиляции, а не исключение при запуске; ни отражения, ни лишней сборки на старте.
/// </para>
/// <para>
/// Общую на процесс статику (белый список загрузок, отметки MCP для шлюза) корень не трогает:
/// её ставит <see cref="ApplyProcessWide"/>, который зовёт только настоящий запуск. Поэтому граф
/// можно собрать в тесте без окна и проверить, что всё связано.
/// </para>
/// </remarks>
internal static class AppComposition
{
    /// <summary>Собирает службы программы под выбранный профиль.</summary>
    /// <param name="secrets">Что вырезать из отчётов о сбое; его же держит перехватчик аварий.</param>
    public static AppServices Build(
        AppConfiguration configuration,
        StartupProfile profile,
        StartupArgs startup,
        WipeResult? wiped,
        SecretRegistry secrets)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(secrets);

        var dataRoot = profile.DataRoot;
        var settingsStore = profile.SettingsStore;
        var settings = profile.Settings;

        // Держатель выбранного ключа, журнал трат, книга остатков и журнал аудита — по одному на
        // программу: AgentOptions раздаётся копиями, и все копии обязаны смотреть на одни и те
        // же объекты, иначе смена ключа или запись траты не дошли бы до агента и генераторов.
        var keys = new ApiKeyProvider(configuration.VeniceKey);
        var ledger = new SpendLedger(dataRoot);
        var balances = new BalanceBook();

        // Ключи из строк журнала вычищаются тем же списком, что и из отчётов о сбоях.
        var audit = new AuditLog(dataRoot, () => secrets.Current);

        // Инструкции пользователя — одна библиотека на программу: смена профиля переводит её на
        // другую папку, а движок, хост агентов и инструмент держат ту же ссылку.
        var instructions = new InstructionLibrary(dataRoot);

        // Серверы MCP (C11): свой HTTP-клиент — у них свои тайм-ауты и свои адресаты.
        var mcp = new McpHost(dataRoot, HttpClients.Create(TimeSpan.FromMinutes(2)));
        mcp.Refresh();

        var scope = new ProfileScope(
            dataRoot,
            settingsStore,
            settings,
            keys,
            ledger,
            instructions,
            configuration.VeniceKey,
            configuration.OpenRouterKey,
            audit,
            mcp,
            secrets);

        // Только теперь известно, чей это профиль, — а ключи у каждого свои. До этой строки
        // программа работает на ключе из окружения, и так же она работает дальше, если своих
        // ключей человек не заводил.
        scope.KeyStore.Load();
        scope.ApplyActiveKey();

        // Шифрование — через профиль, а не через локальные настройки: после смены профиля это
        // уже другой объект.
        audit.Encrypt = () => scope.Settings.EncryptChats;

        // Действующий белый список живёт в settings.json; appsettings.json его только засевает.
        if (settings.DownloadAllowedDomains is null)
        {
            settings.DownloadAllowedDomains = [.. configuration.Download.AllowedDomains];
            settingsStore.Save(settings);
        }

        // Лимиты трат читают настройки в памяти того профиля, что открыт сейчас.
        var spendGuard = new SpendGuard(() => scope.Settings, ledger);

        var options = new AgentOptions
        {
            ApiKey = configuration.VeniceKey,
            Keys = keys,
            // Записал цену — сразу сверил с порогом: предупреждение о лимите обязано прозвучать
            // на том запросе, что к нему подвёл, а не на следующем.
            SpendSink = (secret, cost, sku) =>
            {
                ledger.Record(secret, cost, sku);
                spendGuard.AfterSpend(secret);
            },
            SpendGate = spendGuard.CheckAsync,
            BalanceSink = balances.Remember,
            Audit = audit,
            BaseUrl = configuration.BaseUrl,
            Model = StartupModel(startup, settingsStore, settings, configuration.Model),
            MaxToolRounds = configuration.MaxToolRounds,
            WebSearch = configuration.WebSearch,
            EnableWebCitations = configuration.EnableWebCitations,
            EnableXSearch = configuration.EnableXSearch,
            Download = configuration.Download
        };

        var http = HttpClients.Create(TimeSpan.FromMinutes(5));
        var downloadHttp = HttpClients.Create(TimeSpan.FromMinutes(15), browserIdentity: true);
        var venice = new VeniceClient(http, options);
        var models = new VeniceModelListCache(venice, keys);
        venice.ResolveModelInfo = models.Find;

        // Всё, что читает настройки, читает их с диска того профиля, что открыт сейчас: смена
        // профиля переводит хранилище и для движка, агента и генераторов, а захваченный здесь
        // settingsStore навсегда привязал бы их к профилю, открытому при запуске.
        AppSettings ReadSettings() => scope.SettingsStore.Load();

        var confirmations = new ConfirmationQueue(ReadSettings);

        // Общий на программу: реестр нужен и хосту (записаться), и движку чата (остановить или
        // пересадить того, кто уже работает).
        var runningAgents = new AgentRegistry();
        var planReviews = new PlanReviewQueue();

        // Инструменты рецептов — те же, что у агента, плюс файловые чата: рецепт сохраняют из
        // журнала любого из них. Лениво и один раз — набор строится за заметное время, а рецепты
        // запускают редко.
        // Что модель прочла в каждом чате и какой файл правится сейчас — одно на все файловые
        // инструменты: штамп чтения из read_file проверяет edit_file, замок держат все пишущие.
        var fileState = new FileToolState();
        var recipeTools = new Lazy<ToolRegistry>(() => new ToolRegistry(
        [
            .. AgentTools.Create(
                venice,
                downloadHttp,
                options.Download,
                knownSecrets: () => (options.Keys?.Keys.Select(key => (string?)key.Secret) ?? []).Append(options.ApiKey)).All,
            .. FileTools(fileState)
        ]));
        var agentHost = new AgentHost(
            options, downloadHttp, ReadSettings, confirmations, runningAgents, models.Find, instructions, planReviews,
            mcp.Tools);
        var chatTools = new ToolRegistry(
        [
            .. FileTools(fileState),
            new WebSearchTool((query, ct) =>
                venice.SearchWebAsync(query, ModelSlots.WebSearch(ReadSettings()), ct)),
            // Соотношение сторон у вызова инструмента выводится из размера в пикселях; особое
            // просит только инфографика, а она зовёт клиент напрямую.
            new GenerateImageTool((prompt, width, height, model, ct) =>
                venice.GenerateImageAsync(prompt, width, height, model, aspectRatio: null, ct)),
            new FetchImageTool(),
            new YouTubeTranscriptTool(),
            new InitAgentTool(new AgentSlotLimiter(), agentHost),
            new ReadInstructionTool(instructions),
            new DeferredTaskTool(scope.Deferred)
        ]);

        return new AppServices
        {
            Options = options,
            Profile = scope,
            SpendGuard = spendGuard,
            Balances = balances,
            Profiles = profile.Store,
            ProfileRegistry = profile.Registry,
            Http = http,
            DownloadHttp = downloadHttp,
            Venice = venice,
            Models = models,
            Chat = new ChatEngine(venice, options, ReadSettings, chatTools, runningAgents, instructions, confirmations)
            {
                Files = fileState
            },
            Titles = new ChatTitleGenerator(http, options, ReadSettings),
            Summaries = new ChatSummaryGenerator(http, options, ReadSettings),
            Confirmations = confirmations,
            PlanReviews = planReviews,
            AgentHost = agentHost,
            RecipeRunner = new RecipeRunner(() => recipeTools.Value, confirmations, ReadSettings, () => options.Audit),
            RunTools = () => recipeTools.Value,
            StartupPrompt = startup.Prompt,
            StartupSend = startup.ShouldSend,
            StartupWipe = wiped,
            StartupChatId = startup.OpenChatId,
            StartupAction = startup.Action,
            StartupAskPath = startup.AskPath
        };
    }

    /// <summary>
    /// Файлы и документы чата: чтение, запись, правка, папки, документы и картинки. Один набор на
    /// чат и рецепты — рецепт сохраняют из журнала вызовов чата, и инструменты там должны быть те же.
    /// </summary>
    internal static ITool[] FileTools(FileToolState state) =>
    [
        new ReadFileTool(state),
        new WriteFileTool(state),
        new EditFileTool(state),
        new CreateFolderTool(),
        new CreateDocumentTool(state),
        new EditDocumentTool(state),
        new SaveImageTool()
    ];

    /// <summary>
    /// Общая на процесс статика, которая смотрит на собранные службы: белый список загрузок и
    /// отметки «только чтение» у инструментов MCP. Её читают инструменты и шлюз, переписывать
    /// которые нельзя, поэтому она остаётся статикой — но ставится здесь, в одном месте.
    /// </summary>
    public static void ApplyProcessWide(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        DownloadValidator.ConfigureAllowedDomains(services.Settings.DownloadAllowedDomains);
        if (services.Mcp is { } mcp)
        {
            ToolGate.McpReadOnly = name => mcp.ReadOnlyNames.Contains(name);
        }
    }

    /// <summary>
    /// Модель по умолчанию: из <c>--model</c> (и тогда она же записывается в настройки), иначе
    /// выбранная в настройках — если файл настроек уже был и там не «Авто», — иначе из конфигурации.
    /// </summary>
    private static string StartupModel(StartupArgs startup, AppSettingsStore settingsStore, AppSettings settings, string configured)
    {
        if (!string.IsNullOrWhiteSpace(startup.Model))
        {
            var model = startup.Model.Trim();
            settings.ChatModelId = model;
            settingsStore.Save(settings);
            return model;
        }

        return settingsStore.Exists &&
               !string.IsNullOrWhiteSpace(settings.ChatModelId) &&
               !settings.ChatModelId.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? settings.ChatModelId
            : configured;
    }
}
