using System.Runtime.Versioning;
using System.Text;
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
            ? protoProp.GetString()?.ToLowerInvariant() ?? "all"
            : "all";

        var action = actionProp.GetString()?.ToLowerInvariant();
        try
        {
            return action switch
            {
                "list_listeners" => Task.FromResult(ListListeners(protocol)),
                "find_port" => Task.FromResult(FindPort(arguments, protocol)),
                "find_process" => Task.FromResult(FindProcess(arguments, protocol)),
                _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Port listener error: {ex.Message}"));
        }
    }

    private static ToolResult ListListeners(string protocol)
    {
        var names = NativeNetTable.ProcessNames();
        var sb = new StringBuilder();
        var rows = FilterProtocol(CollectListeners(), protocol)
            .OrderBy(r => r.LocalPort)
            .ThenBy(r => r.Protocol);

        sb.AppendLine("Proto Local Port PID Process");
        var count = 0;
        foreach (var row in rows)
        {
            sb.AppendLine(
                $"{row.Protocol} {row.LocalAddress} {row.LocalPort} {row.Pid} {NativeNetTable.LookupProcess(names, row.Pid)}");
            if (++count >= 400)
            {
                sb.AppendLine("… [обрезано]");
                break;
            }
        }

        return ToolResult.Ok(count == 0 ? "Слушающих портов не найдено." : sb.ToString().TrimEnd());
    }

    private static ToolResult FindPort(JsonElement arguments, string protocol)
    {
        if (!arguments.TryGetProperty("port", out var portProp) || !portProp.TryGetInt32(out var port))
        {
            return ToolResult.Fail("Missing port parameter");
        }

        var names = NativeNetTable.ProcessNames();
        var sb = new StringBuilder();
        sb.AppendLine($"Port {port}:");

        var matches = FilterProtocol(NativeNetTable.GetTcpRows().Concat(NativeNetTable.GetUdpRows()), protocol)
            .Where(r => r.LocalPort == port)
            .ToList();

        if (matches.Count == 0)
        {
            return ToolResult.Ok($"Ничего не слушает порт {port}.");
        }

        foreach (var row in matches)
        {
            var remote = row.Protocol == "UDP" ? "*" : $"{row.RemoteAddress}:{row.RemotePort}";
            sb.AppendLine(
                $"{row.Protocol} {row.State} {row.LocalAddress}:{row.LocalPort} {remote} PID {row.Pid} {NativeNetTable.LookupProcess(names, row.Pid)}");
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult FindProcess(JsonElement arguments, string protocol)
    {
        var processName = arguments.TryGetProperty("process_name", out var nameProp) &&
                          nameProp.ValueKind == JsonValueKind.String
            ? nameProp.GetString() ?? ""
            : "";

        if (string.IsNullOrWhiteSpace(processName))
        {
            return ToolResult.Fail("Missing process_name parameter");
        }

        var names = NativeNetTable.ProcessNames();
        var pids = names
            .Where(kv => kv.Value.Contains(processName, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToHashSet();

        if (pids.Count == 0)
        {
            return ToolResult.Ok($"Процесс «{processName}» не найден.");
        }

        var sb = new StringBuilder();
        var rows = FilterProtocol(NativeNetTable.GetTcpRows().Concat(NativeNetTable.GetUdpRows()), protocol)
            .Where(r => pids.Contains(r.Pid))
            .ToList();

        foreach (var pid in pids.OrderBy(p => p))
        {
            sb.AppendLine($"=== PID {pid} ({NativeNetTable.LookupProcess(names, pid)}) ===");
            var owned = rows.Where(r => r.Pid == pid).ToList();
            if (owned.Count == 0)
            {
                sb.AppendLine("  (нет соединений)");
                continue;
            }

            foreach (var row in owned)
            {
                var remote = row.Protocol == "UDP" ? "*" : $"{row.RemoteAddress}:{row.RemotePort}";
                sb.AppendLine($"  {row.Protocol} {row.State} {row.LocalAddress}:{row.LocalPort} {remote}");
            }
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static IEnumerable<NativeNetTable.Row> CollectListeners() =>
        NativeNetTable.GetTcpRows().Where(r => r.State.Equals("Listen", StringComparison.OrdinalIgnoreCase))
            .Concat(NativeNetTable.GetUdpRows());

    private static IEnumerable<NativeNetTable.Row> FilterProtocol(
        IEnumerable<NativeNetTable.Row> rows,
        string protocol) =>
        protocol switch
        {
            "tcp" => rows.Where(r => r.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase)),
            "udp" => rows.Where(r => r.Protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase)),
            _ => rows
        };
}
