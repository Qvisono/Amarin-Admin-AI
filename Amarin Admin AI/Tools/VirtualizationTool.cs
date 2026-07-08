using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class VirtualizationTool : ITool
{
    public string Name => "virtualization";
    public string Description =>
        "Hyper-V and Docker diagnostics and control. VM/container start/stop requires user confirmation.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": [
                "list_hyperv_vms", "hyperv_vm_status", "start_vm", "stop_vm",
                "list_docker_containers", "docker_status", "docker_start", "docker_stop"
              ],
              "description": "Virtualization operation"
            },
            "name": {
              "type": "string",
              "description": "VM or container name"
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
            arguments.TryGetProperty("name", out var nameProp);
            var name = nameProp.ValueKind == JsonValueKind.String ? nameProp.GetString() : null;

            return Task.FromResult(action switch
            {
                "list_hyperv_vms" => RunPowerShell("Get-VM | Select-Object Name,State,CPUUsage,MemoryAssigned | Format-Table -AutoSize"),
                "hyperv_vm_status" => RequireName(name, n => RunPowerShell($"Get-VM -Name '{n}' | Format-List *")),
                "start_vm" => RequireName(name, n => RunPowerShell($"Start-VM -Name '{n}'")),
                "stop_vm" => RequireName(name, n => RunPowerShell($"Stop-VM -Name '{n}' -Force")),
                "list_docker_containers" => RunCommand("docker", "ps -a"),
                "docker_status" => RunCommand("docker", "info"),
                "docker_start" => RequireName(name, n => RunCommand("docker", $"start {n}")),
                "docker_stop" => RequireName(name, n => RunCommand("docker", $"stop {n}")),
                _ => ToolResult.Fail($"Unknown action: {action}")
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Virtualization error: {ex.Message}"));
        }
    }

    private static ToolResult RequireName(string? name, Func<string, ToolResult> action)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolResult.Fail("name is required");
        }

        return action(name);
    }

    private static ToolResult RunPowerShell(string command)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
        return RunCommand("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}");
    }

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
            ? ToolResult.Ok(output.Trim())
            : ToolResult.Fail(output.Trim());
    }
}