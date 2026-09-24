using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;
using Microsoft.Extensions.Configuration;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        CrashHandler.InstallProcessWide();

        var startup = StartupArgs.Parse(args);

        // Ожидание — до замка, иначе оно бессмысленно: ждём мы как раз того, кто замок держит.
        WaitForPreviousInstance(startup.AwaitExitPid);

        // Замок держим до конца процесса: программа на пользователя одна, иначе два экземпляра
        // дерутся за profiles.json и общий chats/index.json.
        using var single = SingleInstance.TryAcquire();
        var route = StartupRouter.Decide(startup, () => single.IsOwner);

        if (route == StartupRoute.SmokeTools)
        {
            return RunSmokeTools();
        }

        if (route == StartupRoute.ApplyUpdate)
        {
            // Единственная работа этого запуска — подменить файл и выйти. Ни окна, ни настроек,
            // ни чатов: процесс поднят через UAC, и делать под администратором что-то ещё он не
            // должен.
            return UpdateInstaller.ApplyElevated(startup.ApplyUpdateFrom, Environment.ProcessPath).Ok
                ? 0
                : 1;
        }

        if (route == StartupRoute.HandedOff)
        {
            // --model намеренно не передаём: он пишет модель в настройки всей программы, и
            // менять её у работающего окна из ярлыка за спиной пользователя хуже, чем не менять.
            SingleInstanceHandoff.Write(AppPaths.Root, startup.Prompt);
            SingleInstance.Activate();
            return 0;
        }

        try
        {
            return RunWpf(startup);
        }
        catch (Exception ex)
        {
            // Сбой до появления окна — битый profiles.json, недоступная папка настроек —
            // раньше закрывал программу молча, ещё до того как человек что-то увидел.
            CrashHandler.ReportStartupFailure(ex);
            return 1;
        }
    }

    /// <summary>
    /// Ждёт, пока прежний экземпляр отпустит замок. Так возвращается программа после обновления.
    /// </summary>
    /// <remarks>
    /// Старый процесс запускает новый и только потом закрывается — иначе, упав раньше, он не
    /// успел бы никого запустить. Замок при этом держится до конца процесса, и без ожидания
    /// новый экземпляр видит живого владельца, отдаёт ему запрос и выходит: человек остаётся
    /// вообще без окна. Потолок в полминуты на случай, если тот процесс завис: лучше поднять
    /// второе окно, чем не подняться совсем.
    /// </remarks>
    private static void WaitForPreviousInstance(int? pid)
    {
        if (pid is not { } id)
        {
            return;
        }

        try
        {
            using var previous = Process.GetProcessById(id);
            previous.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Процесса уже нет — ровно то, чего мы ждали.
        }
    }

    private static int RunWpf(StartupArgs startup)
    {
        // Замер до первого кадра. Останавливается руками перед app.Run: тот не вернётся до
        // закрытия программы, и using отмерил бы весь сеанс вместо запуска.
        var startupTimer = PerfLog.Measure("app_start");

        // До первого окна: OverrideMetadata внутри нельзя звать после того, как свойство
        // впервые прочитали.
        ToolTipDefaults.Apply();

        var configuration = BuildConfiguration();
        var downloadOptions = LoadDownloadOptions(configuration);

        // По ключу на провайдера: человек мог завести оба, и решать за него, какой из них
        // «настоящий», программа не вправе — выбор он делает на странице «Key & Info».
        var apiKey = ReadEnvironmentKey(configuration, "VENICE_API_KEY");
        var openRouterKey = ReadEnvironmentKey(configuration, "OPENROUTER_API_KEY");

        // Ключи уезжают в заголовок Authorization и в сообщения HTTP-исключений, а отчёт об
        // аварии человек пересылает — вырезаем их из отчёта. Список пополнится ключами со
        // страницы «Key & Info», как только станет известен профиль.
        CrashHandler.Secrets = [apiKey, openRouterKey];

        // Держатель активного ключа. Заводится здесь, а наполняется ниже, когда выбран профиль:
        // свои ключи у профиля свои, а AgentOptions раздаётся копиями и обязан смотреть на один
        // общий объект, иначе смена ключа не дошла бы до агента и служебных генераторов.
        var keys = new ApiKeyProvider(apiKey);

        // Собственный журнал трат. Заводится здесь и перекореняется ниже, когда выбран профиль:
        // в AgentOptions он должен попасть один раз, до того как настройки разойдутся копиями.
        var ledger = new SpendLedger(AppPaths.Root);
        var balances = new BalanceBook();

        var options = new AgentOptions
        {
            ApiKey = apiKey,
            Keys = keys,
            SpendSink = ledger.Record,
            BalanceSink = balances.Remember,
            BaseUrl = configuration["Venice:BaseUrl"] ?? "https://api.venice.ai/api/v1",
            Model = configuration["Venice:Model"] ?? "grok-4-6",
            MaxToolRounds = int.TryParse(configuration["Venice:MaxToolRounds"], out var rounds) ? rounds : 30,
            WebSearch = configuration["Venice:WebSearch"] ?? "off",
            EnableWebCitations = !bool.TryParse(configuration["Venice:EnableWebCitations"], out var citations) || citations,
            EnableXSearch = bool.TryParse(configuration["Venice:EnableXSearch"], out var xSearch) ? xSearch : null,
            Download = downloadOptions
        };

        // Profiles first: the active one decides which directory settings and chats come from.
        // The default profile maps to the original app root, so an upgrade never relocates
        // an existing conversation.
        var profileStore = new ProfileStore();
        var registry = profileStore.Load();
        var activeProfile = profileStore.Active(registry);
        var dataRoot = profileStore.DataRootFor(activeProfile.Id);

        // Explicit shutdown until the real window exists. WPF hands Application.MainWindow to the
        // first window created on this thread, which would be the lock screen — and under
        // OnMainWindowClose, closing it on a *correct* password would shut the app down before
        // MainWindow ever opened.
        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        CrashHandler.InstallUi(app);

        // До первого окна, включая экран входа: курсор, спрятанный полем пароля на время
        // набора, пропадал бы так же, как спрятанный композером. См. CursorGuard.
        CursorGuard.Install();

        // Тему берём из настроек активного профиля до всего остального: экран входа должен
        // выглядеть как приложение, а не как белый прямоугольник. Хранилище заводится одно на
        // запуск и переживает экран входа: раньше здесь стоял одноразовый экземпляр, и
        // settings.json успевал разобраться трижды, прежде чем окно показалось.
        var settingsStore = new AppSettingsStore(dataRoot);
        var settings = settingsStore.Load();
        ThemeManager.Initialize(app, settings.Theme);

        // Язык — до экрана входа: он тоже часть интерфейса и обязан быть на выбранном языке.
        LanguageManager.Initialize(app, settings.LanguageCode);

        // На экране входа можно выбрать другого пользователя, поэтому настройки читаются
        // только после него — у выбранного профиля своя папка с чатами и своим settings.json.
        if (activeProfile.IsLocked)
        {
            var unlocked = PasswordWindow.UnlockAtStartup(profileStore, registry);
            if (unlocked is null)
            {
                return 1;
            }

            if (!string.Equals(unlocked.Id, activeProfile.Id, StringComparison.Ordinal))
            {
                registry.ActiveProfileId = unlocked.Id;
                profileStore.Save(registry);
                activeProfile = unlocked;
                dataRoot = profileStore.DataRootFor(activeProfile.Id);

                // Вошли под другим пользователем — у него своя папка и свои настройки.
                settingsStore = new AppSettingsStore(dataRoot);
                settings = settingsStore.Load();
            }
        }

        // Фон считаем, пока WPF разбирает разметку окна: обои бывают на десятки мегапикселей,
        // и без этого человек успевал увидеть градиент-затычку прежде самой картинки.
        if (settings.Appearance is { Enabled: true, BackdropMode: BackdropMode.Image } appearance)
        {
            AppearanceImageCache.Prewarm(
                AppearanceImageCache.ResolvePath(appearance.BackgroundImagePath, dataRoot),
                appearance.ImageSaturation,
                appearance.ImageBlur,
                dataRoot);
        }

        // Только теперь известно, чей это профиль, — а ключи у каждого свои. До этой строки
        // программа работает на ключе из окружения, и так же она работает дальше, если своих
        // ключей человек не заводил.
        ledger.UseRoot(dataRoot);
        var keyStore = new ApiKeyStore(dataRoot, apiKey, openRouterKey);
        keyStore.Load();
        keys.Use(keyStore.ActiveCredential(), keyStore.VeniceCredential(), keyStore.Handles());
        CrashHandler.Secrets = keyStore.AllSecrets();
        ThemeManager.Apply(settings.Theme);
        LanguageManager.Apply(settings.LanguageCode);

        // The effective allowlist lives in settings.json; appsettings.json only seeded it.
        if (settings.DownloadAllowedDomains is null)
        {
            settings.DownloadAllowedDomains = [.. downloadOptions.AllowedDomains];
            settingsStore.Save(settings);
        }

        DownloadValidator.ConfigureAllowedDomains(settings.DownloadAllowedDomains);

        if (!string.IsNullOrWhiteSpace(startup.Model))
        {
            options.Model = startup.Model.Trim();
            settings.ChatModelId = options.Model;
            settingsStore.Save(settings);
        }
        else if (settingsStore.Exists &&
                 !string.IsNullOrWhiteSpace(settings.ChatModelId) &&
                 !settings.ChatModelId.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            options.Model = settings.ChatModelId;
        }

        var http = HttpClients.Create(TimeSpan.FromMinutes(5));
        var downloadHttp = HttpClients.Create(TimeSpan.FromMinutes(15), browserIdentity: true);
        var venice = new VeniceClient(http, options);
        var models = new VeniceModelListCache(venice, keys);
        venice.ResolveModelInfo = models.Find;
        var chatStore = new ChatStore(dataRoot);

        // Assigned just below. Everything that reads settings goes through the services bag so
        // that switching profiles re-roots the store for the engine, agent and title generator
        // too — capturing `settingsStore` directly would pin them to the profile seen at launch.
        AppServices? services = null;
        AppSettings ReadSettings() => services?.SettingsStore.Load() ?? settingsStore.Load();

        var confirmations = new ConfirmationQueue(ReadSettings);
        // Общий на программу: реестр нужен и хосту (записаться), и движку чата (остановить или
        // пересадить того, кто уже работает).
        var runningAgents = new AgentRegistry();
        var agentHost = new AgentHost(
            options, downloadHttp, ReadSettings, confirmations, runningAgents, models.Find);
        var chatTools = new ToolRegistry(
        [
            new ReadFileTool(),
            new WriteFileTool(),
            new WebSearchTool((query, ct) =>
                venice.SearchWebAsync(query, ModelSlots.WebSearch(ReadSettings()), ct)),
            // Aspect ratio is chosen from the pixel size for tool calls; only the infographic
            // flow asks for a specific ratio, and it calls the client directly.
            new GenerateImageTool((prompt, width, height, model, ct) =>
                venice.GenerateImageAsync(prompt, width, height, model, aspectRatio: null, ct)),
            new FetchImageTool(),
            new YouTubeTranscriptTool(),
            new InitAgentTool(new AgentSlotLimiter(), agentHost)
        ]);
        var engine = new ChatEngine(venice, options, ReadSettings, chatTools, runningAgents);
        var titles = new ChatTitleGenerator(http, options, ReadSettings);
        var summaries = new ChatSummaryGenerator(http, options, ReadSettings);

        services = new AppServices
        {
            Options = options,
            SettingsStore = settingsStore,
            Settings = settings,
            ChatStore = chatStore,
            Prompts = new PromptLibrary(dataRoot),
            KeyStore = keyStore,
            Ledger = ledger,
            Balances = balances,
            Keys = keys,
            EnvironmentKey = apiKey,
            OpenRouterEnvironmentKey = openRouterKey,
            Profiles = profileStore,
            ProfileRegistry = registry,
            Http = http,
            DownloadHttp = downloadHttp,
            Venice = venice,
            Models = models,
            Chat = engine,
            Titles = titles,
            Summaries = summaries,
            Confirmations = confirmations,
            StartupPrompt = startup.Prompt
        };

        var disposable = services;
        app.Exit += (_, _) => disposable.Dispose();

        var window = new MainWindow();
        window.AttachServices(services);

        // Строго здесь: AttachServices уже применил масштаб интерфейса (а тот приходит окну
        // поддельным WM_DPICHANGED и переписал бы выставленный размер), а Show() ещё не
        // случился — иначе окно мигнёт на экране прежними размерами.
        WindowGeometry.Restore(window, settings);

        // Claim the slot the lock screen may have taken, then restore the normal close-to-exit
        // behaviour now that the window it refers to is the one the user actually sees.
        app.MainWindow = window;
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;

        startupTimer.Dispose();
        return app.Run(window);
    }

    /// <summary>
    /// Ключ провайдера снаружи программы: переменная окружения, затем user-secrets. На диск
    /// программы он не переписывается — человек сознательно держал его снаружи.
    /// </summary>
    private static string ReadEnvironmentKey(IConfiguration configuration, string name) =>
        Environment.GetEnvironmentVariable(name) ?? configuration[name] ?? string.Empty;

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables()
            .Build();

    private static DownloadOptions LoadDownloadOptions(IConfiguration configuration)
    {
        var configuredDomains = configuration.GetSection("Download:AllowedDomains")
            .GetChildren()
            .Select(item => item.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();

        return new DownloadOptions
        {
            AllowedDomains = configuredDomains.Length > 0
                ? configuredDomains
                : new DownloadOptions().AllowedDomains,
            MaxSizeBytes = long.TryParse(configuration["Download:MaxSizeMb"], out var maxMb)
                ? maxMb * 1024L * 1024L
                : new DownloadOptions().MaxSizeBytes
        };
    }

    private static int RunSmokeTools()
    {
        AllocConsole();
        ConsoleEncoding.Configure();
        return RunToolSmokeTestAsync().GetAwaiter().GetResult();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    private static async Task<int> RunToolSmokeTestAsync()
    {
        Console.WriteLine("Amarin - smoke-test локальных инструментов (без Venice API)…");
        Console.WriteLine();

        var registry = new ToolRegistry(
        [
            new PowerShellTool(),
            new RegistryTool(),
            new ServiceTool(),
            new FileSystemTool(),
            new SystemInfoTool(),
            new ScreenshotTool(),
            new ClipboardTool(),
            new FolderAnalysisTool(),
            new EventLogTool(),
            new NetworkTool(),
            new ScheduledTaskTool(),
            new WmiTool(),
            new ProcessTool(),
            new VirtualizationTool(),
            new ReliabilityTool(),
            new WindowsUpdateTool(),
            new SecurityTool(),
            new DevicesTool(),
            new DnsConfigTool(),
            new PortListenerTool(),
            new RemoteAccessTool(),
            new ChangeRollbackTool(),
            new PerformanceTool(),
            new StartupProgramsTool(),
            new CredentialsTool(),
            new SystemRepairTool(),
            new RestorePointTool(),
            new DiskManagementTool(),
            new DiskSpaceTool(),
            new SoftwareInventoryTool(),
            new FirewallRulesTool(),
            new WindowsFeaturesTool(),
            new LocalUsersTool()
        ]);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var results = await ToolSmokeRunner.RunAsync(registry, cts.Token);

        var passed = 0;
        var failed = 0;

        foreach (var result in results)
        {
            var status = result.Success ? "OK" : "FAIL";
            if (result.Success)
            {
                passed++;
            }
            else
            {
                failed++;
            }

            Console.WriteLine($"[{status,-4}] {result.ToolName,-22} {result.ElapsedMs,5} ms  {result.Summary}");
        }

        Console.WriteLine();
        Console.WriteLine($"Итого: {passed} OK, {failed} FAIL из {results.Count}.");
        PerfLog.Write($"smoke_complete ok={passed} fail={failed} memory={GC.GetTotalMemory(false)}");
        return failed > 0 ? 1 : 0;
    }
}
