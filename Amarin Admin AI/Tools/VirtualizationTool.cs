using System.Runtime.Versioning;
using System.Text.Json;
using Amarin.Core;

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

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!arguments.TryGetProperty("action", out var actionProp))
            {
                return ToolResult.Fail("Missing required parameter: action");
            }

            var action = actionProp.GetString()?.ToLowerInvariant();
            arguments.TryGetProperty("name", out var nameProp);
            var name = nameProp.ValueKind == JsonValueKind.String ? nameProp.GetString()?.Trim() : null;

            return action switch
            {
                "list_hyperv_vms" => await PowerShell(
                    VirtualizationCommands.ListVms, cancellationToken),
                "hyperv_vm_status" => await WithVmName(name, n => PowerShell(
                    VirtualizationCommands.VmStatus(n), cancellationToken)),
                "start_vm" => await WithVmName(name, n => PowerShell(
                    VirtualizationCommands.StartVm(n), cancellationToken)),
                "stop_vm" => await WithVmName(name, n => PowerShell(
                    VirtualizationCommands.StopVm(n), cancellationToken)),
                "list_docker_containers" => await Docker(["ps", "-a"], cancellationToken),
                "docker_status" => await Docker(["info"], cancellationToken),
                "docker_start" => await WithContainerName(name, n => Docker(["start", n], cancellationToken)),
                "docker_stop" => await WithContainerName(name, n => Docker(["stop", n], cancellationToken)),
                _ => ToolResult.Fail($"Unknown action: {action}")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Virtualization error: {ex.Message}");
        }
    }

    private static Task<ToolResult> WithVmName(string? name, Func<string, Task<ToolResult>> action) =>
        VirtualizationCommands.IsVmName(name)
            ? action(name!)
            : Task.FromResult(ToolResult.Fail(Loc.Get("S.Tool.Virt.BadVmName")));

    private static Task<ToolResult> WithContainerName(string? name, Func<string, Task<ToolResult>> action) =>
        VirtualizationCommands.IsContainerName(name)
            ? action(name!)
            : Task.FromResult(ToolResult.Fail(Loc.Get("S.Tool.Virt.BadContainerName")));

    private static Task<ToolResult> PowerShell(string script, CancellationToken cancellationToken) =>
        PowerShellHelper.RunAsync(script, 180, cancellationToken, maxOutput: 16_000);

    private static Task<ToolResult> Docker(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        NativeProcess.RunAsync("docker", arguments, 180, cancellationToken);
}
