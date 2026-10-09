using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Amarin.Composition;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

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
            return RunSmokeTools(startup.SmokeReportPath);
        }

        if (route == StartupRoute.ApplyUpdate)
        {
            // Единственная работа этого запуска — подменить файл и выйти. Ни окна, ни настроек,
            // ни чатов: процесс поднят через UAC, и делать под администратором что-то ещё он не
            // должен.
            var applied = startup.RollbackUpdate
                ? UpdateInstaller.RollBack(Environment.ProcessPath)
                : UpdateInstaller.ApplyElevated(startup.ApplyUpdateFrom, Environment.ProcessPath, startup.ApplyUpdateSha256);
            return applied.Ok ? 0 : 1;
        }

        if (route == StartupRoute.HandedOff)
        {
            // --model намеренно не передаём: он пишет модель в настройки всей программы, и
            // менять её у работающего окна из ярлыка за спиной пользователя хуже, чем не менять.
            // Автозапуск (--tray) и пробуждение к сроку (--wake) при уже работающей программе
            // делать нечего: окно поднимать незачем — человек его не просил, а задачи работающая
            // программа выполнит сама.
            if (startup.Action is StartupAction.Tray or StartupAction.Wake)
            {
                return 0;
            }

            SingleInstanceHandoff.Write(
                AppPaths.Root, startup.Prompt, startup.Send, startup.Action, startup.OpenChatId, startup.AskPath);
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

        // Первым делом: профиль покрывает только то, что скомпилировано после этой строки.
        StartupJit.Start(RuntimeContext.AppVersion);

        // До первого окна: OverrideMetadata внутри нельзя звать после того, как свойство
        // впервые прочитали.
        ToolTipDefaults.Apply();

        // «Удалить все данные» из прошлого запуска — раньше, чем прочитан хоть один файл
        // профиля. Без метки просто убирает залежавшуюся просьбу (см. PendingWipe).
        var wiped = PendingWipe.Run(AppPaths.Root, startup.WipeToken, DateTime.UtcNow);

        var configuration = AppConfiguration.Read();

        // Ключи уезжают в заголовок Authorization и в сообщения HTTP-исключений, а отчёт об
        // аварии человек пересылает — вырезаем их из отчёта. Список пополнится ключами со
        // страницы «Key & Info», как только станет известен профиль.
        var secrets = new SecretRegistry();
        secrets.Use([configuration.VeniceKey, configuration.OpenRouterKey]);
        CrashHandler.UseSecrets(secrets);

        // Сперва профиль: от него зависит, из какой папки читаются настройки и чаты. Профиль по
        // умолчанию смотрит в корень данных, поэтому обновление не уносит старые переписки.
        var profileStore = new ProfileStore();
        var registry = profileStore.Load();
        var activeProfile = profileStore.Active(registry);
        var dataRoot = profileStore.DataRootFor(activeProfile.Id);

        // Выход только явный, пока нет настоящего окна. WPF отдаёт Application.MainWindow первому
        // окну этого потока — экрану входа, — и при OnMainWindowClose его закрытие после *верного*
        // пароля гасило бы программу раньше, чем откроется главное окно.
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
        ThemeManager.FollowHighContrast = settings.FollowHighContrast;
        ThemeManager.Initialize(app, settings.Theme);

        // Язык — до экрана входа: он тоже часть интерфейса и обязан быть на выбранном языке.
        LanguageManager.Initialize(app, settings.LanguageCode);

        // На экране входа можно выбрать другого пользователя, поэтому настройки читаются
        // только после него — у выбранного профиля своя папка с чатами и своим settings.json.
        if (activeProfile.IsLocked)
        {
            var unlocked = PasswordWindow.UnlockAtStartup(profileStore, registry, woken: startup.Action == StartupAction.Wake);
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

        ThemeManager.FollowHighContrast = settings.FollowHighContrast;
        ThemeManager.Apply(settings.Theme);
        ChatFonts.Apply(settings);
        LanguageManager.Apply(settings.LanguageCode);

        // Всё, что зависит от профиля, — одним вызовом корня композиции.
        var services = AppComposition.Build(
            configuration,
            new StartupProfile(profileStore, registry, dataRoot, settingsStore, settings),
            startup,
            wiped,
            secrets);
        AppComposition.ApplyProcessWide(services);

        var disposable = services;
        app.Exit += (_, _) => disposable.Dispose();

        var window = new MainWindow();
        window.AttachServices(services);

        // Строго здесь: AttachServices уже применил масштаб интерфейса (а тот приходит окну
        // поддельным WM_DPICHANGED и переписал бы выставленный размер), а Show() ещё не
        // случился — иначе окно мигнёт на экране прежними размерами.
        WindowGeometry.Restore(window, settings);

        // Автозапуск (--tray, G3): сразу в трей. Без значка — обычный запуск: иначе окно было бы
        // недостижимо.
        if (startup.Action is StartupAction.Tray or StartupAction.Wake && settings.Windows is { ShowTrayIcon: true })
        {
            window.PrepareStartInTray();
        }
        else if (startup.Action == StartupAction.Wake)
        {
            // Без значка в трее прятать некуда: окно приходит свёрнутым и фокус не отнимает.
            window.PrepareQuietStart();
        }

        // Забираем место, которое мог занять экран входа, и возвращаем обычное «закрыл окно —
        // вышел»: теперь главное окно — то, которое человек видит.
        app.MainWindow = window;
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;

        startupTimer.Dispose();
        return app.Run(window);
    }

    /// <param name="reportPath">
    /// Файл для итога (<c>--smoke-report</c>): тогда консоль не открывается вовсе — её окно
    /// некому читать, а итог Markdown-таблицей уходит туда, откуда его возьмёт CI.
    /// </param>
    private static int RunSmokeTools(string? reportPath)
    {
        if (reportPath is null)
        {
            AllocConsole();
            ConsoleEncoding.Configure();
        }

        return RunToolSmokeTestAsync(reportPath).GetAwaiter().GetResult();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    private static async Task<int> RunToolSmokeTestAsync(string? reportPath)
    {
        if (reportPath is null)
        {
            Console.WriteLine("Amarin - smoke-test локальных инструментов (без Venice API)…");
            Console.WriteLine();
        }

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

        var exitCode = ToolSmokeRunner.ExitCode(results);
        PerfLog.Write($"smoke_complete {ToolSmokeRunner.Totals(results)} memory={GC.GetTotalMemory(false)}");

        if (reportPath is not null)
        {
            try
            {
                var full = Path.GetFullPath(reportPath);
                if (Path.GetDirectoryName(full) is { Length: > 0 } folder)
                {
                    Directory.CreateDirectory(folder);
                }

                await File.WriteAllTextAsync(full, ToolSmokeRunner.FormatReport(results), new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Некуда записать итог — прогон не удался, как бы ни прошли инструменты.
                return 2;
            }

            return exitCode;
        }

        foreach (var result in results)
        {
            Console.WriteLine($"[{result.Status,-4}] {result.ToolName,-22} {result.ElapsedMs,5} ms  {result.Summary}");
        }

        Console.WriteLine();
        Console.WriteLine(ToolSmokeRunner.Totals(results));
        return exitCode;
    }
}
