using System.Reflection;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;
using Microsoft.Extensions.Configuration;
using PdfSharp.Fonts;
using Spectre.Console;

ConsoleEncoding.Configure();
GlobalFontSettings.UseWindowsFontsUnderWindows = true;

// Config: non-secret settings from appsettings.json; API key ONLY from env / user-secrets.
// Never read Venice:ApiKey from committed JSON files.
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
    .AddEnvironmentVariables()
    .Build();

var configuredDomains = configuration.GetSection("Download:AllowedDomains")
    .GetChildren()
    .Select(item => item.Value)
    .Where(value => !string.IsNullOrWhiteSpace(value))
    .Select(value => value!)
    .ToArray();

var downloadOptions = new DownloadOptions
{
    AllowedDomains = configuredDomains.Length > 0
        ? configuredDomains
        : new DownloadOptions().AllowedDomains,
    MaxSizeBytes = long.TryParse(configuration["Download:MaxSizeMb"], out var maxMb)
        ? maxMb * 1024L * 1024L
        : new DownloadOptions().MaxSizeBytes
};

// Priority: process env > user-secrets/env via configuration (VENICE_API_KEY only).
var apiKey = Environment.GetEnvironmentVariable("VENICE_API_KEY")
    ?? configuration["VENICE_API_KEY"]
    ?? string.Empty;

var options = new AgentOptions
{
    ApiKey = apiKey,
    BaseUrl = configuration["Venice:BaseUrl"] ?? "https://api.venice.ai/api/v1",
    Model = configuration["Venice:Model"] ?? "grok-4-5",
    MaxToolRounds = int.TryParse(configuration["Venice:MaxToolRounds"], out var rounds) ? rounds : 30,
    WebSearch = configuration["Venice:WebSearch"] ?? "off",
    EnableWebCitations = !bool.TryParse(configuration["Venice:EnableWebCitations"], out var citations) || citations,
    EnableXSearch = bool.TryParse(configuration["Venice:EnableXSearch"], out var xSearch) ? xSearch : null,
    Download = downloadOptions
};

if (args.Contains("--smoke-tools", StringComparer.OrdinalIgnoreCase))
{
    return await RunToolSmokeTestAsync();
}



var ui = new ConsolePresenter();
ui.ShowBanner();

if (string.IsNullOrWhiteSpace(options.ApiKey))
{
    ui.ShowError("Не задан API-ключ Venice.ai.");
    ui.ShowInfo("Задайте переменную окружения VENICE_API_KEY (или: dotnet user-secrets set \"VENICE_API_KEY\" \"...\").");
    ui.ShowInfo("Ключ не хранится в appsettings.json и не коммитится в репозиторий.");
    return 1;
}

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
using var downloadHttp = new HttpClient(new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    EnableMultipleHttp2Connections = true
})
{
    Timeout = TimeSpan.FromMinutes(15)
};
var venice = new VeniceClient(http, options);

var toolRegistry = new ToolRegistry(
[
    new PowerShellTool(),
    new RegistryTool(),
    new ServiceTool(),
    new FileSystemTool(),
    new SystemInfoTool(),
    new WebDownloadTool(downloadHttp, downloadOptions),
    new ScreenshotTool(),
    new ClipboardTool(),
    new FolderAnalysisTool(),
    new AskUserTool(ui.PromptUserChoiceAsync),
    new WebSearchTool(venice.SearchWebAsync),
    new ScrapeUrlTool(venice.ScrapeUrlAsync),
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

var actionLog = new SessionActionLog();
var undoTracker = new SessionUndoTracker();
var reportCollector = new SessionReportCollector();
var agent = new Agent(venice, toolRegistry, options, ui, actionLog, undoTracker, reportCollector);

var sessionMode = SessionMode.Continuous;
if (SessionSettingsStore.TryLoad(out var savedMode))
{
    sessionMode = savedMode;
}
else
{
    sessionMode = await ui.PromptSessionModeAsync(SessionMode.Continuous);
    SessionSettingsStore.Save(sessionMode);
    ui.ShowInfo("Режим сохранён в appsettings.json.");
}

agent.SessionMode = sessionMode;

var prompt = new ConsolePrompt();

PrintStatusBar(ui, options.Model, agent, venice);

while (true)
{
    var userRequest = prompt.ReadLine();

    if (userRequest is null)
    {
        return 0;
    }

    if (string.IsNullOrWhiteSpace(userRequest))
    {
        continue;
    }

    if (userRequest.Equals("exit", StringComparison.OrdinalIgnoreCase))
    {
        ui.ShowInfo("До встречи.");
        return 0;
    }

    if (await HandleCommandAsync(userRequest))
    {
        continue;
    }

    using var requestCts = new CancellationTokenSource();
    ConsoleCancelEventHandler? cancelHandler = null;
    cancelHandler = (_, e) =>
    {
        e.Cancel = true;
        requestCts.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;

    try
    {
        await agent.RunAsync(userRequest, requestCts.Token);
        ui.ShowRequestCost(venice.RequestCost);
        ShowCachedBalance(venice, ui);
    }
    catch (OperationCanceledException)
    {
        ui.ShowWarning("Запрос отменён (Ctrl+C).");
    }
    catch (VeniceApiException ex)
    {
        ui.ShowError(ex.Message);
    }
    catch (Exception ex)
    {
        ui.ShowError($"Ошибка: {ex.Message}");
        if (ex.InnerException is not null)
        {
            ui.ShowInfo($"Подробности: {ex.InnerException.Message}");
        }
    }
    finally
    {
        if (cancelHandler is not null)
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    ConsoleInputRestore.Restore();
    Console.Out.Flush();
    ui.ShowSeparator();
}

async Task<bool> HandleCommandAsync(string userRequest)
{
    if (userRequest.Equals("/clear", StringComparison.OrdinalIgnoreCase))
    {
        agent.ClearSession();
        ui.ClearScreen();
        ui.ShowBanner();
        PrintStatusBar(ui, options.Model, agent, venice);
        ui.ShowInfo("История сессии и журнал действий очищены.");
        return true;
    }

    if (userRequest.Equals("/help", StringComparison.OrdinalIgnoreCase) ||
        userRequest.Equals("?", StringComparison.OrdinalIgnoreCase))
    {
        ui.ShowHelp();
        return true;
    }

    if (userRequest.Equals("/session", StringComparison.OrdinalIgnoreCase) ||
        userRequest.Equals("/mode", StringComparison.OrdinalIgnoreCase))
    {
        agent.SessionMode = await ui.PromptSessionModeAsync(agent.SessionMode);
        SessionSettingsStore.Save(agent.SessionMode);
        ui.ShowInfo($"Режим: {SessionModeParser.ToDisplayName(agent.SessionMode)} (сохранён)");
        return true;
    }

    if (userRequest.StartsWith("/model", StringComparison.OrdinalIgnoreCase))
    {
        var modelArg = userRequest.Length > "/model".Length
            ? userRequest["/model".Length..].Trim()
            : null;

        var newModel = string.IsNullOrWhiteSpace(modelArg)
            ? await ui.PromptModelAsync(
                options.Model,
                VeniceModelCatalog.GetSelectableModels(options.Model))
            : modelArg;

        venice.SetActiveModel(newModel);
        VeniceSettingsStore.SaveModel(newModel);
        PrintStatusBar(ui, options.Model, agent, venice);
        ui.ShowInfo($"Модель: {newModel} (сохранена в appsettings.json)");
        return true;
    }

    if (userRequest.Equals("/readonly", StringComparison.OrdinalIgnoreCase))
    {
        ToggleReadOnly(agent, ui);
        return true;
    }

    if (userRequest.Equals("/undo", StringComparison.OrdinalIgnoreCase))
    {
        if (!undoTracker.HasUndoPoint)
        {
            ui.ShowInfo("Нет точки отката — последний запрос не вносил изменений или откат уже выполнен.");
            return true;
        }

        var approved = await ui.ConfirmDangerousActionAsync(
            DangerousActionGuard.DescribeUndo(undoTracker.DescribeUndoPoint()));
        if (approved)
        {
            try
            {
                ui.ShowUndoResult(undoTracker.Undo());
            }
            catch (Exception ex)
            {
                // Compare/restore bugs must not tear down the process.
                ui.ShowError($"Откат не выполнен (внутренняя ошибка): {ex.Message}");
            }
        }
        else
        {
            ui.ShowInfo("Откат отменён.");
        }

        return true;
    }

    if (userRequest.StartsWith("/export", StringComparison.OrdinalIgnoreCase))
    {
        var formatArg = userRequest.Length > "/export".Length
            ? userRequest["/export".Length..].Trim()
            : null;

        if (!SessionReportExporter.TryParseFormat(formatArg, out var format))
        {
            ui.ShowWarning("Формат: /export [html|md|pdf|all]. По умолчанию — all.");
            return true;
        }

        var exportResult = SessionReportExporter.Export(
            format,
            reportCollector,
            actionLog,
            undoTracker,
            options.Model,
            agent.SessionMode,
            agent.ReadOnlyMode);

        ui.ShowExportResult(exportResult);
        return true;
    }

    if (userRequest.Equals("balance", StringComparison.OrdinalIgnoreCase))
    {
        ShowCachedBalance(venice, ui);
        return true;
    }

    if (userRequest.Equals("/history", StringComparison.OrdinalIgnoreCase))
    {
        ui.ShowSessionHistory(actionLog.Entries);
        return true;
    }

    return false;
}

static void PrintStatusBar(ConsolePresenter ui, string model, Agent agent, VeniceClient venice)
{
    ui.ShowStatusBar(
        model,
        agent.SessionMode,
        agent.ReadOnlyMode,
        agent.UndoTracker.HasUndoPoint,
        venice.LastBalance?.Format());
}

static void ToggleReadOnly(Agent agent, ConsolePresenter ui)
{
    agent.ReadOnlyMode = !agent.ReadOnlyMode;
    ui.ShowInfo(agent.ReadOnlyMode
        ? "Режим только диагностика включён — запись и опасный PowerShell заблокированы."
        : "Режим только диагностика выключен — доступны все инструменты (с подтверждением опасных действий).");
}

static void ShowCachedBalance(VeniceClient venice, ConsolePresenter ui)
{
    if (venice.LastBalance is null)
    {
        ui.ShowInfo("Баланс: появится после первого запроса к Venice.");
        return;
    }

    ui.ShowBalance(venice.LastBalance);
}

static async Task<int> RunToolSmokeTestAsync()
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
    return failed > 0 ? 1 : 0;
}