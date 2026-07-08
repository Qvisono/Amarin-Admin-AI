using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class PortListenerTool : ITool
{
    public string Name => "port_listener";
    public string Description =>
        "List listening TCP/UDP ports and map them to owning processes.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list_listeners", "find_port", "find_process"],
              "description": "Port listener action"
            },
            "port": {
              "type": "integer",
              "description": "Port number for find_port"
            },
            "process_name": {
              "type": "string",
              "description": "Process name substring for find_process"
            },
            "protocol": {
              "type": "string",
              "enum": ["tcp", "udp", "all"],
              "description": "Protocol filter (default all)"
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

        var protocol = arguments.TryGetProperty("protocol", out var protoProp) &&
                       protoProp.ValueKind == JsonValueKind.String
            ? protoProp.GetString() ?? "all"
            : "all";

        var action = actionProp.GetString()?.ToLowerInvariant();
        var script = action switch
        {
            "list_listeners" => ListListenersScript(protocol),
            "find_port" => FindPortScript(arguments, protocol),
            "find_process" => FindProcessScript(arguments, protocol),
            _ => null
        };

        return script is null
            ? Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            : Task.FromResult(PowerShellHelper.Run(script, 120));
    }

    private static string ListListenersScript(string protocol) => $$$"""
        $proto = '{{protocol}}'
        $conns = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue
        if ($proto -eq 'udp') { $conns = @() }
        if ($proto -in @('all','tcp')) {
          $conns | Select-Object LocalAddress, LocalPort, OwningProcess,
            @{n='Process';e={(Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName}} |
            Sort-Object LocalPort | Format-Table -AutoSize
        }
        if ($proto -in @('all','udp')) {
          Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
            Select-Object LocalAddress, LocalPort, OwningProcess,
            @{n='Process';e={(Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName}} |
            Sort-Object LocalPort | Format-Table -AutoSize
        }
        """;

    private static string FindPortScript(JsonElement arguments, string protocol)
    {
        if (!arguments.TryGetProperty("port", out var portProp) || !portProp.TryGetInt32(out var port))
        {
            return "Write-Error 'Missing port parameter' ; exit 1";
        }

        return $$$"""
            $port = {{port}}
            Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue |
              Select-Object State, LocalAddress, LocalPort, RemoteAddress, OwningProcess,
                @{n='Process';e={(Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName}} |
              Format-Table -AutoSize
            Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue |
              Select-Object LocalAddress, LocalPort, OwningProcess,
                @{n='Process';e={(Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName}} |
              Format-Table -AutoSize
            netstat -ano | findstr ":$port "
            """;
    }

    private static string FindProcessScript(JsonElement arguments, string protocol)
    {
        var processName = arguments.TryGetProperty("process_name", out var nameProp) &&
                          nameProp.ValueKind == JsonValueKind.String
            ? nameProp.GetString() ?? ""
            : "";

        if (string.IsNullOrWhiteSpace(processName))
        {
            return "Write-Error 'Missing process_name parameter' ; exit 1";
        }

        var safe = processName.Replace("'", "''", StringComparison.Ordinal);
        return $$$"""
            $name = '{{safe}}'
            $pids = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "*$name*" } | Select-Object -ExpandProperty Id
            foreach ($pid in $pids) {
              Write-Output "=== PID $pid ==="
              Get-NetTCPConnection -OwningProcess $pid -ErrorAction SilentlyContinue |
                Select-Object State, LocalAddress, LocalPort, RemoteAddress | Format-Table -AutoSize
              Get-NetUDPEndpoint -OwningProcess $pid -ErrorAction SilentlyContinue |
                Select-Object LocalAddress, LocalPort | Format-Table -AutoSize
            }
            """;
    }
}