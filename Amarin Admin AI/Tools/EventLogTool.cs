using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class EventLogTool : ITool
{
    public string Name => "event_log";
    public string Description =>
        "Read Windows Event Logs via Get-WinEvent: list logs, query events, or use presets for common diagnostics.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list_logs", "read"],
              "description": "list_logs or read events"
            },
            "preset": {
              "type": "string",
              "enum": [
                "critical_recent", "errors_last_hour", "app_errors_24h",
                "system_errors_24h", "security_events_24h", "all_errors_today"
              ],
              "description": "Optional preset (overrides log_name/hours/level when set)"
            },
            "log_name": {
              "type": "string",
              "description": "Log name: Application, System, Security, etc."
            },
            "hours": {
              "type": "integer",
              "description": "How many hours back to search (default 24, max 168)"
            },
            "level": {
              "type": "string",
              "enum": ["all", "error", "warning", "information", "critical"],
              "description": "Event level filter"
            },
            "event_id": {
              "type": "integer",
              "description": "Optional Event ID filter"
            },
            "source": {
              "type": "string",
              "description": "Optional source/provider filter"
            },
            "max_events": {
              "type": "integer",
              "description": "Maximum events to return (default 30, max 100)"
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
            return action switch
            {
                "list_logs" => await ListLogsAsync(cancellationToken),
                "read" => await ReadEventsAsync(arguments, cancellationToken),
                _ => ToolResult.Fail($"Unknown action: {action}")
            };
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Event log error: {ex.Message}");
        }
    }

    private static Task<ToolResult> ListLogsAsync(CancellationToken cancellationToken)
    {
        var script = """
            Get-WinEvent -ListLog * -ErrorAction SilentlyContinue |
              Where-Object { $_.IsEnabled -and $_.RecordCount -gt 0 } |
              Sort-Object LogName |
              Select-Object -First 60 LogName, RecordCount, IsClassicLog |
              Format-Table -AutoSize
            """;

        return PowerShellHelper.RunAsync(script, 90, cancellationToken);
    }

    private static Task<ToolResult> ReadEventsAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var (logName, hours, level, maxEvents) = ResolveQuery(arguments);

        int? eventId = arguments.TryGetProperty("event_id", out var idProp) && idProp.TryGetInt32(out var id)
            ? id
            : null;

        var sourceFilter = arguments.TryGetProperty("source", out var sourceProp) &&
                           sourceProp.ValueKind == JsonValueKind.String
            ? sourceProp.GetString()?.Replace("'", "''", StringComparison.Ordinal)
            : null;

        var levelFilter = BuildLevelFilter(level);
        var eventIdFilter = eventId is not null ? $"Id = {eventId.Value}" : null;
        var sourceScript = sourceFilter is not null
            ? $"| Where-Object {{ $_.ProviderName -like '*{sourceFilter}*' }}"
            : string.Empty;

        var script = $$"""
            $start = (Get-Date).AddHours(-{{hours}})
            $filter = @{
              LogName = '{{logName.Replace("'", "''", StringComparison.Ordinal)}}'
              StartTime = $start
            }
            {{(levelFilter is not null ? $"$filter['Level'] = @({levelFilter})" : "")}}
            {{(eventIdFilter is not null ? $"$filter['Id'] = {eventId!.Value}" : "")}}
            try {
              $events = Get-WinEvent -FilterHashtable $filter -MaxEvents {{maxEvents}} -ErrorAction Stop
            } catch {
              if ($_.Exception.Message -match 'No events were found') {
                'Событий не найдено в {{logName}} за последние {{hours}} ч.'
                return
              }
              throw
            }
            $events {{sourceScript}} | ForEach-Object {
              $msg = if ($_.Message) { ($_.Message -replace '\s+', ' ').Substring(0, [Math]::Min(300, $_.Message.Length)) } else { '' }
              "[{0:yyyy-MM-dd HH:mm:ss}] {1} | ID {2} | {3}" -f $_.TimeCreated, $_.LevelDisplayName, $_.Id, $_.ProviderName
              $msg
              ''
            }
            """;

        return PowerShellHelper.RunAsync(script, 120, cancellationToken);
    }

    private static (string LogName, int Hours, string Level, int MaxEvents) ResolveQuery(JsonElement arguments)
    {
        var maxEvents = GetInt(arguments, "max_events", 30, 1, 100);

        if (arguments.TryGetProperty("preset", out var presetProp) &&
            presetProp.ValueKind == JsonValueKind.String)
        {
            return presetProp.GetString()?.ToLowerInvariant() switch
            {
                "critical_recent" => ("System", 24, "critical", maxEvents),
                "errors_last_hour" => ("System", 1, "error", maxEvents),
                "app_errors_24h" => ("Application", 24, "error", maxEvents),
                "system_errors_24h" => ("System", 24, "error", maxEvents),
                "security_events_24h" => ("Security", 24, "warning", maxEvents),
                "all_errors_today" => ("System", 24, "error", Math.Min(maxEvents, 50)),
                _ => (
                    arguments.TryGetProperty("log_name", out var lp) && lp.ValueKind == JsonValueKind.String
                        ? lp.GetString() ?? "System"
                        : "System",
                    GetInt(arguments, "hours", 24, 1, 168),
                    arguments.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.String
                        ? lv.GetString()?.ToLowerInvariant() ?? "all"
                        : "all",
                    maxEvents)
            };
        }

        var logName = arguments.TryGetProperty("log_name", out var logProp) &&
                      logProp.ValueKind == JsonValueKind.String
            ? logProp.GetString() ?? "System"
            : "System";

        var hours = GetInt(arguments, "hours", 24, 1, 168);
        var level = arguments.TryGetProperty("level", out var levelProp) &&
                    levelProp.ValueKind == JsonValueKind.String
            ? levelProp.GetString()?.ToLowerInvariant() ?? "all"
            : "all";

        return (logName, hours, level, maxEvents);
    }

    private static string? BuildLevelFilter(string level) => level switch
    {
        "critical" => "1",
        "error" => "2",
        "warning" => "3",
        "information" => "4",
        _ => null
    };

    private static int GetInt(JsonElement args, string name, int defaultValue, int min, int max)
    {
        if (!args.TryGetProperty(name, out var prop) || !prop.TryGetInt32(out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }
}