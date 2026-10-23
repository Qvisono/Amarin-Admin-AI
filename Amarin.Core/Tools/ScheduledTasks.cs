using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>Действие задачи: программа с аргументами или иное (обработчик COM, письмо, сообщение).</summary>
/// <param name="Path">Программа; null — действие не запускает программу.</param>
internal sealed record ScheduledTaskAction(string Kind, string? Path, string Arguments);

/// <summary>Задача Планировщика, как её видит его COM-интерфейс.</summary>
internal sealed record ScheduledTaskInfo(
    string Path,
    bool Enabled,
    string State,
    DateTime? NextRun,
    DateTime? LastRun,
    int LastResult,
    string? Author,
    string? Description,
    string? RunAs,
    bool Highest,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<ScheduledTaskAction> Actions);

/// <summary>
/// Чтение задач Планировщика через его COM-интерфейс (1.37.0): для <c>scheduled_task list/query</c>
/// и для аудита автозапуска.
/// </summary>
/// <remarks>
/// <para>
/// Не через текст <c>schtasks /query</c>: тот пишет в консоль её кодовой страницей, а у дочерней
/// консоли программы это OEM-страница системы. Кириллица в пути или имени задачи там, где
/// системная страница её не знает (437 на английской Windows с русским языком), становилась
/// «?????» ещё до программы, и никакая расшифровка её не вернула бы. COM отдаёт строки в Unicode.
/// </para>
/// <para>
/// Данные для модели — английские, единым видом: прежний текст <c>schtasks</c> был на языке
/// Windows и обрезался на шестнадцати тысячах знаков посреди списка.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ScheduledTasks
{
    /// <summary>У задачи ни разу не запускавшейся Планировщик отдаёт «нулевую» дату COM.</summary>
    private static readonly DateTime Never = new(1900, 1, 1);

    private const int HiddenToo = 1;

    /// <summary>Все задачи, включая скрытые. Папку без доступа пропускает, а не обрывает обход.</summary>
    public static List<ScheduledTaskInfo> All()
    {
        var tasks = new List<ScheduledTaskInfo>();
        Walk(Connect().GetFolder(@"\"), tasks);
        return tasks;
    }

    /// <summary>Задача по имени или пути; null — такой нет.</summary>
    public static ScheduledTaskInfo? Find(string name)
    {
        var path = name.Trim();
        if (!path.StartsWith('\\'))
        {
            path = @"\" + path;
        }

        try
        {
            return Read(Connect().GetFolder(@"\").GetTask(path));
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or DirectoryNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Список для модели: строка на задачу.</summary>
    public static string FormatList(IReadOnlyList<ScheduledTaskInfo> tasks)
    {
        if (tasks.Count == 0)
        {
            return "No scheduled tasks.";
        }

        var text = new StringBuilder();
        text.Append(tasks.Count).AppendLine(" tasks. path | state | next run | last run | last result | runs");
        foreach (var task in tasks.OrderBy(task => task.Path, StringComparer.OrdinalIgnoreCase))
        {
            text.Append(task.Path).Append(" | ")
                .Append(task.State).Append(" | ")
                .Append(Time(task.NextRun)).Append(" | ")
                .Append(Time(task.LastRun)).Append(" | ")
                .Append(Result(task)).Append(" | ")
                .AppendLine(string.Join("; ", task.Actions.Select(Describe)));
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Одна задача подробно.</summary>
    public static string FormatOne(ScheduledTaskInfo task)
    {
        var text = new StringBuilder()
            .Append("Task: ").AppendLine(task.Path)
            .Append("State: ").AppendLine(task.State)
            .Append("Next run: ").AppendLine(Time(task.NextRun))
            .Append("Last run: ").AppendLine(Time(task.LastRun))
            .Append("Last result: ").AppendLine(Result(task))
            .Append("Run as: ").Append(string.IsNullOrWhiteSpace(task.RunAs) ? "-" : task.RunAs)
            .AppendLine(task.Highest ? " (highest privileges)" : "")
            .Append("Author: ").AppendLine(string.IsNullOrWhiteSpace(task.Author) ? "-" : task.Author)
            .Append("Description: ").AppendLine(string.IsNullOrWhiteSpace(task.Description) ? "-" : task.Description)
            .AppendLine("Triggers:");
        foreach (var trigger in task.Triggers.DefaultIfEmpty("none"))
        {
            text.Append("- ").AppendLine(trigger);
        }

        text.AppendLine("Actions:");
        foreach (var action in task.Actions)
        {
            text.Append("- ").AppendLine(Describe(action));
        }

        return text.ToString().TrimEnd();
    }

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")
                   ?? throw new InvalidOperationException("Task Scheduler is not available");
        dynamic service = Activator.CreateInstance(type)
                          ?? throw new InvalidOperationException("Task Scheduler is not available");
        service.Connect();
        return service;
    }

    private static void Walk(dynamic folder, List<ScheduledTaskInfo> tasks)
    {
        try
        {
            foreach (var task in folder.GetTasks(HiddenToo))
            {
                try
                {
                    tasks.Add(Read(task));
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
                {
                    // Задача, чьё описание закрыто, не должна прятать соседние.
                }
            }

            foreach (var child in folder.GetFolders(0))
            {
                Walk(child, tasks);
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            // Папка, закрытая для этого пользователя: её задачи не видны и в «Планировщике».
        }
    }

    private static ScheduledTaskInfo Read(dynamic task)
    {
        var definition = task.Definition;
        var triggers = new List<string>();
        foreach (var trigger in definition.Triggers)
        {
            string start = trigger.StartBoundary ?? "";
            triggers.Add(TriggerKind((int)trigger.Type) + (start.Length > 0 ? " from " + start : "") +
                         ((bool)trigger.Enabled ? "" : " (disabled)"));
        }

        var actions = new List<ScheduledTaskAction>();
        foreach (var action in definition.Actions)
        {
            actions.Add((int)action.Type == 0
                ? new ScheduledTaskAction("exec", (string?)action.Path ?? "", (string?)action.Arguments ?? "")
                : new ScheduledTaskAction(ActionKind((int)action.Type), null, ""));
        }

        return new ScheduledTaskInfo(
            (string)task.Path,
            (bool)task.Enabled,
            StateName((int)task.State),
            Date((DateTime)task.NextRunTime),
            Date((DateTime)task.LastRunTime),
            (int)task.LastTaskResult,
            (string?)definition.RegistrationInfo.Author,
            (string?)definition.RegistrationInfo.Description,
            (string?)definition.Principal.UserId,
            (int)definition.Principal.RunLevel == 1,
            triggers,
            actions);
    }

    private static DateTime? Date(DateTime value) => value < Never ? null : value;

    private static string Time(DateTime? value) =>
        value?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-";

    private static string Result(ScheduledTaskInfo task) =>
        task.LastRun is null ? "-" : "0x" + task.LastResult.ToString("X8", CultureInfo.InvariantCulture);

    private static string Describe(ScheduledTaskAction action) =>
        action.Path is null ? action.Kind : (action.Path + " " + action.Arguments).Trim();

    private static string StateName(int state) => state switch
    {
        1 => "disabled",
        2 => "queued",
        3 => "ready",
        4 => "running",
        _ => "unknown"
    };

    private static string ActionKind(int type) => type switch
    {
        5 => "com handler",
        6 => "send email",
        7 => "show message",
        _ => "other"
    };

    private static string TriggerKind(int type) => type switch
    {
        0 => "on event",
        1 => "once",
        2 => "daily",
        3 => "weekly",
        4 => "monthly",
        5 => "monthly by weekday",
        6 => "on idle",
        7 => "on registration",
        8 => "at startup",
        9 => "at logon",
        11 => "on session change",
        12 => "custom",
        _ => "other"
    };
}
