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
        var startup = StartupArgs.Parse(args);
        if (startup.SmokeTools)
        {
            return RunSmokeTools();
        }

        return RunWpf(startup);
    }

    private static int RunWpf(StartupArgs startup)
    {
        var configuration = BuildConfiguration();
        var downloadOptions = LoadDownloadOptions(configuration);
        DownloadValidator.ConfigureAllowedDomains(downloadOptions.AllowedDomains);

        var apiKey = Environment.GetEnvironmentVariable("VENICE_API_KEY")
                     ?? configuration["VENICE_API_KEY"]
                     ?? string.Empty;

        var options = new AgentOptions
        {
            ApiKey = apiKey,
            BaseUrl = configuration["Venice:BaseUrl"] ?? "https://api.venice.ai/api/v1",
            Model = configuration["Venice:Model"] ?? "grok-4-6",
            MaxToolRounds = int.TryParse(configuration["Venice:MaxToolRounds"], out var rounds) ? rounds : 30,
            WebSearch = configuration["Venice:WebSearch"] ?? "off",
            EnableWebCitations = !bool.TryParse(configuration["Venice:EnableWebCitations"], out var citations) || citations,
            EnableXSearch = bool.TryParse(configuration["Venice:EnableXSearch"], out var xSearch) ? xSearch : null,
            Download = downloadOptions
        };

        var settingsStore = new AppSettingsStore();
        var settings = settingsStore.Load();
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
        var downloadHttp = HttpClients.Create(TimeSpan.FromMinutes(15));
        var venice = new VeniceClient(http, options);
        var models = new VeniceModelListCache(venice);
        var chatStore = new ChatStore();
        var confirmations = new ConfirmationQueue(() => settingsStore.Load());
        var agentHost = new AgentHost(options, downloadHttp, () => settingsStore.Load(), confirmations);
        var chatTools = new ToolRegistry(
        [
            new ReadFileTool(),
            new WriteFileTool(),
            new WebSearchTool(venice.SearchWebAsync),
            new InitAgentTool(new AgentSlotLimiter(), agentHost)
        ]);
        var engine = new ChatEngine(venice, options, () => settingsStore.Load(), chatTools);
        var titles = new ChatTitleGenerator(http, options, () => settingsStore.Load());

        var services = new AppServices
        {
            Options = options,
            SettingsStore = settingsStore,
            Settings = settings,
            ChatStore = chatStore,
            Http = http,
            DownloadHttp = downloadHttp,
            Venice = venice,
            Models = models,
            Chat = engine,
            Titles = titles,
            Confirmations = confirmations,
            StartupPrompt = startup.Prompt
        };

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        app.Exit += (_, _) => services.Dispose();

        var window = new MainWindow();
        window.AttachServices(services);
        return app.Run(window);
    }

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
        Console.WriteLine("Amarin — smoke-test локальных инструментов (без Venice API)…");
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
