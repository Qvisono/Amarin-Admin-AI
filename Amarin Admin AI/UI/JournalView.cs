using System.Globalization;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI;

/// <summary>
/// One line of the journal, already in the words it will be shown in.
/// </summary>
/// <remarks>
/// Plain read-only strings rather than converters on the model: the list is rebuilt whenever
/// anything about it changes, so there is nothing to notify, and keeping the formatting here means
/// the row template in XAML has no logic in it at all. Both tabs feed the same shape, which is why
/// the fields are named by position instead of by meaning.
/// </remarks>
internal sealed class JournalRow
{
    /// <summary>Status mark. The template colours it from <see cref="IsFailure"/>/<see cref="IsPending"/>.</summary>
    public required string Glyph { get; init; }

    public required bool IsFailure { get; init; }

    public required bool IsPending { get; init; }

    /// <summary>Tool name, or the restore point's label.</summary>
    public required string Title { get; init; }

    /// <summary>Arguments the model passed, or where the restore point lives.</summary>
    public required string Subtitle { get; init; }

    public required string Timestamp { get; init; }

    /// <summary>Duration and chat on an action; the machine name on a restore point.</summary>
    public required string Trailer { get; init; }

    public required string Tooltip { get; init; }

    /// <summary>Chat to open on click. Null on rows that lead nowhere.</summary>
    public string? ChatId { get; init; }

    /// <summary>
    /// What the row was built from, kept so the details screen does not have to reconstruct it
    /// out of the formatted strings above. Exactly one of the two is set.
    /// </summary>
    public JournalEntry? Entry { get; init; }

    public SnapshotEntry? Snapshot { get; init; }
}

/// <summary>Turns journal data into rows the overlay can show.</summary>
internal static class JournalView
{
    /// <summary>
    /// Arguments are one line here even when the model sent them pretty-printed: the row has a
    /// fixed height, and a newline inside it would push everything below off the card.
    /// </summary>
    private const int SubtitleLimit = 200;

    public static JournalRow ToRow(JournalEntry entry, bool showChat)
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

        return new JournalRow
        {
            Glyph = pending ? "⋯" : failure ? "✕" : "✓",
            IsFailure = failure,
            IsPending = pending,
            Title = title,
            Subtitle = OneLine(entry.ArgumentsJson, SubtitleLimit),
            Timestamp = FormatTime(entry.StartedAt, entry.TimeIsApproximate),
            Trailer = trailer,
            Tooltip = BuildTooltip(entry),
            ChatId = entry.ChatId,
            Entry = entry
        };
    }

    public static JournalRow ToRow(SnapshotEntry snapshot)
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
                ? "—"
                : snapshot.Created.ToString("d MMM, HH:mm", CultureInfo.CurrentCulture),
            Trailer = snapshot.Machine,
            // Line break assembled here, not inside the caption: XAML would collapse it, and the
            // translator has no business owning the layout of a tooltip.
            Tooltip = Loc.Get("S.Journal.Snapshot.Tooltip") + "\n" + snapshot.Path,
            Snapshot = snapshot
        };
    }

    /// <summary>Everything a row can be searched by, lowercased once at build time.</summary>
    public static string SearchKey(JournalEntry entry) =>
        (entry.ToolName + " " + entry.ArgumentsJson + " " + entry.ResultPreview + " " +
         entry.ChatTitle + " " + entry.AgentName).ToLowerInvariant();

    public static string SearchKey(SnapshotEntry snapshot) =>
        (snapshot.Id + " " + snapshot.Label + " " + snapshot.Machine).ToLowerInvariant();

    /// <summary>One line of context under the title on the details screen.</summary>
    public static string BuildMeta(JournalEntry entry)
    {
        var parts = new List<string> { FormatTime(entry.StartedAt, entry.TimeIsApproximate) };

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

        return string.Join("  ·  ", parts);
    }

    public static string BuildMeta(SnapshotEntry snapshot)
    {
        var parts = new List<string>
        {
            snapshot.Created == DateTime.MinValue
                ? snapshot.Id
                : snapshot.Created.ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture)
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

    private static string FormatTime(DateTime at, bool approximate)
    {
        if (at == default)
        {
            return "—";
        }

        var text = at.Date == DateTime.Today
            ? at.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
            : at.ToString("d MMM, HH:mm", CultureInfo.CurrentCulture);

        // The tilde is the only hint on the row itself that the time came from the surrounding
        // message rather than the call; the tooltip spells it out.
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
