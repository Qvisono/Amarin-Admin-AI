using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed partial class ScheduledTaskTool : ITool
{
    /// <summary>
    /// Расписания, которые принимает <c>create</c>. Остальные значения <c>/sc</c> требуют
    /// ключей, которых у инструмента нет (<c>onevent</c> — XPath-запроса), а любая строка сверх
    /// списка прежде уходила в команду как есть — вместе с приписанными к ней ключами.
    /// </summary>
    internal static readonly IReadOnlyList<string> Schedules =
        ["once", "minute", "hourly", "daily", "weekly", "monthly", "onstart", "onlogon", "onidle"];

    private const int TimeoutSeconds = 60;

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
              "description": "Task name or path, without quotes"
            },
            "command": {
              "type": "string",
              "description": "Program and arguments to run, for create. Quote a path with spaces."
            },
            "schedule": {
              "type": "string",
              "enum": ["once", "minute", "hourly", "daily", "weekly", "monthly", "onstart", "onlogon", "onidle"],
              "description": "When to run, for create. Default daily."
            },
            "start_time": {
              "type": "string",
              "description": "Start time HH:mm (24h). Required for once, optional for the others."
            }
          },
          "required": ["action"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!arguments.TryGetProperty("action", out var actionProp) ||
                actionProp.ValueKind != JsonValueKind.String)
            {
                return Task.FromResult(ToolResult.Fail("Missing required parameter: action"));
            }

            var action = (actionProp.GetString() ?? "").Trim().ToLowerInvariant();
            var taskName = ReadString(arguments, "task_name");

            if (action == "list")
            {
                return Task.FromResult(Schtasks(["/query", "/fo", "LIST", "/v"], cancellationToken));
            }

            if (action == "create")
            {
                return Task.FromResult(TryBuildCreateArguments(
                        taskName,
                        ReadString(arguments, "command"),
                        ReadString(arguments, "schedule"),
                        ReadString(arguments, "start_time"),
                        out var createArguments,
                        out var createError)
                    ? Schtasks(createArguments, cancellationToken)
                    : ToolResult.Fail(createError));
            }

            if (!TryValidateTaskName(taskName, out var nameError))
            {
                return Task.FromResult(ToolResult.Fail(nameError));
            }

            var name = taskName!.Trim();
            string[]? args = action switch
            {
                "query" => ["/query", "/tn", name, "/fo", "LIST", "/v"],
                "run" => ["/run", "/tn", name],
                "enable" => ["/change", "/tn", name, "/enable"],
                "disable" => ["/change", "/tn", name, "/disable"],
                "delete" => ["/delete", "/tn", name, "/f"],
                _ => null
            };

            return Task.FromResult(args is null
                ? ToolResult.Fail($"Unknown action: {action}")
                : Schtasks(args, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Scheduled task error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Собирает аргументы <c>schtasks /create</c> или объясняет модели, что не так.
    /// </summary>
    /// <remarks>
    /// Кавычки в имени задачи отбиваются: имени они не нужны, а в строке команды разрывали
    /// значение. В <c>command</c> кавычки допустимы — путь с пробелами иначе не записать, — и
    /// безопасны: команда уходит одним элементом списка аргументов, и schtasks видит её целиком
    /// значением <c>/tr</c>, а не набором своих ключей.
    /// </remarks>
    internal static bool TryBuildCreateArguments(
        string? taskName,
        string? command,
        string? schedule,
        string? startTime,
        out IReadOnlyList<string> arguments,
        out string error)
    {
        arguments = [];
        if (!TryValidateTaskName(taskName, out error))
        {
            return false;
        }

        var run = command?.Trim() ?? "";
        if (run.Length == 0)
        {
            error = "command is required for create.";
            return false;
        }

        if (run.Any(char.IsControl))
        {
            error = "В command не должно быть переводов строки и управляющих символов: задача " +
                    "запускает одну команду. Если нужно несколько — положи их в скрипт .ps1 или " +
                    ".cmd и запускай его. Не повторяй вызов с той же командой.";
            return false;
        }

        var when = string.IsNullOrWhiteSpace(schedule) ? "daily" : schedule.Trim().ToLowerInvariant();
        if (!Schedules.Contains(when, StringComparer.Ordinal))
        {
            error = $"Недопустимое значение schedule: \"{schedule}\". Допустимые: " +
                    string.Join(", ", Schedules) +
                    ". Других ключей schtasks инструмент не принимает — не повторяй попытку с тем же значением.";
            return false;
        }

        var time = startTime?.Trim() ?? "";
        if (time.Length > 0 && !TimePattern().IsMatch(time))
        {
            error = $"Недопустимое значение start_time: \"{startTime}\". Нужен формат ЧЧ:мм (24 часа), например 09:30.";
            return false;
        }

        if (when == "once" && time.Length == 0)
        {
            error = "Для schedule \"once\" обязателен start_time в формате ЧЧ:мм.";
            return false;
        }

        var list = new List<string> { "/create", "/tn", taskName!.Trim(), "/tr", run, "/sc", when.ToUpperInvariant() };
        if (time.Length > 0)
        {
            list.Add("/st");
            list.Add(time);
        }

        list.Add("/f");
        arguments = list;
        error = "";
        return true;
    }

    internal static bool TryValidateTaskName(string? taskName, out string error)
    {
        var name = taskName?.Trim() ?? "";
        if (name.Length == 0)
        {
            error = "task_name is required.";
            return false;
        }

        if (name.Contains('"') || name.Any(char.IsControl))
        {
            error = "В task_name не должно быть кавычек и управляющих символов. Укажи имя или путь " +
                    "задачи как есть, без кавычек. Не повторяй вызов с тем же именем.";
            return false;
        }

        error = "";
        return true;
    }

    private static ToolResult Schtasks(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        NativeProcess.Run("schtasks.exe", arguments, TimeoutSeconds, cancellationToken);

    private static string? ReadString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    private static partial Regex TimePattern();
}
