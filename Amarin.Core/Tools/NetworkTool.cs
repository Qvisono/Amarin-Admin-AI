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
        "Network diagnostics and repair: adapters, DNS, ping, traceroute, connections, firewall rules, Wi-Fi " +
        "profiles; flush DNS, turn the firewall or an adapter on/off, forget a Wi-Fi profile, reset Winsock or the " +
        "IP stack (both need a reboot). Mutating actions require user confirmation.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": [
                "adapters", "dns", "ping", "traceroute", "connections", "firewall_rules", "wifi_profiles",
                "flush_dns", "firewall_enable", "firewall_disable", "adapter_enable", "adapter_disable",
                "wifi_forget", "reset_winsock", "reset_ip"
              ],
              "description": "Network operation"
            },
            "host": {
              "type": "string",
              "description": "Host name or IP for ping/traceroute"
            },
            "max_hops": {
              "type": "integer",
              "description": "traceroute: max hops, 1-30 (default 20)"
            },
            "adapter": {
              "type": "string",
              "description": "Adapter name (as in adapters) for adapter_enable/adapter_disable"
            },
            "wifi_profile": {
              "type": "string",
              "description": "Wi-Fi profile name (as in wifi_profiles) for wifi_forget"
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
                "traceroute" => await TracerouteAsync(arguments, cancellationToken),
                "wifi_profiles" => await NativeProcess.RunRawAsync(
                    "netsh", NetworkCommands.WifiListArguments, CommandTimeoutSeconds, cancellationToken),
                "wifi_forget" => await ForgetWifiAsync(arguments, cancellationToken),
                "adapter_enable" => await SetAdapterAsync(arguments, enable: true, cancellationToken),
                "adapter_disable" => await SetAdapterAsync(arguments, enable: false, cancellationToken),
                "reset_winsock" => WithRebootNote(await RunAsync("netsh", NetworkCommands.WinsockReset, cancellationToken)),
                "reset_ip" => WithRebootNote(await RunAsync("netsh", NetworkCommands.IpReset, cancellationToken)),
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

    private static async Task<ToolResult> TracerouteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var host = StringArg(arguments, "host");
        if (host is null || !DnsConfigTool.IsHostName(host))
        {
            return ToolResult.Fail("traceroute needs host: a host name or an IP address. Do not retry with the same value.");
        }

        var hops = arguments.TryGetProperty("max_hops", out var hopsProp) && hopsProp.TryGetInt32(out var value)
            ? value
            : 20;
        return await NativeProcess.RunAsync("tracert.exe", NetworkCommands.TracertArguments(host, hops),
            NetworkCommands.TracertTimeoutSeconds(hops), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ToolResult> ForgetWifiAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var profile = StringArg(arguments, "wifi_profile");
        if (!NetworkCommands.IsWifiProfileName(profile))
        {
            return ToolResult.Fail(
                "wifi_profile is missing or contains quotes. Use the exact name from wifi_profiles. Do not retry with the same value.");
        }

        return await NativeProcess.RunRawAsync("netsh", NetworkCommands.WifiDeleteArguments(profile!),
            CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ToolResult> SetAdapterAsync(JsonElement arguments, bool enable, CancellationToken cancellationToken)
    {
        var adapter = StringArg(arguments, "adapter");
        if (!UndoCommands.IsInterfaceAlias(adapter))
        {
            return ToolResult.Fail(
                "adapter is missing or contains wildcards. Use the exact adapter name from adapters. Do not retry with the same value.");
        }

        return await PowerShellHelper.RunAsync(NetworkCommands.AdapterScript(adapter!, enable), 120, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Сброс стека вступает в силу только после перезагрузки — модель должна это передать.</summary>
    private static ToolResult WithRebootNote(ToolResult result) =>
        result.Success ? ToolResult.Ok(result.Output + "\n" + Loc.Get("S.Tool.Network.RebootNeeded")) : result;

    private static string? StringArg(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static Task<ToolResult> RunAsync(
        string file,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        NativeProcess.RunAsync(file, arguments, CommandTimeoutSeconds, cancellationToken, maxOutput: 16_000);

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n… [обрезано]";
}
