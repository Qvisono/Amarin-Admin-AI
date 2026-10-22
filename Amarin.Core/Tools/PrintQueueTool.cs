using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Очередь печати: принтеры и задания, отмена застрявших, перезапуск диспетчера печати,
/// принтер по умолчанию.
/// </summary>
/// <remarks>
/// Принтеры и их драйверы по-прежнему показывает <c>devices printers</c>; здесь — то, что
/// делают с застрявшей печатью.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PrintQueueTool : ITool
{
    public const string ToolName = "print_queue";

    private const string Spooler = "Spooler";

    public string Name => ToolName;

    public string Description =>
        "Print queue: list shows the printers, the default one, their status, offline state and job count, and " +
        "whether Windows picks the default printer itself; jobs lists print jobs with status and age and marks " +
        "stuck ones. cancel_job and clear_queue remove jobs, restart_spooler restarts the Print Spooler (purge also " +
        "deletes the spooler's queued job files while it is stopped), set_default makes a printer the default. " +
        "Changes need user confirmation.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "jobs", "cancel_job", "clear_queue", "restart_spooler", "set_default"],
              "description": "Print queue operation"
            },
            "printer": {
              "type": "string",
              "description": "Printer name exactly as list shows it; jobs without it lists every printer"
            },
            "job_id": {
              "type": "integer",
              "description": "cancel_job: the job Id from jobs"
            },
            "purge": {
              "type": "boolean",
              "description": "restart_spooler: also delete every queued job file while the spooler is stopped"
            }
          },
          "required": ["action"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var action = DangerousActionGuard.ActionOf(arguments);
        var printer = FileToolPaths.String(arguments, "printer")?.Trim();
        if (action is "cancel_job" or "clear_queue" or "set_default" && !PrintQueueCommands.IsPrinterName(printer))
        {
            return ToolResult.Fail("printer is required: the printer name exactly as list shows it.");
        }

        switch (action)
        {
            case "list":
                return Output(await PowerShellHelper.RunAsync(PrintQueueCommands.ListScript, 90, cancellationToken).ConfigureAwait(false));

            case "jobs":
                if (!string.IsNullOrEmpty(printer) && !PrintQueueCommands.IsPrinterName(printer))
                {
                    return ToolResult.Fail("printer must be a printer name as list shows it.");
                }

                return Output(await PowerShellHelper.RunAsync(PrintQueueCommands.JobsScript(printer), 90, cancellationToken).ConfigureAwait(false));

            case "cancel_job":
                if (FileToolPaths.Int(arguments, "job_id") is not { } job || job <= 0)
                {
                    return ToolResult.Fail("job_id is required: the job Id from jobs.");
                }

                var cancelled = await PowerShellHelper.RunAsync(PrintQueueCommands.CancelScript(printer!, job), 60, cancellationToken)
                    .ConfigureAwait(false);
                return Changed(cancelled, $"Job {job} removed from {printer}.");

            case "clear_queue":
                var cleared = await PowerShellHelper.RunAsync(PrintQueueCommands.ClearScript(printer!), 120, cancellationToken)
                    .ConfigureAwait(false);
                return Changed(cleared, $"Queue of {printer} cleared: {PowerShellHelper.ExtractStdout(cleared.Output).Replace("REMOVED", "", StringComparison.Ordinal).Trim()} jobs removed.");

            case "restart_spooler":
                return await Task.Run(() => RestartSpooler(FileToolPaths.Flag(arguments, "purge")), cancellationToken).ConfigureAwait(false);

            case "set_default":
                var result = await PowerShellHelper.RunAsync(PrintQueueCommands.DefaultScript(printer!), 60, cancellationToken)
                    .ConfigureAwait(false);
                var text = PowerShellHelper.ExtractStdout(result.Output);
                if (text.Contains("PRINTER_NOT_FOUND", StringComparison.Ordinal))
                {
                    return ToolResult.Fail(Loc.Format("S.Tool.Print.NotFound", printer));
                }

                return text.EndsWith("OK", StringComparison.Ordinal)
                    ? ToolResult.Ok($"{printer} is now the default printer; Windows no longer switches the default by itself.")
                    : Changed(result, "");

            default:
                return ToolResult.Fail($"Unknown action: {action}");
        }
    }

    private static ToolResult RestartSpooler(bool purge)
    {
        string? directory = null;
        if (purge)
        {
            directory = PrintQueueCommands.SpoolDirectory(
                PrintQueueCommands.ConfiguredSpoolDirectory(), Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (directory is null)
            {
                return ToolResult.Fail(Loc.Get("S.Tool.Print.SpoolOutside"));
            }
        }

        try
        {
            using var spooler = new ServiceController(Spooler);
            var dependents = spooler.DependentServices
                .Where(service => service.Status == ServiceControllerStatus.Running)
                .Select(service => service.ServiceName)
                .ToList();
            if (spooler.Status != ServiceControllerStatus.Stopped)
            {
                spooler.Stop();
                spooler.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            }

            var removed = directory is null ? 0 : PrintQueueCommands.Purge(directory);
            spooler.Start();
            spooler.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            foreach (var name in dependents)
            {
                using var service = new ServiceController(name);
                if (service.Status != ServiceControllerStatus.Running)
                {
                    service.Start();
                }
            }

            return ToolResult.Ok("Print Spooler restarted." + (directory is null ? "" : $" Queued job files deleted: {removed}."));
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 5 })
        {
            return ToolResult.Fail(Loc.Get("S.Tool.NeedsAdmin"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or UnauthorizedAccessException)
        {
            return ToolResult.Fail($"Print Spooler did not restart: {ex.Message}");
        }
    }

    private static ToolResult Output(ToolResult result) =>
        result.Success
            ? ToolResult.Ok(PowerShellHelper.ExtractStdout(result.Output))
            : PowerShellHelper.IsAccessDenied(result.Output) ? ToolResult.Fail(Loc.Get("S.Tool.NeedsAdmin")) : result;

    private static ToolResult Changed(ToolResult result, string success) =>
        result.Success && success.Length > 0
            ? ToolResult.Ok(success)
            : PowerShellHelper.IsAccessDenied(result.Output) ? ToolResult.Fail(Loc.Get("S.Tool.NeedsAdmin")) : ToolResult.Fail(result.Output);
}
