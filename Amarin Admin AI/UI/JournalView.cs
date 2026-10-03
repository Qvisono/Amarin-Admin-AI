using System.Globalization;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI;

/// <summary>
/// Строка журнала — уже теми словами, какими будет показана.
/// </summary>
/// <remarks>
/// Готовые строки, а не конвертеры на модели: список пересобирается при любом изменении, сообщать
/// нечего, а форматирование здесь оставляет шаблон строки в XAML без логики. Обе вкладки дают одну
/// форму, поэтому поля названы по месту, а не по смыслу.
/// </remarks>
internal sealed class JournalRow
{
    /// <summary>Status mark. The template colours it from <see cref="IsFailure"/>/<see cref="IsPending"/>.</summary>
    public required string Glyph { get; init; }

    public required bool IsFailure { get; init; }

    public required bool IsPending { get; init; }

    /// <summary>Tool name, or the restore point's label.</summary>
    public required string Title { get; init; }

    /// <summary>Аргументы от модели или где лежит точка восстановления.</summary>
    public required string Subtitle { get; init; }

    public required string Timestamp { get; init; }

    /// <summary>Длительность и чат у действия; имя машины у точки восстановления.</summary>
    public required string Trailer { get; init; }

    public required string Tooltip { get; init; }

    /// <summary>Чат, который откроется по щелчку; null — строка никуда не ведёт.</summary>
    public string? ChatId { get; init; }

    /// <summary>
    /// Из чего собрана строка — чтобы экран подробностей не восстанавливал это из готовых строк
    /// выше. Задано ровно одно из двух.
    /// </summary>
    public JournalEntry? Entry { get; init; }

    public SnapshotEntry? Snapshot { get; init; }

    /// <summary>Сводка чата, если строка пришла с одноимённой вкладки.</summary>
    public ChatSummaryEntry? Summary { get; init; }

    /// <summary>Точка восстановления Windows — соседка снимков на той же вкладке.</summary>
    public WindowsRestorePoint? RestorePoint { get; init; }

    /// <summary>Строка журнала аудита, если строка пришла с одноимённой вкладки.</summary>
    public AuditEntry? Audit { get; init; }

    /// <summary>Время строки для общей сортировки снимков и точек восстановления.</summary>
    public DateTime SortTime { get; init; }
}

/// <summary>Сводка одного чата — то, что показывает вкладка «Сводки».</summary>
/// <remarks>
/// Собирается из индекса чатов, а не из файлов переписок: вкладке нужны только заголовок,
/// сводка и время, и все они в индексе уже есть.
/// </remarks>
internal sealed record ChatSummaryEntry(string ChatId, string Title, string Text, DateTime UpdatedAt);

/// <summary>Превращает данные журнала в строки для показа.</summary>
internal static class JournalView
{
    /// <summary>
    /// Аргументы — одной строкой, даже если модель прислала их с отступами: у строки журнала
    /// постоянная высота, и перенос внутри вытолкнул бы всё ниже за край карточки.
    /// </summary>
    private const int SubtitleLimit = 200;

    /// <param name="format">
    /// Порядок даты из настроек. Параметром, а не статикой: у класса всё статическое, и
    /// изменяемое состояние пришлось бы возвращать на место в каждом тесте.
    /// </param>
    public static JournalRow ToRow(
        JournalEntry entry, bool showChat, DateFormat format = DateFormat.DayMonthShort)
    {
        var pending = entry.Status is ToolCallStatus.Pending or ToolCallStatus.Running;
        var failure = !pending && !entry.Success;

        var title = entry.ToolName;
        if (!string.IsNullOrWhiteSpace(entry.AgentName))
        {
            title += "  ·  " + entry.AgentName;
        }

        var trailer = FormatDuration(entry.Duration);
        if (showChat)
        {
            var chat = MainWindow.DisplayTitle(entry.ChatTitle);
            trailer = trailer.Length == 0 ? chat : trailer + "  ·  " + chat;
        }

        // Иначе человек открыл бы чат и не нашёл там вызова: он в спрятанном варианте ответа.
        if (entry.InHiddenVariant)
        {
            var hidden = Loc.Get("S.Journal.HiddenVariant");
            trailer = trailer.Length == 0 ? hidden : trailer + "  ·  " + hidden;
        }

        return new JournalRow
        {
            Glyph = pending ? "⋯" : failure ? "✕" : "✓",
            IsFailure = failure,
            IsPending = pending,
            Title = title,
            Subtitle = OneLine(entry.ArgumentsJson, SubtitleLimit),
            Timestamp = FormatTime(entry.StartedAt, entry.TimeIsApproximate, format),
            Trailer = trailer,
            Tooltip = BuildTooltip(entry),
            ChatId = entry.ChatId,
            Entry = entry
        };
    }

    public static JournalRow ToRow(SnapshotEntry snapshot, DateFormat format = DateFormat.DayMonthShort)
    {
        var label = string.IsNullOrWhiteSpace(snapshot.Label)
            ? Loc.Get("S.Journal.Snapshot.NoLabel")
            : snapshot.Label;

        return new JournalRow
        {
            Glyph = "↺",
            IsFailure = false,
            IsPending = false,
            Title = label,
            Subtitle = snapshot.Id,
            Timestamp = snapshot.Created == DateTime.MinValue
                ? "-"
                : ChatFormat.DateTimeShort(snapshot.Created, format),
            Trailer = snapshot.Machine,
            // Перенос строки ставится здесь, а не в подписи: XAML его схлопнул бы, а раскладка
            // подсказки — не дело переводчика.
            Tooltip = Loc.Get("S.Journal.Snapshot.Tooltip") + "\n" + snapshot.Path,
            Snapshot = snapshot,
            SortTime = snapshot.Created
        };
    }

    public static JournalRow ToRow(WindowsRestorePoint point, DateFormat format = DateFormat.DayMonthShort)
    {
        ArgumentNullException.ThrowIfNull(point);

        return new JournalRow
        {
            Glyph = "◷",
            IsFailure = false,
            IsPending = false,
            Title = string.IsNullOrWhiteSpace(point.Description)
                ? Loc.Get("S.Journal.Snapshot.NoLabel")
                : point.Description,
            Subtitle = Loc.Format("S.Journal.RestorePoint.Subtitle", point.Sequence),
            Timestamp = point.Created == DateTime.MinValue
                ? "-"
                : ChatFormat.DateTimeShort(point.Created, format),
            Trailer = "",
            Tooltip = Loc.Get("S.Journal.RestorePoint.Tooltip"),
            RestorePoint = point,
            SortTime = point.Created
        };
    }

    public static string SearchKey(WindowsRestorePoint point) =>
        (point.Description + " " + point.Sequence.ToString(CultureInfo.InvariantCulture)).ToLowerInvariant();

    public static string BuildMeta(WindowsRestorePoint point, DateFormat format = DateFormat.DayMonthShort) =>
        (point.Created == DateTime.MinValue ? "-" : ChatFormat.DateTimeShort(point.Created, format)) +
        "  ·  " + Loc.Format("S.Journal.RestorePoint.Subtitle", point.Sequence);

    /// <summary>
    /// Вызов, создавший точку восстановления Windows: у его подробностей есть кнопка мастера
    /// «Восстановление системы» — вернуться к точке можно только им.
    /// </summary>
    public static bool IsRestorePointCreate(JournalEntry entry)
    {
        if (!string.Equals(entry.ToolName, "restore_point", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(entry.ArgumentsJson))
        {
            return false;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(entry.ArgumentsJson);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                   DangerousActionGuard.ActionOf(document.RootElement) == "create";
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    public static JournalRow ToRow(ChatSummaryEntry summary, DateFormat format = DateFormat.DayMonthShort)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new JournalRow
        {
            Glyph = "≡",
            IsFailure = false,
            IsPending = false,
            Title = summary.Title,
            Subtitle = summary.Text,
            Timestamp = summary.UpdatedAt == DateTime.MinValue
                ? "-"
                : ChatFormat.DateTimeShort(summary.UpdatedAt, format),
            Trailer = "",
            Tooltip = summary.Text,
            ChatId = summary.ChatId,
            Summary = summary
        };
    }

    /// <summary>Всё, по чему ищется строка, в нижнем регистре — один раз при сборке.</summary>
    public static string SearchKey(JournalEntry entry) =>
        (entry.ToolName + " " + entry.ArgumentsJson + " " + entry.ResultPreview + " " +
         entry.ChatTitle + " " + entry.AgentName).ToLowerInvariant();

    public static string SearchKey(SnapshotEntry snapshot) =>
        (snapshot.Id + " " + snapshot.Label + " " + snapshot.Machine).ToLowerInvariant();

    public static string SearchKey(ChatSummaryEntry summary) =>
        (summary.Title + " " + summary.Text).ToLowerInvariant();

    /// <param name="chatExists">
    /// Жив ли ещё чат. Журнал аудита переживает удалённые чаты — ради этого он и отдельный, — и
    /// строка обязана сказать, что открыть такой чат уже нельзя.
    /// </param>
    public static JournalRow ToRow(AuditEntry entry, bool chatExists, DateFormat format = DateFormat.DayMonthShort)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var title = entry.Tool;
        if (!string.IsNullOrWhiteSpace(entry.Agent))
        {
            title += "  ·  " + entry.Agent;
        }

        return new JournalRow
        {
            Glyph = entry.Outcome switch
            {
                AuditOutcome.Ok => "✓",
                AuditOutcome.Failed => "✕",
                AuditOutcome.Refused => "⊘",
                _ => "⋯"
            },
            IsFailure = entry.Outcome is AuditOutcome.Failed or AuditOutcome.Refused,
            IsPending = entry.Outcome is AuditOutcome.Cancelled,
            Title = title,
            Subtitle = OneLine(entry.Args, SubtitleLimit),
            Timestamp = FormatTime(entry.Time, approximate: false, format),
            Trailer = OutcomeLabel(entry.Outcome) + "  ·  " + ChatLabel(entry, chatExists),
            Tooltip = AuditTooltip(entry, chatExists),
            ChatId = chatExists ? entry.ChatId : null,
            Audit = entry,
            SortTime = entry.Time
        };
    }

    public static string SearchKey(AuditEntry entry) =>
        (entry.Tool + " " + entry.Args + " " + entry.Result + " " + entry.ChatTitle + " " + entry.Agent + " " +
         OutcomeLabel(entry.Outcome)).ToLowerInvariant();

    /// <summary>Строка под заголовком в подробностях: когда, чем кончилось, кто решал, что сказал SynGuard.</summary>
    public static string BuildMeta(AuditEntry entry, bool chatExists, DateFormat format = DateFormat.DayMonthShort) =>
        string.Join(
            "  ·  ",
            FormatTime(entry.Time, approximate: false, format),
            OutcomeLabel(entry.Outcome),
            ApprovalLabel(entry.ApprovedBy),
            GuardLabel(entry.Guard),
            ChatLabel(entry, chatExists));

    public static string OutcomeLabel(AuditOutcome outcome) => Loc.Get("S.Audit.Outcome." + outcome);

    public static string ApprovalLabel(ApprovalSource source) => Loc.Get("S.Audit.By." + source);

    public static string GuardLabel(AuditGuard guard) => Loc.Get("S.Audit.Guard." + guard);

    private static string ChatLabel(AuditEntry entry, bool chatExists)
    {
        if (string.IsNullOrEmpty(entry.ChatId))
        {
            return "-";
        }

        var title = MainWindow.DisplayTitle(entry.ChatTitle ?? "");
        return chatExists ? title : Loc.Format("S.Journal.Audit.ChatGone", title);
    }

    private static string AuditTooltip(AuditEntry entry, bool chatExists)
    {
        var text = entry.Tool + "\n" + BuildMeta(entry, chatExists);
        if (!string.IsNullOrWhiteSpace(entry.Args))
        {
            text += "\n\n" + Trim(entry.Args, 600);
        }

        if (!string.IsNullOrWhiteSpace(entry.Result))
        {
            text += "\n\n" + Loc.Get("S.Journal.Result") + "\n" + Trim(entry.Result, 600);
        }

        return text;
    }

    /// <summary>Строка пояснения под заголовком на экране подробностей.</summary>
    public static string BuildMeta(JournalEntry entry, DateFormat format = DateFormat.DayMonthShort)
    {
        var parts = new List<string> { FormatTime(entry.StartedAt, entry.TimeIsApproximate, format) };

        if (FormatDuration(entry.Duration) is { Length: > 0 } duration)
        {
            parts.Add(duration);
        }

        parts.Add(Loc.Get(entry.Status is ToolCallStatus.Pending or ToolCallStatus.Running
            ? "S.Journal.StatusRunning"
            : entry.Success ? "S.Journal.StatusDone" : "S.Journal.StatusFailed"));

        parts.Add(MainWindow.DisplayTitle(entry.ChatTitle));

        if (entry.TimeIsApproximate)
        {
            parts.Add(Loc.Get("S.Journal.TimeApproximate"));
        }

        if (entry.InHiddenVariant)
        {
            parts.Add(Loc.Get("S.Journal.HiddenVariant"));
        }

        return string.Join("  ·  ", parts);
    }

    public static string BuildMeta(SnapshotEntry snapshot, DateFormat format = DateFormat.DayMonthShort)
    {
        var parts = new List<string>
        {
            snapshot.Created == DateTime.MinValue
                ? snapshot.Id
                : ChatFormat.DateTimeShort(snapshot.Created, format)
        };

        if (!string.IsNullOrWhiteSpace(snapshot.Machine))
        {
            parts.Add(snapshot.Machine);
        }

        parts.Add(snapshot.Path);
        return string.Join("  ·  ", parts);
    }

    private static string BuildTooltip(JournalEntry entry)
    {
        var text = entry.ToolName;
        if (!string.IsNullOrWhiteSpace(entry.AgentName))
        {
            text += "\n" + Loc.Format("S.Journal.ByAgent", entry.AgentName);
        }

        if (!string.IsNullOrWhiteSpace(entry.ArgumentsJson))
        {
            text += "\n\n" + Trim(entry.ArgumentsJson, 600);
        }

        if (!string.IsNullOrWhiteSpace(entry.ResultPreview))
        {
            text += "\n\n" + Loc.Get("S.Journal.Result") + "\n" + Trim(entry.ResultPreview, 600);
        }

        if (entry.TimeIsApproximate)
        {
            text += "\n\n" + Loc.Get("S.Journal.TimeApproximate");
        }

        return text;
    }

    private static string FormatTime(DateTime at, bool approximate, DateFormat format)
    {
        if (at == default)
        {
            return "-";
        }

        // У сегодняшних записей дата не пишется вовсе — в журнале их большинство, и повторять
        // её в каждой строке значило бы съесть место под то, что и так видно.
        var text = at.Date == DateTime.Today
            ? at.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : ChatFormat.DateTimeShort(at, format);

        // Тильда — единственный знак на строке, что время взято у сообщения, а не у вызова;
        // подсказка объясняет это словами.
        return approximate ? "~" + text : text;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "";
        }

        return duration.TotalSeconds < 1
            ? ((int)duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms"
            : duration.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
    }

    private static string OneLine(string? text, int limit) =>
        Trim(string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), limit);

    private static string Trim(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "…";
}
