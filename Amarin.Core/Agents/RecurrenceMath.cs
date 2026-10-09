namespace Amarin.Core;

/// <summary>
/// Арифметика повторов «в это время суток по этим дням» и «каждые столько» — одна на расписание
/// агента (<see cref="ScheduleClock"/>) и отложенные задачи (<see cref="DeferredClock"/>).
/// </summary>
/// <remarks>
/// Чистые функции от «сейчас»: часы подменяются в тестах, а не ждутся. Время суток считается в
/// местном времени — «каждый день в 9:00» значит девять утра и после перехода на летнее время.
/// </remarks>
internal static class RecurrenceMath
{
    /// <summary>
    /// Последний момент «в это время суток» (по этим дням недели) не позже <paramref name="now"/>.
    /// </summary>
    /// <param name="days">Дни недели; null — каждый день; пустой список — никогда (null в ответе).</param>
    public static DateTime? LastOccurrence(TimeSpan time, IReadOnlyCollection<DayOfWeek>? days, DateTime now)
    {
        if (days is { Count: 0 })
        {
            return null;
        }

        for (var back = 0; back <= 7; back++)
        {
            var moment = now.Date.AddDays(-back) + time;
            if (moment <= now && (days is null || days.Contains(moment.DayOfWeek)))
            {
                return moment;
            }
        }

        return null;
    }

    /// <summary>Первый момент «в это время суток» (по этим дням недели) строго позже <paramref name="now"/>.</summary>
    /// <inheritdoc cref="LastOccurrence"/>
    public static DateTime? NextOccurrence(TimeSpan time, IReadOnlyCollection<DayOfWeek>? days, DateTime now)
    {
        if (days is { Count: 0 })
        {
            return null;
        }

        for (var ahead = 0; ahead <= 8; ahead++)
        {
            var moment = now.Date.AddDays(ahead) + time;
            if (moment > now && (days is null || days.Contains(moment.DayOfWeek)))
            {
                return moment;
            }
        }

        return null;
    }

    /// <summary>
    /// Первая точка сетки <c>anchor + k·period</c> строго позже <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// По сетке от первого срока, а не «через период от последнего запуска»: запуск, опоздавший на
    /// минуту, иначе сдвигал бы все следующие, и «каждый час в :00» уползал бы к :07. Пропущенные
    /// точки (компьютер спал) не догоняются очередью — следующая точка всегда впереди.
    /// </remarks>
    public static DateTime NextOnGrid(DateTime anchor, TimeSpan period, DateTime now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        if (anchor > now)
        {
            return anchor;
        }

        var steps = (now - anchor).Ticks / period.Ticks + 1;
        return anchor + TimeSpan.FromTicks(period.Ticks * steps);
    }
}
