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

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!arguments.TryGetProperty("action", out var actionProp))
            {
                return Task.FromResult(ToolResult.Fail("Missing required parameter: action"));
            }

            var action = actionProp.GetString()?.ToLowerInvariant();
            return action switch
            {
                "adapters" => Task.FromResult(GetAdapters()),
                "dns" => Task.FromResult(GetDns()),
                "ping" => Task.FromResult(PingHost(arguments)),
                "connections" => Task.FromResult(GetConnections()),
                "firewall_rules" => Task.FromResult(RunNetsh("advfirewall firewall show rule name=all")),
                "flush_dns" => Task.FromResult(RunCommand("ipconfig", "/flushdns")),
                "firewall_enable" => Task.FromResult(SetFirewall(arguments, enabled: true)),
                "firewall_disable" => Task.FromResult(SetFirewall(arguments, enabled: false)),
                _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Network error: {ex.Message}"));
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

    private static ToolResult GetDns() =>
        RunCommand("ipconfig", "/all");

    private static ToolResult PingHost(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("host", out var hostProp) ||
            string.IsNullOrWhiteSpace(hostProp.GetString()))
        {
            return ToolResult.Fail("host is required for ping");
        }

        return RunCommand("ping", $"-n 4 {hostProp.GetString()}");
    }

    private static ToolResult GetConnections() =>
        RunCommand("netstat", "-ano");

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