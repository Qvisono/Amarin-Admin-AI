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

        var action = actionProp.GetString()?.Trim().ToLowerInvariant();
        return action switch
        {
            "status_sfc" => RunStatusSfc(cancellationToken),
            "status_dism" => PowerShellHelper.RunLongAsync(
                "DISM /Online /Cleanup-Image /CheckHealth 2>&1", StatusDismSeconds, cancellationToken),
            "run_sfc" => PowerShellHelper.RunLongAsync("sfc /scannow 2>&1", SfcSeconds, cancellationToken),
            "run_dism" => PowerShellHelper.RunLongAsync(
                "DISM /Online /Cleanup-Image /RestoreHealth 2>&1", DismSeconds, cancellationToken),
            _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
        };
    }

    /// <summary>
    /// Сколько ждать DISM /RestoreHealth. Предположение, а не замер: на медленном диске и при
    /// скачивании компонентов из Центра обновления он идёт дольше получаса, а оборванный на
    /// середине восстановление не доводит.
    /// </summary>
    internal const int DismSeconds = 3600;

    /// <summary>Сколько ждать SFC /scannow: десяти минут ему часто не хватает.</summary>
    internal const int SfcSeconds = 1800;

    private const int StatusDismSeconds = 300;

    /// <summary>Чтение: хвост CBS.log и отложенные операции — ничего не запускает.</summary>
    internal const string StatusSfcScript = """
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

    private static Task<ToolResult> RunStatusSfc(CancellationToken cancellationToken) =>
        PowerShellHelper.RunAsync(StatusSfcScript, 60, cancellationToken: cancellationToken);
}
