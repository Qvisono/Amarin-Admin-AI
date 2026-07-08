using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class SecurityTool : ITool
{
    public string Name => "security_status";
    public string Description =>
        "Windows Firewall and Microsoft Defender status, rules summary, threats, scan state. Read-only.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": [
                "firewall_status", "firewall_rules", "defender_status",
                "defender_threats", "defender_preferences"
              ],
              "description": "Security diagnostic action"
            },
            "profile": {
              "type": "string",
              "enum": ["domain", "private", "public", "all"],
              "description": "Firewall profile filter"
            },
            "max_rules": {
              "type": "integer",
              "description": "Max firewall rules (default 40, max 150)"
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

        var maxRules = GetInt(arguments, "max_rules", 40, 1, 150);
        var profile = arguments.TryGetProperty("profile", out var profileProp) &&
                      profileProp.ValueKind == JsonValueKind.String
            ? profileProp.GetString() ?? "all"
            : "all";

        var action = actionProp.GetString()?.ToLowerInvariant();
        var script = action switch
        {
            "firewall_status" => FirewallStatusScript(),
            "firewall_rules" => FirewallRulesScript(profile, maxRules),
            "defender_status" => DefenderStatusScript(),
            "defender_threats" => DefenderThreatsScript(),
            "defender_preferences" => DefenderPreferencesScript(),
            _ => null
        };

        return script is null
            ? Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            : Task.FromResult(PowerShellHelper.Run(script, 180));
    }

    private static string FirewallStatusScript() => """
        Get-NetFirewallProfile | Select-Object Name, Enabled, DefaultInboundAction, DefaultOutboundAction | Format-Table -AutoSize
        netsh advfirewall show allprofiles
        """;

    private static string FirewallRulesScript(string profile, int max) => $$"""
        $rules = Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -eq 'True' }
        if ('{{profile}}' -ne 'all') { $rules = $rules | Where-Object { $_.Profile -match '{{profile}}' } }
        $rules | Select-Object -First {{max}} DisplayName, Direction, Action, Profile |
          ForEach-Object {
            $port = (Get-NetFirewallPortFilter -AssociatedNetFirewallRule $_ -ErrorAction SilentlyContinue).LocalPort
            [PSCustomObject]@{ $_.DisplayName; $_.Direction; $_.Action; Ports=$port }
          } | Format-Table -Wrap
        """;

    private static string DefenderStatusScript() => """
        Get-MpComputerStatus -ErrorAction SilentlyContinue | Format-List
        if (-not $?) { & "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" -GetFiles 2>$null }
        """;

    private static string DefenderThreatsScript() => """
        Get-MpThreatDetection -ErrorAction SilentlyContinue | Select-Object -First 30 ThreatName, Resources, InitialDetectionTime, ActionSuccess | Format-List
        Get-MpThreat -ErrorAction SilentlyContinue | Select-Object -First 20 ThreatName, SeverityID, CategoryID | Format-Table
        """;

    private static string DefenderPreferencesScript() => """
        Get-MpPreference -ErrorAction SilentlyContinue | Select-Object DisableRealtimeMonitoring, ExclusionPath, ExclusionProcess, MAPSReporting, SubmitSamplesConsent | Format-List
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