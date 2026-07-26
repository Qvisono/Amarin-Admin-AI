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
        // Get-WinEvent -ListLog * emits non-terminating errors for inaccessible logs → CLIXML on stderr
        // and can set non-zero exit. Prefer SilentlyContinue + explicit exit 0.
        var script = """
            $ErrorActionPreference = 'SilentlyContinue'
            $logs = @(Get-WinEvent -ListLog * -ErrorAction SilentlyContinue |
              Where-Object { $_.IsEnabled -and $_.RecordCount -gt 0 } |
              Sort-Object LogName |
              Select-Object -First 60 LogName, RecordCount, IsClassicLog)
            if ($logs.Count -eq 0) {
              Write-Output 'Доступных логов с событиями не найдено.'
              exit 0
            }
            $logs | Format-Table -AutoSize | Out-String -Width 200 | Write-Output
            exit 0
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
        var sourceScript = sourceFilter is not null
            ? $"| Where-Object {{ $_.ProviderName -like '*{sourceFilter}*' }}"
            : string.Empty;

        var safeLog = logName.Replace("'", "''", StringComparison.Ordinal);

        // "No events were found" is a normal empty result for smoke (e.g. critical_recent).
        // Never leave a non-zero exit or Error CLIXML that BuildResult treats as FAIL.
        var script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $start = (Get-Date).AddHours(-{{hours}})
            $filter = @{
              LogName = '{{safeLog}}'
              StartTime = $start
            }
            {{(levelFilter is not null ? $"$filter['Level'] = @({levelFilter})" : "")}}
            {{(eventId is not null ? $"$filter['Id'] = {eventId.Value}" : "")}}

            $events = @()
            try {
              $events = @(Get-WinEvent -FilterHashtable $filter -MaxEvents {{maxEvents}} -ErrorAction SilentlyContinue)
            } catch {
              # swallow — empty result below
            }

            if ($null -eq $events -or $events.Count -eq 0) {
              Write-Output 'Событий не найдено в {{safeLog}} за последние {{hours}} ч.'
              exit 0
            }

            $events {{sourceScript}} | ForEach-Object {
              $raw = if ($_.Message) { $_.Message } else { '' }
              $msg = if ($raw) {
                ($raw -replace '\s+', ' ').Substring(0, [Math]::Min(300, $raw.Length))
              } else { '' }
              "[{0:yyyy-MM-dd HH:mm:ss}] {1} | ID {2} | {3}" -f $_.TimeCreated, $_.LevelDisplayName, $_.Id, $_.ProviderName
              $msg
              ''
            }
            exit 0
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
