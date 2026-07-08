using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class SystemRepairTool : ITool
{
    public string Name => "system_repair";
    public string Description =>
        "Windows system repair diagnostics: DISM/SFC health status and repair commands. " +
        "Actions: status_sfc, status_dism, run_sfc, run_dism.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["status_sfc", "status_dism", "run_sfc", "run_dism"],
              "description": "System repair action"
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

        var action = actionProp.GetString()?.ToLowerInvariant();
        return action switch
        {
            "status_sfc" => Task.FromResult(RunStatusSfc()),
            "status_dism" => Task.FromResult(RunStatusDism()),
            "run_sfc" => Task.FromResult(RunSfc()),
            "run_dism" => Task.FromResult(RunDism()),
            _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
        };
    }

    private static ToolResult RunStatusSfc()
    {
        var script = """
            $log = "$env:windir\Logs\CBS\CBS.log"
            if (Test-Path $log) {
              $last = Get-Content $log -Tail 30 -ErrorAction SilentlyContinue
              '=== CBS.log (last 30 lines) ==='
              $last
            } else { 'CBS.log not found.' }
            '=== Pending SFC ==='
            $pending = Test-Path "$env:windir\WinSxS\pending.xml"
            "Pending.xml exists: $pending"
            """;

        return PowerShellHelper.Run(script, 60);
    }

    private static ToolResult RunStatusDism()
    {
        return PowerShellHelper.Run("DISM /Online /Cleanup-Image /CheckHealth 2>&1", 300);
    }

    private static ToolResult RunSfc()
    {
        return PowerShellHelper.Run("sfc /scannow 2>&1", 600);
    }

    private static ToolResult RunDism()
    {
        return PowerShellHelper.Run("DISM /Online /Cleanup-Image /RestoreHealth 2>&1", 900);
    }
}