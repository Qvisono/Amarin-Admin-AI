using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
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
                "list_logs" => Task.FromResult(ListLogs()),
                "read" => Task.FromResult(ReadEvents(arguments)),
                _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Event log error: {ex.Message}"));
        }
    }

    private static ToolResult ListLogs()
    {
        var rows = new List<(string Name, long Records, bool Classic)>();
        using var session = new EventLogSession();
        foreach (var name in session.GetLogNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var config = new EventLogConfiguration(name, session);
                if (!config.IsEnabled)
                {
                    continue;
                }

                var info = session.GetLogInformation(name, PathType.LogName);
                if (info.RecordCount is null or 0)
                {
                    continue;
                }

                rows.Add((name, info.RecordCount.Value, config.IsClassicLog));
                if (rows.Count >= 60)
                {
                    break;
                }
            }
            catch
            {
                // inaccessible log
            }
        }

        if (rows.Count == 0)
        {
            return ToolResult.Ok("Доступных логов с событиями не найдено.");
        }

        var sb = new StringBuilder();
        sb.AppendLine("LogName RecordCount IsClassicLog");
        foreach (var row in rows)
        {
            sb.AppendLine($"{row.Name} {row.Records} {row.Classic}");
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult ReadEvents(JsonElement arguments)
    {
        var (logName, hours, level, maxEvents) = ResolveQuery(arguments);
        int? eventId = arguments.TryGetProperty("event_id", out var idProp) && idProp.TryGetInt32(out var id)
            ? id
            : null;
        var sourceFilter = arguments.TryGetProperty("source", out var sourceProp) &&
                           sourceProp.ValueKind == JsonValueKind.String
            ? sourceProp.GetString()
            : null;

        var start = DateTime.UtcNow.AddHours(-hours);
        var startText = start.ToString("o", CultureInfo.InvariantCulture);
        var xpath = new StringBuilder();
        xpath.Append("*[System[TimeCreated[@SystemTime>='");
        xpath.Append(startText);
        xpath.Append("']");

        var levelNumber = LevelNumber(level);
        if (levelNumber is not null)
        {
            xpath.Append(" and Level=");
            xpath.Append(levelNumber.Value);
        }

        if (eventId is not null)
        {
            xpath.Append(" and EventID=");
            xpath.Append(eventId.Value);
        }

        xpath.Append("]]");

        var sb = new StringBuilder();
        var found = 0;
        try
        {
            var query = new EventLogQuery(logName, PathType.LogName, xpath.ToString())
            {
                ReverseDirection = true
            };
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null && found < maxEvents; record = reader.ReadEvent())
            {
                using (record)
                {
                    var provider = record.ProviderName ?? "";
                    if (!string.IsNullOrWhiteSpace(sourceFilter) &&
                        provider.IndexOf(sourceFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    var time = record.TimeCreated?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
                    var levelName = record.LevelDisplayName ?? record.Level?.ToString() ?? "";
                    sb.AppendLine($"[{time}] {levelName} | ID {record.Id} | {provider}");

                    var message = SafeMessage(record);
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        sb.AppendLine(message);
                    }

                    sb.AppendLine();
                    found++;
                }
            }
        }
        catch (EventLogNotFoundException)
        {
            return ToolResult.Fail($"Журнал не найден: {logName}");
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Fail($"Нет доступа к журналу {logName}.");
        }
        catch (EventLogException ex)
        {
            return ToolResult.Ok($"Событий не найдено в {logName} за последние {hours} ч. ({ex.Message})");
        }

        if (found == 0)
        {
            return ToolResult.Ok($"Событий не найдено в {logName} за последние {hours} ч.");
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static string SafeMessage(EventRecord record)
    {
        try
        {
            var raw = record.FormatDescription() ?? "";
            raw = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return raw.Length <= 300 ? raw : raw[..300];
        }
        catch
        {
            return "";
        }
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

    private static int? LevelNumber(string level) => level switch
    {
        "critical" => 1,
        "error" => 2,
        "warning" => 3,
        "information" => 4,
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
