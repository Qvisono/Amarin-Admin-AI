using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateTool : ITool
{
    public string Name => "windows_update";
    public string Description =>
        "Windows Update: status, history, pending updates, whether a reboot is needed; install pending or " +
        "chosen KB updates, hide/unhide updates, pause updates for N days or resume. Never reboots by itself. " +
        "install, hide, unhide, pause and resume require user confirmation.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["status", "history", "pending", "reboot_required", "install", "hide", "unhide", "pause", "resume"],
              "description": "Windows Update operation"
            },
            "kb": {
              "type": "array",
              "items": { "type": "string" },
              "description": "KB numbers (KB5034441). install: empty means all pending updates; hide/unhide: required"
            },
            "days": {
              "type": "integer",
              "description": "pause: days to pause, 1-35"
            },
            "max_items": {
              "type": "integer",
              "description": "Max history entries (default 25, max 100)"
            }
          },
          "required": ["action"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("action", out var actionProp))
        {
            return Task.FromResult(ToolResult.Fail("Missing required parameter: action"));
        }

        var max = GetInt(arguments, "max_items", 25, 1, 100);
        var action = actionProp.GetString()?.Trim().ToLowerInvariant();
        switch (action)
        {
            case "install" or "hide" or "unhide":
                if (!UpdateCommands.TryKbList(KbArguments(arguments), out var kbs, out var kbError))
                {
                    return Task.FromResult(ToolResult.Fail(kbError + " Do not retry with the same value."));
                }

                if (action != "install" && kbs.Count == 0)
                {
                    return Task.FromResult(ToolResult.Fail($"{action} needs at least one KB number in kb."));
                }

                // Установка идёт минутами, а то и часом: долгая операция, с отменой хода.
                return action == "install"
                    ? PowerShellHelper.RunLongAsync(UpdateCommands.InstallScript(kbs), InstallSeconds, cancellationToken)
                    : PowerShellHelper.RunAsync(UpdateCommands.HideScript(kbs, hide: action == "hide"), 300,
                        cancellationToken);

            case "pause":
                return PowerShellHelper.RunAsync(
                    UpdateCommands.PauseScript(GetInt(arguments, "days", 7, 1, UpdateCommands.MaxPauseDays), DateTime.UtcNow),
                    60,
                    cancellationToken);

            case "resume":
                return PowerShellHelper.RunAsync(UpdateCommands.ResumeScript(), 60, cancellationToken);
        }

        var script = action switch
        {
            "status" => StatusScript(),
            "history" => HistoryScript(max),
            "pending" => PendingScript(),
            "reboot_required" => RebootScript(),
            _ => null
        };

        return script is null
            ? Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            : PowerShellHelper.RunAsync(script, 240, cancellationToken);
    }

    internal static string StatusScript() => """
        $s = New-Object -ComObject Microsoft.Update.Session
        $searcher = $s.CreateUpdateSearcher()
        Write-Output "Last scan: $($searcher.GetTotalHistoryCount()) history entries"
        $pending = $searcher.Search("IsInstalled=0 and Type='Software'").Updates
        Write-Output "Pending updates: $($pending.Count)"
        $pending | Select-Object Title, LastDeploymentChangeTime, IsDownloaded, IsMandatory | Format-Table -Wrap
        """;

    internal static string HistoryScript(int max) => $$"""
        $s = New-Object -ComObject Microsoft.Update.Session
        $searcher = $s.CreateUpdateSearcher()
        $count = $searcher.GetTotalHistoryCount()
        $start = [Math]::Max(0, $count - {{max}})
        $history = $searcher.QueryHistory(0, $count) | Select-Object -Skip $start
        $history | Select-Object Date, Title, ResultCode, HResult | Format-Table -Wrap
        """;

    internal static string PendingScript() => """
        $s = New-Object -ComObject Microsoft.Update.Session
        $searcher = $s.CreateUpdateSearcher()
        $pending = $searcher.Search("IsInstalled=0").Updates
        $pending | Select-Object Title, Description, IsDownloaded, IsMandatory, MaxDownloadSize | Format-List
        """;

    internal static string RebootScript() => """
        $reboot = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
        $cbs = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
        $wu = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\PostRebootReporting'
        [PSCustomObject]@{
          WindowsUpdateRebootRequired = $reboot
          CbsRebootPending = $cbs
          WUPostRebootReporting = $wu
          PendingFileRename = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue).PendingFileRenameOperations -ne $null
        } | Format-List
        """;

    /// <summary>Установка обновлений: загрузка и установка одного крупного — до часа.</summary>
    private const int InstallSeconds = 3600;

    private static IEnumerable<string?> KbArguments(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("kb", out var kb))
        {
            return [];
        }

        return kb.ValueKind switch
        {
            JsonValueKind.Array => kb.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString()),
            JsonValueKind.String => (kb.GetString() ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries),
            _ => []
        };
    }

    private static int GetInt(JsonElement args, string name, int defaultValue, int min, int max)
    {
        if (!args.TryGetProperty(name, out var prop) || !prop.TryGetInt32(out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }
}