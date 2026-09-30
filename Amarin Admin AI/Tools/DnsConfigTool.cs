using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class DnsConfigTool : ITool
{
    public string Name => "dns_config";
    public string Description =>
        "DNS resolvers, hosts file, system proxy settings and hostname resolution tests; set an adapter's DNS " +
        "servers or return them to DHCP, add or remove a hosts file entry. set_dns, reset_dns, hosts_add and " +
        "hosts_remove require user confirmation and can be rolled back.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": [
                "resolvers", "hosts_file", "proxy", "test_resolve", "suffix_list",
                "set_dns", "reset_dns", "hosts_add", "hosts_remove"
              ],
              "description": "DNS/proxy action"
            },
            "hostname": {
              "type": "string",
              "description": "Hostname for test_resolve, hosts_add, hosts_remove"
            },
            "address": {
              "type": "string",
              "description": "IP address for hosts_add"
            },
            "adapter": {
              "type": "string",
              "description": "Adapter name (as in resolvers) for set_dns/reset_dns"
            },
            "servers": {
              "type": "array",
              "items": { "type": "string" },
              "description": "set_dns: 1-6 DNS server IP addresses in priority order"
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
        try
        {
            return action switch
            {
                "resolvers" => Resolvers(cancellationToken),
                "hosts_file" => Task.FromResult(HostsFile()),
                "proxy" => Proxy(cancellationToken),
                "test_resolve" => TestResolve(arguments, cancellationToken),
                "set_dns" => SetDns(arguments, cancellationToken),
                "reset_dns" => ResetDns(arguments, cancellationToken),
                "hosts_add" => Task.FromResult(EditHosts(arguments, add: true)),
                "hosts_remove" => Task.FromResult(EditHosts(arguments, add: false)),
                "suffix_list" => PowerShellHelper.RunAsync(
                    "Get-DnsClient | Select-Object InterfaceAlias, ConnectionSpecificSuffix, RegisterThisConnectionsAddress | Format-Table -Wrap",
                    cancellationToken: cancellationToken),
                _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"DNS config error: {ex.Message}"));
        }
    }

    private static Task<ToolResult> Resolvers(CancellationToken cancellationToken) =>
        PowerShellHelper.RunAsync("""
            Get-DnsClientServerAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
              Select-Object InterfaceAlias, ServerAddresses | Format-Table -Wrap
            ipconfig /all
            """, cancellationToken: cancellationToken);

    private static ToolResult HostsFile()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
        if (!File.Exists(path))
        {
            return ToolResult.Fail($"Hosts file not found: {path}");
        }

        var lines = File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
            .Take(200);

        return ToolResult.Ok(string.Join(Environment.NewLine, lines));
    }

    private static async Task<ToolResult> Proxy(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine((await PowerShellHelper.RunAsync("""
            Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' |
              Select-Object ProxyEnable, ProxyServer, ProxyOverride, AutoConfigURL | Format-List
            netsh winhttp show proxy
            """, cancellationToken: cancellationToken)).Output);
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static async Task<ToolResult> TestResolve(JsonElement arguments, CancellationToken cancellationToken)
    {
        var hostname = arguments.TryGetProperty("hostname", out var hostProp) &&
                       hostProp.ValueKind == JsonValueKind.String
            ? hostProp.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(hostname))
        {
            return ToolResult.Fail("Missing required parameter: hostname");
        }

        hostname = hostname.Trim();
        if (!IsHostName(hostname))
        {
            return ToolResult.Fail(
                "Invalid hostname: only letters, digits, dots, hyphens and underscores (or an IP " +
                "address) are accepted. Do not retry with the same value.");
        }

        // Имя проверено и стоит в кавычках в обоих местах. Прежде nslookup получал его без
        // кавычек, и «example.com; Remove-Item …» выполнялось вторым оператором — а это чтение,
        // о котором человека не спрашивают.
        var safe = PowerShellHelper.QuoteLiteral(hostname);
        return await PowerShellHelper.RunAsync($$"""
            Resolve-DnsName -Name '{{safe}}' -ErrorAction SilentlyContinue | Format-Table -AutoSize
            nslookup '{{safe}}'
            """, cancellationToken: cancellationToken);
    }

    private static Task<ToolResult> SetDns(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (Adapter(arguments) is not { } adapter)
        {
            return Task.FromResult(AdapterRequired());
        }

        var values = arguments.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array
            ? servers.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            : [];
        return NetworkCommands.TryDnsServers(values, out var list, out var error)
            ? PowerShellHelper.RunAsync(NetworkCommands.SetDnsScript(adapter, list), 60, cancellationToken)
            : Task.FromResult(ToolResult.Fail(error + " Do not retry with the same value."));
    }

    private static Task<ToolResult> ResetDns(JsonElement arguments, CancellationToken cancellationToken) =>
        Adapter(arguments) is { } adapter
            ? PowerShellHelper.RunAsync(NetworkCommands.ResetDnsScript(adapter), 60, cancellationToken)
            : Task.FromResult(AdapterRequired());

    private static string? Adapter(JsonElement arguments)
    {
        var adapter = arguments.TryGetProperty("adapter", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
        return UndoCommands.IsInterfaceAlias(adapter) ? adapter : null;
    }

    private static ToolResult AdapterRequired() =>
        ToolResult.Fail("adapter is missing or contains wildcards. Use the exact name from resolvers.");

    /// <summary>
    /// Правка hosts — в самой программе, не скриптом: файл маленький, а разбор строк проверяется
    /// тестами (<see cref="NetworkCommands.HostsAdd"/>). Копию для отката снимает снимок.
    /// </summary>
    private static ToolResult EditHosts(JsonElement arguments, bool add)
    {
        var host = arguments.TryGetProperty("hostname", out var hostProp) && hostProp.ValueKind == JsonValueKind.String
            ? hostProp.GetString()?.Trim()
            : null;
        // Только ASCII: hosts читается в кодировке системы, и имя с кириллицей там не сработает —
        // для такого имени нужна его punycode-форма (xn--…).
        if (host is null || !IsHostName(host) || System.Net.IPAddress.TryParse(host, out _) || !host.All(char.IsAscii))
        {
            return ToolResult.Fail(
                "hostname must be an ASCII host name, not an IP address (use the xn-- form for non-Latin names). " +
                "Do not retry with the same value.");
        }

        var address = arguments.TryGetProperty("address", out var addressProp) && addressProp.ValueKind == JsonValueKind.String
            ? addressProp.GetString()?.Trim()
            : null;
        if (add && (address is null || !System.Net.IPAddress.TryParse(address, out _)))
        {
            return ToolResult.Fail("hosts_add needs address: an IP address. Do not retry with the same value.");
        }

        var path = NetworkCommands.HostsPath;
        try
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path) : [];
            string text;
            if (add)
            {
                var (updated, changed) = NetworkCommands.HostsAdd(lines, address!, host);
                if (!changed)
                {
                    return ToolResult.Ok($"hosts already maps {host} to {address}.");
                }

                text = NetworkCommands.JoinHosts(updated);
            }
            else
            {
                var (updated, removed) = NetworkCommands.HostsRemove(lines, host);
                if (removed == 0)
                {
                    return ToolResult.Ok($"hosts has no entry for {host}.");
                }

                text = NetworkCommands.JoinHosts(updated);
            }

            // Без BOM: часть программ читает hosts построчно и спотыкается о метку в начале.
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return ToolResult.Ok(add ? $"hosts: {address} {host} added." : $"hosts: {host} removed.");
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Fail(Amarin.Core.Loc.Get("S.Tool.NeedsAdmin"));
        }
        catch (IOException ex)
        {
            return ToolResult.Fail($"hosts file is busy or unreadable: {ex.Message}");
        }
    }

    /// <summary>Имя узла или IP-адрес: ничего, кроме того, что бывает в имени.</summary>
    internal static bool IsHostName(string value) =>
        value.Length is > 0 and <= 253 &&
        (System.Net.IPAddress.TryParse(value, out _) ||
         value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ||
                         (ch > 127 && char.IsLetterOrDigit(ch))));
}