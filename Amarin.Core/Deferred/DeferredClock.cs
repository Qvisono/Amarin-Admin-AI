using System.Globalization;

namespace Amarin.Core;

/// <summary>То, от чего зависят сроки: часы, время работы Windows, вход, запуск программы.</summary>
/// <param name="Uptime">Время работы Windows — как в Диспетчере задач (<see cref="Environment.TickCount64"/>).</param>
/// <param name="BootUtc">Когда Windows загрузилась.</param>
/// <param name="SessionStartedUtc">
/// Когда начался нынешний вход в Windows — то, что человек считает «включил компьютер». При
/// быстром запуске «Завершение работы» — это сон ядра, и загрузка не меняется, а вход меняется.
/// </param>
/// <param name="AppStartedUtc">Когда запущена программа.</param>
/// <param name="Zone">Часовой пояс для «каждый день в 9:00» — подменяется в тестах.</param>
internal readonly record struct DeferredFacts(
    DateTime NowUtc,
    TimeSpan Uptime,
    DateTime BootUtc,
    DateTime SessionStartedUtc,
    DateTime AppStartedUtc,
    TimeZoneInfo Zone);

/// <summary>Пределы отложенных задач: защита от бесконечных повторов и трат без человека.</summary>
internal static class DeferredLimits
{
    /// <summary>Сколько задач может ждать в одном профиле.</summary>
    public const int MaxPending = 50;

    /// <summary>Дальше этого срок не ставится: через год задача, скорее всего, уже никому не нужна.</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromDays(366);

    /// <summary>Опоздание меньше этого не называется: таймер и так срабатывает с точностью до секунд.</summary>
    public static readonly TimeSpan LateThreshold = TimeSpan.FromMinutes(2);

    /// <summary>Столько неудач подряд — и повторяющаяся задача останавливается, а не жжёт деньги дальше.</summary>
    public const int MaxConsecutiveFailures = 3;

    /// <summary>Потолок цены одного прогона агента по умолчанию, в долларах.</summary>
    public const decimal AgentRunCapUsd = 0.50m;

    /// <summary>Больше этого потолок прогона модель поставить не может.</summary>
    public const decimal MaxAgentRunCapUsd = 5m;

    /// <summary>Сколько прогон агента может идти, включая ожидание ответа на вопросы.</summary>
    public static readonly TimeSpan AgentDeadline = TimeSpan.FromHours(2);

    public const int MinCheckMinutes = 1;
    public const int MaxCheckMinutes = 24 * 60;

    /// <summary>
    /// Самый частый повтор «каждые N минут» по виду задачи: агент держит одно из трёх мест для
    /// ходов и стоит денег, команда запускает процесс, напоминание ничего не стоит.
    /// </summary>
    public static int MinEveryMinutes(DeferredKind kind) => kind switch
    {
        DeferredKind.Reminder => 1,
        DeferredKind.Command => 5,
        DeferredKind.Agent => 15,
        _ => 60
    };
}

/// <summary>
/// Сроки отложенных задач. Чистые функции от <see cref="DeferredFacts"/>: часы подменяются в
/// тестах, а не ждутся.
/// </summary>
/// <remarks>
/// <para>
/// Пропущенное (компьютер был выключен или спал) срабатывает <b>один</b> раз: следующий срок
/// повторяющейся задачи — первая точка строго после «сейчас», а не очередь из всех пропущенных.
/// Так же устроено расписание агента (<see cref="ScheduleClock"/>).
/// </para>
/// <para>
/// Разовый срок хранится моментом UTC — переезд в другой часовой пояс его не двигает. «Каждый
/// день в 9:00» пересчитывается по местным часам при каждой перестановке и смене времени
/// системы, поэтому переход на летнее время его не сдвигает.
/// </para>
/// </remarks>
internal static class DeferredClock
{
    /// <summary>Положение дел прямо сейчас — от часов, Windows и процесса.</summary>
    public static DeferredFacts Now() =>
        new(DateTime.UtcNow, SystemSession.Uptime, SystemSession.BootUtc, SystemSession.SessionStartedUtc, SystemSession.AppStartedUtc, TimeZoneInfo.Local);

    /// <summary>Пора ли выполнять задачу. Задачи по условию решает проверка (<see cref="NeedsProbe"/>).</summary>
    public static bool IsDue(DeferredTask task, DeferredFacts facts)
    {
        if (task.Status != DeferredStatus.Pending)
        {
            return false;
        }

        if (task.SnoozedUntilUtc is { } snoozed)
        {
            return facts.NowUtc >= snoozed;
        }

        var since = task.LastFiredUtc ?? task.CreatedUtc;
        return task.Trigger.Kind switch
        {
            DeferredTriggerKind.At => task.NextDueUtc is { } due && facts.NowUtc >= due,
            DeferredTriggerKind.Uptime => task.Trigger.UptimeHours is { } hours &&
                                          facts.Uptime >= TimeSpan.FromHours(hours) &&
                                          !FiredThisBoot(task, facts),
            DeferredTriggerKind.NextBoot => facts.SessionStartedUtc > since,
            DeferredTriggerKind.NextAppStart => facts.AppStartedUtc > since,
            _ => false
        };
    }

    /// <summary>Пора ли проверить условие задачи.</summary>
    public static bool NeedsProbe(DeferredTask task, DeferredFacts facts) =>
        task.Status == DeferredStatus.Pending &&
        task.Trigger.Kind == DeferredTriggerKind.Condition &&
        (task.SnoozedUntilUtc is null || facts.NowUtc >= task.SnoozedUntilUtc) &&
        facts.NowUtc >= (task.LastProbeUtc ?? DateTime.MinValue) + CheckInterval(task);

    public static TimeSpan CheckInterval(DeferredTask task) =>
        TimeSpan.FromMinutes(Math.Clamp(task.Trigger.CheckEveryMinutes, DeferredLimits.MinCheckMinutes, DeferredLimits.MaxCheckMinutes));

    /// <summary>
    /// Отмечает срабатывание и ставит следующий срок. False — задача разовая, и это был её
    /// последний раз.
    /// </summary>
    public static bool Rearm(DeferredTask task, DeferredFacts facts)
    {
        var fired = task.NextDueUtc;
        task.LastFiredUtc = facts.NowUtc;
        task.Occurrences++;
        task.SnoozedUntilUtc = null;
        if (task.Trigger.Kind == DeferredTriggerKind.Uptime)
        {
            task.BootAtFireUtc = facts.BootUtc;
        }

        switch (task.Repeat.Kind)
        {
            case DeferredRepeatKind.Every:
                var period = TimeSpan.FromMinutes(Math.Max(DeferredLimits.MinEveryMinutes(task.Kind), task.Repeat.EveryMinutes));
                task.NextDueUtc = RecurrenceMath.NextOnGrid(fired ?? facts.NowUtc, period, facts.NowUtc);
                return true;
            case DeferredRepeatKind.Daily or DeferredRepeatKind.Weekly:
                task.NextDueUtc = NextLocal(task.Repeat, facts);
                return task.NextDueUtc is not null;
            case DeferredRepeatKind.EveryAppStart or DeferredRepeatKind.EveryBoot:
                return true;
            default:
                task.NextDueUtc = null;
                return false;
        }
    }

    /// <summary>
    /// Пересчитывает «каждый день в 9:00» по нынешним часам — после смены времени или часового
    /// пояса системы. Разовые сроки не трогает: момент UTC от этого не меняется.
    /// </summary>
    public static void Recompute(DeferredTask task, DeferredFacts facts)
    {
        if (task.Status == DeferredStatus.Pending && task.SnoozedUntilUtc is null &&
            task.Repeat.Kind is DeferredRepeatKind.Daily or DeferredRepeatKind.Weekly &&
            task.Trigger.Kind == DeferredTriggerKind.At &&
            NextLocal(task.Repeat, facts with { NowUtc = facts.NowUtc.AddSeconds(-1) }) is { } next)
        {
            task.NextDueUtc = next;
        }
    }

    /// <summary>Следующий срок «в это время суток по этим дням» в местных часах, моментом UTC.</summary>
    public static DateTime? NextLocal(DeferredRepeat repeat, DeferredFacts facts)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(facts.NowUtc, facts.Zone);
        var days = repeat.Kind == DeferredRepeatKind.Weekly ? repeat.Days : null;
        if (RecurrenceMath.NextOccurrence(TimeOf(repeat), days, local) is not { } next)
        {
            return null;
        }

        // Час, которого в эту ночь нет (переход на летнее время), — следующий существующий.
        while (facts.Zone.IsInvalidTime(next))
        {
            next = next.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(next, DateTimeKind.Unspecified), facts.Zone);
    }

    /// <summary>Время суток правила; без него — девять утра.</summary>
    public static TimeSpan TimeOf(DeferredRepeat repeat) =>
        TimeSpan.TryParseExact(repeat.Time?.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var time) &&
        time >= TimeSpan.Zero && time < TimeSpan.FromDays(1)
            ? time
            : new TimeSpan(9, 0, 0);

    /// <summary>Насколько опоздало срабатывание; null — вовремя или срока не было.</summary>
    public static TimeSpan? Lateness(DeferredTask task, DeferredFacts facts)
    {
        var due = task.SnoozedUntilUtc ?? (task.Trigger.Kind switch
        {
            DeferredTriggerKind.At => task.NextDueUtc,
            DeferredTriggerKind.Uptime when task.Trigger.UptimeHours is { } hours => facts.BootUtc + TimeSpan.FromHours(hours),
            _ => null
        });

        return due is { } moment && facts.NowUtc - moment > DeferredLimits.LateThreshold ? facts.NowUtc - moment : null;
    }

    /// <summary>
    /// Ближайший момент, к которому программа должна быть запущена, — для Планировщика Windows;
    /// null — сроков по времени нет (событиям и условиям хватает запуска при входе).
    /// </summary>
    public static DateTime? WakeAtUtc(IEnumerable<DeferredTask> tasks, DeferredFacts facts)
    {
        DateTime? earliest = null;
        foreach (var task in tasks.Where(task => task.Status == DeferredStatus.Pending))
        {
            var due = task.SnoozedUntilUtc ?? (task.Trigger.Kind switch
            {
                DeferredTriggerKind.At => task.NextDueUtc,
                DeferredTriggerKind.Uptime when task.Trigger.UptimeHours is { } hours && !FiredThisBoot(task, facts) =>
                    facts.BootUtc + TimeSpan.FromHours(hours),
                _ => null
            });

            if (due is { } moment && (earliest is null || moment < earliest))
            {
                earliest = moment;
            }
        }

        return earliest;
    }

    /// <summary>Задача по времени работы уже сработала в эту загрузку Windows.</summary>
    private static bool FiredThisBoot(DeferredTask task, DeferredFacts facts) =>
        task.BootAtFireUtc is { } boot && Math.Abs((boot - facts.BootUtc).TotalMinutes) < 2;
}
