using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class NetworkTool : ITool
{
    public string Name => "network";
    public string Description =>
        "Network diagnostics: adapters, DNS, ping, connections, firewall rules. " +
        "Mutating actions require user confirmation.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["adapters", "dns", "ping", "connections", "firewall_rules", "flush_dns", "firewall_enable", "firewall_disable"],
              "description": "Network operation"
            },
            "host": {
              "type": "string",
              "description": "Host for ping"
            },
            "profile": {
              "type": "string",
              "enum": ["domain", "private", "public"],
              "description": "Firewall profile for enable/disable"
            }
          },
          "required": ["action"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!arguments.TryGetProperty("action", out var actionProp))
            {
                return ToolResult.Fail("Missing required parameter: action");
            }

            var action = actionProp.GetString()?.ToLowerInvariant();
            return action switch
            {
                "adapters" => GetAdapters(),
                "dns" => GetDns(),
                "ping" => await PingHostAsync(arguments, cancellationToken),
                "connections" => GetConnections(),
                "firewall_rules" => RunNetsh("advfirewall firewall show rule name=all"),
                "flush_dns" => RunCommand("ipconfig", "/flushdns"),
                "firewall_enable" => SetFirewall(arguments, enabled: true),
                "firewall_disable" => SetFirewall(arguments, enabled: false),
                _ => ToolResult.Fail($"Unknown action: {action}")
            };
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Network error: {ex.Message}");
        }
    }

    private static ToolResult GetAdapters()
    {
        var sb = new StringBuilder();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            sb.AppendLine($"{nic.Name} | {nic.NetworkInterfaceType} | {nic.OperationalStatus}");
            var ip = nic.GetIPProperties();
            foreach (var addr in ip.UnicastAddresses)
            {
                sb.AppendLine($"  {addr.Address}/{addr.PrefixLength}");
            }

            if (ip.DnsAddresses.Count > 0)
            {
                sb.AppendLine($"  DNS: {string.Join(", ", ip.DnsAddresses)}");
            }
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult GetDns()
    {
        var sb = new StringBuilder();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            var ip = nic.GetIPProperties();
            sb.AppendLine($"{nic.Name} | {nic.NetworkInterfaceType}");
            if (!string.IsNullOrWhiteSpace(ip.DnsSuffix))
            {
                sb.AppendLine($"  Suffix: {ip.DnsSuffix}");
            }

            if (ip.DnsAddresses.Count > 0)
            {
                sb.AppendLine($"  DNS: {string.Join(", ", ip.DnsAddresses)}");
            }

            if (ip.GatewayAddresses.Count > 0)
            {
                sb.AppendLine($"  Gateway: {string.Join(", ", ip.GatewayAddresses.Select(g => g.Address))}");
            }
        }

        return ToolResult.Ok(sb.Length == 0 ? "Нет активных адаптеров." : sb.ToString().TrimEnd());
    }

    private static async Task<ToolResult> PingHostAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("host", out var hostProp) ||
            string.IsNullOrWhiteSpace(hostProp.GetString()))
        {
            return ToolResult.Fail("host is required for ping");
        }

        var host = hostProp.GetString()!.Trim();
        var sb = new StringBuilder();
        sb.AppendLine($"Ping {host} (4 packets):");

        using var ping = new Ping();
        var sent = 0;
        var received = 0;
        long totalMs = 0;
        long minMs = long.MaxValue;
        long maxMs = 0;

        for (var i = 0; i < 4; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sent++;
            try
            {
                var reply = await ping.SendPingAsync(host, 4000);
                if (reply.Status == IPStatus.Success)
                {
                    received++;
                    var ms = reply.RoundtripTime;
                    totalMs += ms;
                    if (ms < minMs) minMs = ms;
                    if (ms > maxMs) maxMs = ms;
                    var ttl = reply.Options?.Ttl;
                    sb.AppendLine(ttl is null
                        ? $"  Reply from {reply.Address}: time={ms}ms"
                        : $"  Reply from {reply.Address}: time={ms}ms TTL={ttl}");
                }
                else
                {
                    sb.AppendLine($"  {reply.Status}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  error: {ex.Message}");
            }
        }

        sb.AppendLine($"Sent {sent}, received {received}, lost {sent - received}.");
        if (received > 0)
        {
            sb.AppendLine($"RTT min/avg/max = {minMs}/{totalMs / received}/{maxMs} ms");
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult GetConnections()
    {
        var names = NativeNetTable.ProcessNames();
        var sb = new StringBuilder();
        sb.AppendLine("Proto Local Remote State PID Process");

        var rows = NativeNetTable.GetTcpRows();
        rows.AddRange(NativeNetTable.GetUdpRows());

        var written = 0;
        foreach (var row in rows
                     .OrderBy(r => r.Protocol)
                     .ThenBy(r => r.LocalPort)
                     .ThenBy(r => r.Pid))
        {
            var remote = row.Protocol == "UDP"
                ? "*"
                : $"{row.RemoteAddress}:{row.RemotePort}";
            sb.AppendLine(
                $"{row.Protocol} {row.LocalAddress}:{row.LocalPort} {remote} {row.State} {row.Pid} {NativeNetTable.LookupProcess(names, row.Pid)}");
            if (++written >= 400)
            {
                sb.AppendLine("… [обрезано]");
                break;
            }
        }

        return ToolResult.Ok(written == 0 ? "Нет соединений." : Truncate(sb.ToString().TrimEnd(), 16_000));
    }

    private static ToolResult SetFirewall(JsonElement arguments, bool enabled)
    {
        var profile = arguments.TryGetProperty("profile", out var profileProp) &&
                      profileProp.ValueKind == JsonValueKind.String
            ? profileProp.GetString() ?? "private"
            : "private";

        var state = enabled ? "on" : "off";
        return RunNetsh($"advfirewall set {profile}profile state {state}");
    }

    private static ToolResult RunNetsh(string args) =>
        RunCommand("netsh", args);

    private static ToolResult RunCommand(string file, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        return process.ExitCode == 0
            ? ToolResult.Ok(Truncate(output, 16_000))
            : ToolResult.Fail(Truncate(output, 16_000));
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n… [обрезано]";
}
