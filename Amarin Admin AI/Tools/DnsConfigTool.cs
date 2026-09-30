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
                "resolvers" => Resolvers(cancellationToken),
                "hosts_file" => Task.FromResult(HostsFile()),
                "proxy" => Proxy(cancellationToken),
                "test_resolve" => TestResolve(arguments, cancellationToken),
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

    /// <summary>Имя узла или IP-адрес: ничего, кроме того, что бывает в имени.</summary>
    internal static bool IsHostName(string value) =>
        value.Length is > 0 and <= 253 &&
        (System.Net.IPAddress.TryParse(value, out _) ||
         value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ||
                         (ch > 127 && char.IsLetterOrDigit(ch))));
}