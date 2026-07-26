using System.Diagnostics;
using System.Text.Json;

namespace Amarin.Tools;

public sealed record ToolSmokeResult(
    string ToolName,
    bool Success,
    long ElapsedMs,
    string Summary);

public static class ToolSmokeRunner
{
    private static readonly (string Name, string Arguments)[] SmokeCases =
    [
        ("run_powershell", """{"command":"Get-Date -Format o"}"""),
        ("registry", """{"action":"read","path":"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion"}"""),
        ("windows_service", """{"action":"list"}"""),
        ("filesystem", """{"action":"exists","path":"C:\\__amarin_smoke_missing__.txt"}"""),
        ("system_info", "{}"),
        ("capture_screenshot", "{}"),
        ("read_clipboard", "{}"),
        ("analyze_folder", """{"path":"C:\\Windows"}"""),
        ("event_log", """{"action":"read","preset":"critical_recent","max_events":5}"""),
        ("network", """{"action":"adapters"}"""),
        ("scheduled_task", """{"action":"list"}"""),
        ("wmi_query", """{"scope":"os"}"""),
        ("windows_process", """{"action":"list"}"""),
        ("virtualization", """{"action":"list_docker_containers"}"""),
        ("reliability", """{"action":"stability_records"}"""),
        ("windows_update", """{"action":"reboot_required"}"""),
        ("security_status", """{"action":"firewall_status"}"""),
        ("devices", """{"action":"pnp_devices"}"""),
        ("dns_config", """{"action":"resolvers"}"""),
        ("port_listener", """{"action":"list_listeners"}"""),
        ("remote_access", """{"action":"rdp_status"}"""),
        ("change_rollback", """{"action":"list_snapshots"}"""),
        ("performance", """{"action":"summary"}"""),
        ("startup_programs", """{"action":"list_all"}"""),
        ("credentials", """{"action":"list_cmdkey"}"""),
        ("system_repair", """{"action":"status_sfc"}"""),
        ("restore_point", """{"action":"status"}"""),
        ("disk_management", """{"action":"list_volumes"}"""),
        ("disk_space", """{"action":"analyze"}"""),
        ("software_inventory", """{"action":"list_installed"}"""),
        ("firewall_rules", """{"action":"list","filter":"enabled"}"""),
        ("windows_features", """{"action":"list","filter":"enabled"}"""),
        ("local_users", """{"action":"list_users"}""")
    ];

    public static async Task<IReadOnlyList<ToolSmokeResult>> RunAsync(
        ToolRegistry registry,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ToolSmokeResult>(SmokeCases.Length);

        foreach (var (name, argumentsJson) in SmokeCases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!registry.All.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new ToolSmokeResult(name, false, 0, "Tool not registered"));
                continue;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

            var sw = Stopwatch.StartNew();
            try
            {
                var args = JsonDocument.Parse(argumentsJson).RootElement.Clone();
                var result = await registry.ExecuteAsync(name, args, timeoutCts.Token);
                sw.Stop();
                results.Add(new ToolSmokeResult(
                    name,
                    result.Success,
                    sw.ElapsedMilliseconds,
                    Summarize(result.Output)));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                sw.Stop();
                results.Add(new ToolSmokeResult(name, false, sw.ElapsedMilliseconds, "Timeout (3 min)"));
            }
            catch (Exception ex)
            {
                sw.Stop();
                results.Add(new ToolSmokeResult(name, false, sw.ElapsedMilliseconds, ex.Message));
            }
        }

        return results;
    }

    private static string Summarize(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "empty output";
        }

        var line = output.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return line.Length <= 100 ? line : line[..100] + "…";
    }
}