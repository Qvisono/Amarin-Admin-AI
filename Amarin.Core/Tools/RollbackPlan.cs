using System.Text.RegularExpressions;
using Amarin.Core;

namespace Amarin.Tools;

internal enum ServiceRunAction
{
    None,
    Start,
    Stop
}

/// <summary>Вернуть службе тип запуска и/или состояние.</summary>
internal sealed record ServiceChange(string Name, string? StartTypeFrom, string? StartTypeTo, ServiceRunAction Run);

internal enum TaskChangeKind
{
    Enable,
    Disable,

    /// <summary>Зарегистрировать заново из копии XML, снятой до изменения.</summary>
    Recreate,

    /// <summary>Удалить задачу, которую создал этот запрос.</summary>
    Delete
}

internal sealed record TaskChange(TaskChangeKind Kind, string FullName, string? XmlFile = null);

/// <summary>Итог одного шага отката: строка плана, удался ли шаг и почему нет.</summary>
internal sealed record RollbackOutcome(string Line, bool Success, string? Error);

/// <summary>Что вернёт откат снимка — до того, как что-либо трогать.</summary>
/// <remarks>
/// Сравнение отдельно от применения: план показывают человеку («что вернётся»), и только после
/// его согласия план применяется. Прежний откат ничего не показывал и сразу переписывал все
/// службы, все задачи и импортировал реестр целиком.
/// </remarks>
internal sealed class RollbackPlan
{
    public required string SnapshotId { get; init; }

    public List<RegistryChange> Registry { get; } = [];

    public List<ServiceChange> Services { get; } = [];

    public List<TaskChange> Tasks { get; } = [];

    /// <summary>Файлы <c>.reg</c> снимка старого формата: у него нет состояния для сравнения.</summary>
    public List<string> LegacyRegFiles { get; } = [];

    /// <summary>Обратные шаги: то, что не вернуть сравнением (DNS, hosts, адаптеры, устройства, Wi-Fi).</summary>
    public List<UndoStep> Undo { get; } = [];

    /// <summary>Папка снимка — в ней файлы обратных шагов.</summary>
    public string? SnapshotDirectory { get; set; }

    /// <summary>Что откат видит, но не трогает, — человеческими словами.</summary>
    public List<string> Notes { get; } = [];

    public int Count => Registry.Count + Services.Count + Tasks.Count + LegacyRegFiles.Count + Undo.Count;

    public bool IsEmpty => Count == 0;

    /// <summary>Строки плана по порядку применения.</summary>
    public IEnumerable<string> Lines() =>
        Registry.Select(RollbackText.Describe)
            .Concat(LegacyRegFiles.Select(file => Loc.Format("S.Rollback.Legacy.Import", Path.GetFileName(file))))
            .Concat(Services.SelectMany(RollbackText.Describe))
            .Concat(Tasks.Select(RollbackText.Describe))
            .Concat(Undo.Select(UndoCommands.Describe));
}

/// <summary>Правила отката служб и задач. Без Windows — проверяются тестами напрямую.</summary>
internal static partial class RollbackRules
{
    private static readonly string[] StartTypes = ["Boot", "System", "Automatic", "Manual", "Disabled"];

    /// <summary>
    /// Службы, которые откат вернёт.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Тип запуска возвращается всегда: сам по себе он почти не меняется. Состояние — нет:
    /// службы «по требованию» запускаются и останавливаются сами, и прежний откат, возвращавший
    /// состояние всем трёмстам, гасил то, что Windows только что запустила по делу.
    /// </para>
    /// <para>
    /// Запустить можно службу, которую трогал этот запрос, у которой возвращается тип
    /// запуска или которая запускается автоматически. Остановить — только ту, которую трогал
    /// запрос или которой возвращается «вручную»/«отключена», и никогда — системную из
    /// <see cref="ProtectedSystemTargets.Services"/>.
    /// </para>
    /// </remarks>
    public static List<ServiceChange> PlanServices(
        IReadOnlyList<ServiceSnapshotEntry> before,
        IReadOnlyList<ServiceSnapshotEntry> now,
        IReadOnlySet<string> touched,
        List<string> notes)
    {
        var current = now
            .GroupBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var changes = new List<ServiceChange>();
        var drift = 0;

        foreach (var was in before
                     .GroupBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First())
                     .OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!current.TryGetValue(was.Name, out var present))
            {
                if (touched.Contains(was.Name))
                {
                    notes.Add(Loc.Format("S.Rollback.Note.ServiceGone", was.Name));
                }

                continue;
            }

            var startType = KnownStartType(was.StartType);
            var startChanged = startType is not null &&
                               !string.Equals(startType, present.StartType, StringComparison.OrdinalIgnoreCase);
            var wasRunning = IsRunning(was.Status);
            var isRunning = IsRunning(present.Status);
            var statusChanged = wasRunning != isRunning &&
                                (IsSettled(was.Status) && IsSettled(present.Status));
            var ours = touched.Contains(was.Name);

            var run = ServiceRunAction.None;
            var explained = false;
            if (statusChanged && wasRunning &&
                (ours || startChanged || string.Equals(startType, "Automatic", StringComparison.OrdinalIgnoreCase)))
            {
                run = ServiceRunAction.Start;
            }
            else if (statusChanged && !wasRunning &&
                     (ours || (startChanged && startType is "Manual" or "Disabled")))
            {
                if (ProtectedSystemTargets.IsProtectedService(was.Name))
                {
                    notes.Add(Loc.Format("S.Rollback.Note.Protected", was.Name));
                    explained = true;
                }
                else
                {
                    run = ServiceRunAction.Stop;
                }
            }

            if (!startChanged && run == ServiceRunAction.None)
            {
                if (statusChanged && !explained)
                {
                    drift++;
                }

                continue;
            }

            changes.Add(new ServiceChange(
                was.Name,
                startChanged ? present.StartType : null,
                startChanged ? startType : null,
                run));
        }

        if (drift > 0)
        {
            notes.Add(Loc.Format("S.Rollback.Note.ServiceDrift", drift));
        }

        return changes;
    }

    /// <summary>Задачи планировщика, которые откат вернёт.</summary>
    /// <param name="created">Задачи, созданные запросом (полные имена).</param>
    /// <param name="copies">Копии XML, снятые до изменения: полное имя → файл.</param>
    public static List<TaskChange> PlanTasks(
        IReadOnlyList<ScheduledTaskSnapshotEntry> before,
        IReadOnlyList<ScheduledTaskSnapshotEntry> now,
        IReadOnlySet<string> created,
        IReadOnlyDictionary<string, string> copies,
        List<string> notes)
    {
        var was = ToMap(before);
        var current = ToMap(now);
        var changes = new List<TaskChange>();
        var foreign = 0;

        foreach (var (name, entry) in was.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            copies.TryGetValue(name, out var copy);
            if (!current.TryGetValue(name, out var present))
            {
                if (copy is not null)
                {
                    changes.Add(new TaskChange(TaskChangeKind.Recreate, name, copy));
                }
                else
                {
                    notes.Add(Loc.Format("S.Rollback.Note.TaskNoCopy", name));
                }

                continue;
            }

            // Задачу, которую запрос перезаписал созданием с тем же именем, возвращает её копия
            // целиком — включённость в ней уже записана.
            if (copy is not null && created.Contains(name))
            {
                changes.Add(new TaskChange(TaskChangeKind.Recreate, name, copy));
                continue;
            }

            var wasEnabled = IsEnabled(entry.State);
            if (wasEnabled != IsEnabled(present.State))
            {
                changes.Add(new TaskChange(wasEnabled ? TaskChangeKind.Enable : TaskChangeKind.Disable, name));
            }
        }

        foreach (var name in current.Keys.Where(name => !was.ContainsKey(name))
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (created.Contains(name))
            {
                changes.Add(new TaskChange(TaskChangeKind.Delete, name));
            }
            else
            {
                foreign++;
            }
        }

        if (foreign > 0)
        {
            notes.Add(Loc.Format("S.Rollback.Note.TaskNotOurs", foreign));
        }

        return changes;
    }

    /// <summary>
    /// Шаги реестра, кроме удаления разделов служб. Раздел службы, появившийся после снимка, —
    /// это чаще драйвер, который поставил Windows Update, чем след этого запроса, а удалить его
    /// значит не загрузиться.
    /// </summary>
    public static List<RegistryChange> FilterRegistry(IEnumerable<RegistryChange> changes, List<string> notes)
    {
        var kept = new List<RegistryChange>();
        foreach (var change in changes)
        {
            if (change.Kind == RegistryChangeKind.DeleteKey && ServiceKey().Match(change.KeyPath) is { Success: true } match)
            {
                notes.Add(Loc.Format("S.Rollback.Note.ServiceKey", match.Groups["name"].Value));
                continue;
            }

            kept.Add(change);
        }

        return kept;
    }

    /// <summary>Автозагрузка вне Run-ключей (папка «Автозагрузка», WMI) — только сообщается.</summary>
    public static void NoteStartup(
        IReadOnlyList<StartupProgramSnapshotEntry> before,
        IReadOnlyList<StartupProgramSnapshotEntry> now,
        List<string> notes)
    {
        static IEnumerable<string> Outside(IEnumerable<StartupProgramSnapshotEntry> items) =>
            items.Where(item => string.Equals(item.Source, "WMI", StringComparison.OrdinalIgnoreCase) &&
                                !(item.Location ?? "").Contains("Run", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Name + "\u0001" + item.Command);

        var was = Outside(before).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = Outside(now).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = was.Except(current, StringComparer.OrdinalIgnoreCase)
            .Concat(current.Except(was, StringComparer.OrdinalIgnoreCase))
            .Select(key => key.Split('\u0001')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (changed.Count > 0)
        {
            notes.Add(Loc.Format("S.Rollback.Note.StartupOther", string.Join(", ", changed)));
        }
    }

    /// <summary>Полное имя задачи в одном написании: <c>\Папка\Имя</c>.</summary>
    public static string TaskFullName(string? taskPath, string? taskName)
    {
        var path = string.IsNullOrWhiteSpace(taskPath) ? "\\" : taskPath.Trim();
        if (!path.StartsWith('\\'))
        {
            path = "\\" + path;
        }

        if (!path.EndsWith('\\'))
        {
            path += "\\";
        }

        return path + (taskName ?? "").Trim().Trim('\\');
    }

    /// <summary>Имя задачи так, как его передала модель: «Имя» или «\Папка\Имя».</summary>
    public static string TaskFullName(string name)
    {
        var trimmed = name.Trim();
        var slash = trimmed.LastIndexOf('\\');
        return slash < 0
            ? TaskFullName("\\", trimmed)
            : TaskFullName(trimmed[..(slash + 1)], trimmed[(slash + 1)..]);
    }

    /// <summary>
    /// «Выключена» против всего остального. Running, Ready и Queued — одна и та же включённая
    /// задача в разные минуты; прежний откат считал «Ready → Running» изменением.
    /// </summary>
    public static bool IsEnabled(string? state) =>
        !string.Equals(state?.Trim(), "Disabled", StringComparison.OrdinalIgnoreCase) && state?.Trim() != "1";

    private static Dictionary<string, ScheduledTaskSnapshotEntry> ToMap(IEnumerable<ScheduledTaskSnapshotEntry> tasks) =>
        tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.TaskName))
            .GroupBy(task => TaskFullName(task.TaskPath, task.TaskName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    private static string? KnownStartType(string? value) =>
        StartTypes.FirstOrDefault(type => string.Equals(type, value?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool IsRunning(string? status) =>
        string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "StartPending", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "ContinuePending", StringComparison.OrdinalIgnoreCase);

    /// <summary>Приостановленная служба — не «остановлена» и не «работает»; такую не трогаем.</summary>
    private static bool IsSettled(string? status) =>
        !string.Equals(status, "Paused", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(status, "PausePending", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^HKLM\\SYSTEM\\(CurrentControlSet|ControlSet\d{3})\\Services\\(?<name>[^\\]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServiceKey();
}

/// <summary>Строки плана и итога — на языке интерфейса.</summary>
internal static class RollbackText
{
    private static readonly string[] StartupKeys =
    [
        @"\Microsoft\Windows\CurrentVersion\Run",
        @"\Microsoft\Windows\CurrentVersion\RunOnce",
        @"\Explorer\StartupApproved\Run"
    ];

    public static string Describe(RegistryChange change)
    {
        var name = string.IsNullOrEmpty(change.ValueName) ? "(default)" : change.ValueName;
        var startup = IsStartupKey(change.KeyPath);

        return change.Kind switch
        {
            RegistryChangeKind.SetValue when startup =>
                Loc.Format("S.Rollback.Startup.Restore", name, change.KeyPath),
            RegistryChangeKind.DeleteValue when startup =>
                Loc.Format("S.Rollback.Startup.Remove", name, change.KeyPath),
            RegistryChangeKind.SetValue =>
                Loc.Format("S.Rollback.Registry.Set", name, change.KeyPath, change.Value?.Preview() ?? ""),
            RegistryChangeKind.DeleteValue =>
                Loc.Format("S.Rollback.Registry.Delete", name, change.KeyPath),
            RegistryChangeKind.CreateKey => Loc.Format("S.Rollback.Registry.CreateKey", change.KeyPath),
            _ => Loc.Format("S.Rollback.Registry.DeleteKey", change.KeyPath)
        };
    }

    /// <summary>Служба может дать две строки: тип запуска и запуск/остановку.</summary>
    public static IEnumerable<string> Describe(ServiceChange change)
    {
        if (change.StartTypeTo is not null)
        {
            yield return Loc.Format("S.Rollback.Service.StartType", change.Name, change.StartTypeFrom ?? "?",
                change.StartTypeTo);
        }

        if (change.Run == ServiceRunAction.Start)
        {
            yield return Loc.Format("S.Rollback.Service.Start", change.Name);
        }
        else if (change.Run == ServiceRunAction.Stop)
        {
            yield return Loc.Format("S.Rollback.Service.Stop", change.Name);
        }
    }

    public static string Describe(TaskChange change) => change.Kind switch
    {
        TaskChangeKind.Enable => Loc.Format("S.Rollback.Task.Enable", change.FullName),
        TaskChangeKind.Disable => Loc.Format("S.Rollback.Task.Disable", change.FullName),
        TaskChangeKind.Recreate => Loc.Format("S.Rollback.Task.Recreate", change.FullName),
        _ => Loc.Format("S.Rollback.Task.Delete", change.FullName)
    };

    /// <summary>План целиком — для модели и для строки «что вернётся».</summary>
    public static string Summary(RollbackPlan plan)
    {
        var lines = new List<string>
        {
            plan.IsEmpty
                ? Loc.Get("S.Rollback.Nothing")
                : Loc.Format("S.Rollback.Header", plan.SnapshotId, plan.Count)
        };
        lines.AddRange(plan.Lines().Select(line => "• " + line));
        if (plan.Notes.Count > 0)
        {
            lines.Add("");
            lines.AddRange(plan.Notes.Select(note => "– " + note));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Итог применения: сколько удалось и что нет.</summary>
    public static string Result(IReadOnlyList<RollbackOutcome> outcomes)
    {
        var ok = outcomes.Count(outcome => outcome.Success);
        var lines = new List<string> { Loc.Format("S.Rollback.Done", ok, outcomes.Count) };
        lines.AddRange(outcomes.Select(outcome => outcome.Success
            ? "✓ " + outcome.Line
            : "✕ " + outcome.Line + " — " + outcome.Error));
        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsStartupKey(string keyPath) =>
        StartupKeys.Any(suffix => keyPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
