using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class NetworkTool : ITool
{
    /// <summary>
    /// Потолок внешней команды. Прежде ожидание было бесконечным: зависший netsh держал ход,
    /// и «Стоп» его не прерывал.
    /// </summary>
    private const int CommandTimeoutSeconds = 120;

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
              "enum": ["domain", "private", "public", "all"],
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
                "firewall_rules" => await RunAsync(
                    "netsh", ["advfirewall", "firewall", "show", "rule", "name=all"], cancellationToken),
                "flush_dns" => await RunAsync("ipconfig", ["/flushdns"], cancellationToken),
                "firewall_enable" => await SetFirewallAsync(arguments, enabled: true, cancellationToken),
                "firewall_disable" => await SetFirewallAsync(arguments, enabled: false, cancellationToken),
                _ => ToolResult.Fail($"Unknown action: {action}")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

    private static Task<ToolResult> SetFirewallAsync(
        JsonElement arguments,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var profile = arguments.TryGetProperty("profile", out var profileProp) &&
                      profileProp.ValueKind == JsonValueKind.String
            ? profileProp.GetString() ?? "private"
            : "private";

        // Профиль от модели шёл в строку netsh как есть: «private state off & …» дописывал
        // команде свои ключи. Теперь — только значение из перечня схемы.
        if (!NetworkCommands.TryFirewallProfile(profile, out var netshProfile))
        {
            return Task.FromResult(ToolResult.Fail(Loc.Format("S.Tool.Network.BadProfile", profile)));
        }

        return RunAsync("netsh", NetworkCommands.FirewallState(netshProfile, enabled), cancellationToken);
    }

    private static Task<ToolResult> RunAsync(
        string file,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        NativeProcess.RunAsync(file, arguments, CommandTimeoutSeconds, cancellationToken, maxOutput: 16_000);

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n… [обрезано]";
}
