using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class ReliabilityTool : ITool
{
    public string Name => "reliability";
    public string Description =>
        "Windows Reliability Monitor and crash diagnostics: stability records, crash dumps, BSOD info.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["stability_records", "crash_dumps", "bsod_info", "recent_failures"],
              "description": "Reliability operation"
            },
            "days": {
              "type": "integer",
              "description": "Days of history (default 7, max 30)"
            },
            "max_items": {
              "type": "integer",
              "description": "Max records to return (default 20, max 50)"
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

        var days = GetInt(arguments, "days", 7, 1, 30);
        var maxItems = GetInt(arguments, "max_items", 20, 1, 50);

        var action = actionProp.GetString()?.ToLowerInvariant();
        var script = action switch
        {
            "stability_records" => StabilityScript(days, maxItems),
            "crash_dumps" => CrashDumpsScript(maxItems),
            "bsod_info" => BsodScript(),
            "recent_failures" => RecentFailuresScript(days, maxItems),
            _ => null
        };

        return script is null
            ? Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            : Task.FromResult(PowerShellHelper.Run(script, 180));
    }

    private static string StabilityScript(int days, int max) => $$"""
        $since = (Get-Date).AddDays(-{{days}})
        Get-WinEvent -FilterHashtable @{ LogName='Microsoft-Windows-Diagnostics-Performance/Operational'; StartTime=$since } -MaxEvents {{max}} -ErrorAction SilentlyContinue |
          Select-Object TimeCreated, Id, LevelDisplayName, Message |
          Format-List
        if (-not $?) {
          Get-WinEvent -LogName System -MaxEvents 200 -ErrorAction SilentlyContinue |
            Where-Object { $_.ProviderName -match 'Microsoft-Windows-WER|BugCheck|Application Error' -and $_.TimeCreated -ge $since } |
            Select-Object -First {{max}} TimeCreated, Id, ProviderName, Message |
            Format-List
        }
        """;

    private static string CrashDumpsScript(int max) => $$"""
        $paths = @(
          "$env:LOCALAPPDATA\CrashDumps",
          "$env:WINDIR\Minidump",
          "$env:PROGRAMDATA\Microsoft\Windows\WER"
        )
        foreach ($p in $paths) {
          if (Test-Path $p) {
            Write-Output "=== $p ==="
            Get-ChildItem $p -Recurse -File -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending |
              Select-Object -First {{max}} FullName, Length, LastWriteTime |
              Format-Table -AutoSize
          }
        }
        """;

    private static string BsodScript() => """
        $bug = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' -ErrorAction SilentlyContinue
        if ($bug) { $bug | Format-List }
        Get-WinEvent -FilterHashtable @{ LogName='System'; Id=1001 } -MaxEvents 10 -ErrorAction SilentlyContinue |
          Select-Object TimeCreated, Message | Format-List
        """;

    private static string RecentFailuresScript(int days, int max) => $$"""
        $since = (Get-Date).AddDays(-{{days}})
        $logs = @('Application','System')
        foreach ($log in $logs) {
          Write-Output "=== $log ==="
          Get-WinEvent -LogName $log -MaxEvents 500 -ErrorAction SilentlyContinue |
            Where-Object { $_.TimeCreated -ge $since -and $_.LevelDisplayName -in @('Error','Critical','Warning') -and
              ($_.ProviderName -match 'Application Error|Windows Error Reporting|BugCheck|Service Control Manager') } |
            Select-Object -First {{max}} TimeCreated, Id, ProviderName, LevelDisplayName, Message |
            Format-List
        }
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