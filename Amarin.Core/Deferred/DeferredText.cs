using System.Globalization;

namespace Amarin.Core;

/// <summary>
/// Отложенная задача словами для человека — на языке интерфейса: когда сработает, как повторяется,
/// насколько опоздала.
/// </summary>
/// <remarks>
/// Модели то же самое говорит <see cref="DeferredTimeParser.Describe"/> по-английски: ответ
/// инструмента читает она, а вкладку «Отложенные», карточку напоминания и вопрос о команде — человек.
/// </remarks>
internal static class DeferredText
{
    /// <summary>Когда задача сработает: «завтра, 09:00», «каждый день в 09:00», «при включении ПК».</summary>
    public static string When(DeferredTask task, DeferredFacts facts)
    {
        var repeating = task.Repeat.Kind != DeferredRepeatKind.Once;
        return task.Trigger.Kind switch
        {
            DeferredTriggerKind.At => task.Repeat.Kind switch
            {
                DeferredRepeatKind.Every => Loc.Format("S.Deferred.When.Every", Span(TimeSpan.FromMinutes(task.Repeat.EveryMinutes))),
                DeferredRepeatKind.Daily => Loc.Format("S.Deferred.When.Daily", task.Repeat.Time ?? "09:00"),
                DeferredRepeatKind.Weekly => Loc.Format("S.Deferred.When.Weekly", Days(task.Repeat.Days), task.Repeat.Time ?? "09:00"),
                _ => task.NextDueUtc is { } due ? Moment(due, facts) : ""
            },
            DeferredTriggerKind.Uptime => Loc.Format(repeating ? "S.Deferred.When.UptimeEvery" : "S.Deferred.When.Uptime",
                Span(TimeSpan.FromHours(task.Trigger.UptimeHours ?? 0))),
            DeferredTriggerKind.NextBoot => Loc.Get(repeating ? "S.Deferred.When.EveryBoot" : "S.Deferred.When.NextBoot"),
            DeferredTriggerKind.NextAppStart => Loc.Get(repeating ? "S.Deferred.When.EveryStart" : "S.Deferred.When.NextStart"),
            _ => Loc.Format(repeating ? "S.Deferred.When.ConditionEvery" : "S.Deferred.When.Condition", Span(DeferredClock.CheckInterval(task)))
        };
    }

    /// <summary>Ближайший срок повторяющейся задачи по времени: «ближайший раз — завтра, 09:00»; иначе пусто.</summary>
    public static string Next(DeferredTask task, DeferredFacts facts) =>
        task.Status == DeferredStatus.Pending && task.Repeat.Kind != DeferredRepeatKind.Once && task.NextDueUtc is { } due
            ? Loc.Format("S.Deferred.When.Next", Moment(task.SnoozedUntilUtc ?? due, facts))
            : "";

    /// <summary>«сегодня, 18:30», «завтра, 09:00» или дата с временем.</summary>
    public static string Moment(DateTime utc, DeferredFacts facts)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, facts.Zone);
        var today = TimeZoneInfo.ConvertTimeFromUtc(facts.NowUtc, facts.Zone).Date;
        var clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return (local.Date - today).Days switch
        {
            0 => Loc.Format("S.Deferred.Today", clock),
            1 => Loc.Format("S.Deferred.Tomorrow", clock),
            _ => local.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) + ", " + clock
        };
    }

    /// <summary>Опоздание словами: «опоздало на 2 ч 10 мин».</summary>
    public static string Late(TimeSpan late) => Loc.Format("S.Deferred.Late", Span(late));

    /// <summary>Промежуток: «2 д 3 ч», «45 мин», «30 с».</summary>
    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalMinutes < 1)
        {
            return Loc.Format("S.Deferred.Span.Seconds", Math.Max(1, (int)span.TotalSeconds));
        }

        var parts = new List<string>();
        if (span.Days > 0)
        {
            parts.Add(Loc.Format("S.Deferred.Span.Days", span.Days));
        }

        if (span.Hours > 0)
        {
            parts.Add(Loc.Format("S.Deferred.Span.Hours", span.Hours));
        }

        if (span.Minutes > 0 && span.Days == 0)
        {
            parts.Add(Loc.Format("S.Deferred.Span.Minutes", span.Minutes));
        }

        return parts.Count == 0 ? Loc.Format("S.Deferred.Span.Minutes", 0) : string.Join(" ", parts);
    }

    private static string Days(IReadOnlyCollection<DayOfWeek> days) =>
        string.Join(", ", days.OrderBy(day => ((int)day + 6) % 7).Select(day => Loc.Get("S.Deferred.Day." + day)));
}
