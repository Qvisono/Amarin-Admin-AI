using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class DnsConfigTool : ITool
{
    public string Name => "dns_config";
    public string Description =>
        "DNS resolvers, hosts file, system proxy settings, and hostname resolution tests.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["resolvers", "hosts_file", "proxy", "test_resolve", "suffix_list"],
              "description": "DNS/proxy diagnostic action"
            },
            "hostname": {
              "type": "string",
              "description": "Hostname for test_resolve"
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
                "resolvers" => Task.FromResult(Resolvers()),
                "hosts_file" => Task.FromResult(HostsFile()),
                "proxy" => Task.FromResult(Proxy()),
                "test_resolve" => Task.FromResult(TestResolve(arguments)),
                "suffix_list" => Task.FromResult(PowerShellHelper.Run(
                    "Get-DnsClient | Select-Object InterfaceAlias, ConnectionSpecificSuffix, RegisterThisConnectionsAddress | Format-Table -Wrap")),
                _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"DNS config error: {ex.Message}"));
        }
    }

    private static ToolResult Resolvers() =>
        PowerShellHelper.Run("""
            Get-DnsClientServerAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
              Select-Object InterfaceAlias, ServerAddresses | Format-Table -Wrap
            ipconfig /all
            """);

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

    private static ToolResult Proxy()
    {
        var sb = new StringBuilder();
        sb.AppendLine(PowerShellHelper.Run("""
            Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' |
              Select-Object ProxyEnable, ProxyServer, ProxyOverride, AutoConfigURL | Format-List
            netsh winhttp show proxy
            """).Output);
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult TestResolve(JsonElement arguments)
    {
        var hostname = arguments.TryGetProperty("hostname", out var hostProp) &&
                       hostProp.ValueKind == JsonValueKind.String
            ? hostProp.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(hostname))
        {
            return ToolResult.Fail("Missing required parameter: hostname");
        }

        var safe = hostname.Replace("'", "''", StringComparison.Ordinal);
        return PowerShellHelper.Run($$"""
            Resolve-DnsName -Name '{{safe}}' -ErrorAction SilentlyContinue | Format-Table -AutoSize
            nslookup {{safe}}
            """);
    }
}