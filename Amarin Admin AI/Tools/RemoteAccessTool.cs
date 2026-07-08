using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class RemoteAccessTool : ITool
{
    public string Name => "remote_access";
    public string Description =>
        "RDP settings, VPN connections, Hyper-V virtual switches and VM network info. Read-only.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": [
                "rdp_status", "rdp_sessions", "vpn_connections",
                "hyperv_switches", "hyperv_nics", "listening_rdp"
              ],
              "description": "Remote access diagnostic action"
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
        var script = action switch
        {
            "rdp_status" => RdpStatusScript(),
            "rdp_sessions" => RdpSessionsScript(),
            "vpn_connections" => VpnScript(),
            "hyperv_switches" => HypervSwitchesScript(),
            "hyperv_nics" => HypervNicsScript(),
            "listening_rdp" => ListeningRdpScript(),
            _ => null
        };

        return script is null
            ? Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            : Task.FromResult(PowerShellHelper.Run(script, 120));
    }

    private static string RdpStatusScript() => """
        Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' |
          Select-Object fDenyTSConnections, AllowTSConnections | Format-List
        Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp' -ErrorAction SilentlyContinue |
          Select-Object PortNumber, SecurityLayer, UserAuthentication | Format-List
        (Get-Service -Name TermService -ErrorAction SilentlyContinue) | Select-Object Name, Status, StartType | Format-List
        """;

    private static string RdpSessionsScript() => """
        quser 2>&1
        query session 2>&1
        """;

    private static string VpnScript() => """
        Get-VpnConnection -ErrorAction SilentlyContinue | Format-List
        rasdial 2>&1
        Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceDescription -match 'WAN Miniport|VPN|PPTP|L2TP|SSTP|IKEv2' } |
          Select-Object Name, Status, InterfaceDescription | Format-Table -AutoSize
        """;

    private static string HypervSwitchesScript() => """
        Get-VMSwitch -ErrorAction SilentlyContinue | Select-Object Name, SwitchType, NetAdapterInterfaceDescription, AllowManagementOS | Format-Table -AutoSize
        if (-not $?) { Write-Output 'Hyper-V module unavailable or feature not installed.' }
        """;

    private static string HypervNicsScript() => """
        Get-VMNetworkAdapter -ManagementOS -ErrorAction SilentlyContinue | Select-Object Name, SwitchName, MacAddress, Status | Format-Table -AutoSize
        Get-VM -ErrorAction SilentlyContinue | ForEach-Object {
          Write-Output "=== $($_.Name) ==="
          Get-VMNetworkAdapter -VMName $_.Name -ErrorAction SilentlyContinue | Select-Object Name, SwitchName, MacAddress, Status | Format-Table -AutoSize
        }
        """;

    private static string ListeningRdpScript() => """
        Get-NetTCPConnection -LocalPort 3389 -State Listen -ErrorAction SilentlyContinue |
          Select-Object LocalAddress, LocalPort, OwningProcess | Format-Table -AutoSize
        netstat -ano | findstr ":3389"
        """;
}