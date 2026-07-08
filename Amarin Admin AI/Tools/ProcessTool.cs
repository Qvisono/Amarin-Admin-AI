using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class ProcessTool : ITool
{
    public string Name => "windows_process";
    public string Description =>
        "List and inspect Windows processes. Stop/kill requires user confirmation.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "top", "stop", "kill"],
              "description": "Process operation"
            },
            "process_name": {
              "type": "string",
              "description": "Process name without .exe"
            },
            "pid": {
              "type": "integer",
              "description": "Process ID"
            },
            "filter": {
              "type": "string",
              "description": "Optional name filter for list/top"
            },
            "limit": {
              "type": "integer",
              "description": "Max rows for list/top (default 30)"
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
            arguments.TryGetProperty("filter", out var filterProp);
            var filter = filterProp.ValueKind == JsonValueKind.String ? filterProp.GetString() : null;
            var limit = GetInt(arguments, "limit", 30, 5, 100);

            return action switch
            {
                "list" => Task.FromResult(ListProcesses(filter, limit, byMemory: false)),
                "top" => Task.FromResult(ListProcesses(filter, limit, byMemory: true)),
                "stop" => Task.FromResult(StopProcess(arguments, force: false)),
                "kill" => Task.FromResult(StopProcess(arguments, force: true)),
                _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Process error: {ex.Message}"));
        }
    }

    private static ToolResult ListProcesses(string? filter, int limit, bool byMemory)
    {
        var processes = Process.GetProcesses()
            .Select(p =>
            {
                try
                {
                    return new
                    {
                        Process = p,
                        Name = p.ProcessName,
                        Memory = p.WorkingSet64,
                        Pid = p.Id
                    };
                }
                catch
                {
                    return null;
                }
            })
            .Where(p => p is not null)
            .Select(p => p!)
            .Where(p => filter is null || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var ordered = (byMemory
            ? processes.OrderByDescending(p => p.Memory)
            : processes.OrderBy(p => p.Name))
            .Take(limit)
            .Select(p => $"{p.Pid,6} | {p.Name,-24} | {FormatBytes(p.Memory)}")
            .ToList();

        return ToolResult.Ok(ordered.Count == 0
            ? "Процессы не найдены."
            : string.Join(Environment.NewLine, ordered));
    }

    private static ToolResult StopProcess(JsonElement arguments, bool force)
    {
        Process? process = null;

        if (arguments.TryGetProperty("pid", out var pidProp) && pidProp.TryGetInt32(out var pid))
        {
            process = Process.GetProcessById(pid);
        }
        else if (arguments.TryGetProperty("process_name", out var nameProp) &&
                 !string.IsNullOrWhiteSpace(nameProp.GetString()))
        {
            var name = nameProp.GetString()!;
            process = Process.GetProcessesByName(name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        }

        if (process is null)
        {
            return ToolResult.Fail("Укажите pid или process_name");
        }

        var label = $"{process.ProcessName} (PID {process.Id})";
        if (force)
        {
            process.Kill(entireProcessTree: true);
            return ToolResult.Ok($"Процесс завершён: {label}");
        }

        process.CloseMainWindow();
        if (!process.WaitForExit(3000))
        {
            process.Kill(entireProcessTree: true);
            return ToolResult.Ok($"Процесс принудительно завершён: {label}");
        }

        return ToolResult.Ok($"Процесс остановлен: {label}");
    }

    private static int GetInt(JsonElement args, string name, int defaultValue, int min, int max)
    {
        if (!args.TryGetProperty(name, out var prop) || !prop.TryGetInt32(out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }
}