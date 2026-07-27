using System.Text.Json;

namespace Amarin.Tools;

public sealed class PowerShellTool : ITool
{
    public string Name => "run_powershell";
    public string Description =>
        "Execute a PowerShell command or script on the local Windows system. " +
        "Use for diagnostics, configuration, service management, and any system administration task. " +
        "Deleting existing files (Remove-Item, del, rm, rmdir, etc.) is forbidden. " +
        "For mutating commands always pass explanation (1–2 sentences in Russian for the user confirmation dialog).";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "command": {
              "type": "string",
              "description": "PowerShell command or script block to execute"
            },
            "timeout_seconds": {
              "type": "integer",
              "description": "Maximum execution time in seconds (default 120, max 600)"
            },
            "explanation": {
              "type": "string",
              "description": "Required for mutating commands. Brief Russian explanation for the user: what the code does and what changes on the PC (1–2 sentences, not the raw command)."
            }
          },
          "required": ["command"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("command", out var commandProp) ||
            string.IsNullOrWhiteSpace(commandProp.GetString()))
        {
            return Task.FromResult(ToolResult.Fail("Missing required parameter: command"));
        }

        var command = commandProp.GetString()!;

        if (DeletionGuard.PowerShellAttemptsDeletion(command))
        {
            return Task.FromResult(ToolResult.Fail(DeletionGuard.FileDeletionBlockedMessage));
        }

        var timeoutSeconds = 120;
        if (arguments.TryGetProperty("timeout_seconds", out var timeoutProp) &&
            timeoutProp.TryGetInt32(out var requested))
        {
            timeoutSeconds = Math.Clamp(requested, 5, 600);
        }

        return PowerShellProcessRunner.RunAsync(command, timeoutSeconds, cancellationToken);
    }
}