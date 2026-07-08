using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateTool : ITool
{
    public string Name => "windows_update";
    public string Description =>
        "Windows Update status: pending reboot, update history, installed KB, optional available updates.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["status", "history", "pending", "reboot_required"],
              "description": "Windows Update operation"
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
        var action = actionProp.GetString()?.ToLowerInvariant();

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
            : Task.FromResult(PowerShellHelper.Run(script, 240));
    }

    private static string StatusScript() => """
        $s = New-Object -ComObject Microsoft.Update.Session
        $searcher = $s.CreateUpdateSearcher()
        Write-Output "Last scan: $($searcher.GetTotalHistoryCount()) history entries"
        $pending = $searcher.Search("IsInstalled=0 and Type='Software'").Updates
        Write-Output "Pending updates: $($pending.Count)"
        $pending | Select-Object Title, LastDeploymentChangeTime, IsDownloaded, IsMandatory | Format-Table -Wrap
        """;

    private static string HistoryScript(int max) => $$"""
        $s = New-Object -ComObject Microsoft.Update.Session
        $searcher = $s.CreateUpdateSearcher()
        $count = $searcher.GetTotalHistoryCount()
        $start = [Math]::Max(0, $count - {{max}})
        $history = $searcher.QueryHistory(0, $count) | Select-Object -Skip $start
        $history | Select-Object Date, Title, ResultCode, HResult | Format-Table -Wrap
        """;

    private static string PendingScript() => """
        $s = New-Object -ComObject Microsoft.Update.Session
        $searcher = $s.CreateUpdateSearcher()
        $pending = $searcher.Search("IsInstalled=0").Updates
        $pending | Select-Object Title, Description, IsDownloaded, IsMandatory, MaxDownloadSize | Format-List
        """;

    private static string RebootScript() => """
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

    private static int GetInt(JsonElement args, string name, int defaultValue, int min, int max)
    {
        if (!args.TryGetProperty(name, out var prop) || !prop.TryGetInt32(out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }
}