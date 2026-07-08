using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class ScheduledTaskTool : ITool
{
    public string Name => "scheduled_task";
    public string Description =>
        "Manage Windows Scheduled Tasks: list, query, create, run, enable, disable, delete.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "query", "create", "run", "enable", "disable", "delete"],
              "description": "Task operation"
            },
            "task_name": {
              "type": "string",
              "description": "Task name or path"
            },
            "command": {
              "type": "string",
              "description": "Command for create action"
            },
            "schedule": {
              "type": "string",
              "description": "Schedule for create, e.g. daily /HOURLY"
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
            arguments.TryGetProperty("task_name", out var nameProp);
            var taskName = nameProp.ValueKind == JsonValueKind.String ? nameProp.GetString() : null;

            return Task.FromResult(action switch
            {
                "list" => RunSchtasks("/query /fo LIST /v"),
                "query" => RequireName(taskName, name => RunSchtasks($"/query /tn \"{name}\" /fo LIST /v")),
                "run" => RequireName(taskName, name => RunSchtasks($"/run /tn \"{name}\"")),
                "enable" => RequireName(taskName, name => RunSchtasks($"/change /tn \"{name}\" /enable")),
                "disable" => RequireName(taskName, name => RunSchtasks($"/change /tn \"{name}\" /disable")),
                "delete" => RequireName(taskName, name => RunSchtasks($"/delete /tn \"{name}\" /f")),
                "create" => CreateTask(arguments, taskName),
                _ => ToolResult.Fail($"Unknown action: {action}")
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Scheduled task error: {ex.Message}"));
        }
    }

    private static ToolResult CreateTask(JsonElement arguments, string? taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName))
        {
            return ToolResult.Fail("task_name is required for create");
        }

        if (!arguments.TryGetProperty("command", out var cmdProp) ||
            string.IsNullOrWhiteSpace(cmdProp.GetString()))
        {
            return ToolResult.Fail("command is required for create");
        }

        var schedule = arguments.TryGetProperty("schedule", out var schedProp) &&
                       schedProp.ValueKind == JsonValueKind.String
            ? schedProp.GetString() ?? "daily"
            : "daily";

        var command = cmdProp.GetString()!;
        return RunSchtasks($"/create /tn \"{taskName}\" /tr \"{command}\" /sc {schedule} /f");
    }

    private static ToolResult RequireName(string? taskName, Func<string, ToolResult> action)
    {
        if (string.IsNullOrWhiteSpace(taskName))
        {
            return ToolResult.Fail("task_name is required");
        }

        return action(taskName);
    }

    private static ToolResult RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
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