using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>Когда задача сработает впервые и как повторится — разобранное из слов модели.</summary>
internal sealed record DeferredSchedule(DeferredTrigger Trigger, DeferredRepeat Repeat, DateTime? FirstDueUtc);

/// <summary>Срок, который нельзя поставить, — текст для модели: что исправить.</summary>
internal sealed class DeferredInputException(string message) : Exception(message);

/// <summary>
/// Разбор срока отложенной задачи из аргументов инструмента: «через 90 минут», «в 18:30»,
/// «2026-10-11 09:00», «при аптайме 48 часов», «при следующем включении», «когда скрипт скажет да»
/// и повтор — каждые N минут, ежедневно, по дням недели, при каждом запуске или включении.
/// </summary>
/// <remarks>
/// Модель не знает, который час: в промпте только дата. Поэтому сроки принимаются так, как их
/// сказал человек («через 2 часа», «в 9:00» — ближайшее), а ответ инструмента называет вычисленное
/// время и «сейчас» — модель видит, что получилось, и может поправить.
/// </remarks>
internal static partial class DeferredTimeParser
{
    private static readonly string[] TimeFormats = [@"h\:mm", @"hh\:mm"];

    private static readonly string[] MomentFormats =
    [
        "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd H:mm",
        "dd.MM.yyyy HH:mm", "dd.MM.yyyy H:mm", "dd.MM.yyyy", "yyyy-MM-dd"
    ];

    public static DeferredSchedule Resolve(JsonElement args, DeferredKind kind, DeferredFacts facts)
    {
        var repeatKind = RepeatOf(FileToolPaths.String(args, "repeat"));
        var inText = FileToolPaths.String(args, "in");
        var atText = FileToolPaths.String(args, "at");
        var uptime = Number(args, "uptime_hours");
        var eventText = FileToolPaths.String(args, "event")?.Trim().ToLowerInvariant();
        var condition = FileToolPaths.String(args, "condition");

        var trigger = new DeferredTrigger();
        var repeat = new DeferredRepeat { Kind = repeatKind };
        if (!string.IsNullOrWhiteSpace(condition))
        {
            trigger.Kind = DeferredTriggerKind.Condition;
            trigger.Condition = condition.Trim();
            var every = FileToolPaths.Int(args, "check_every_minutes") ?? 5;
            if (every < DeferredLimits.MinCheckMinutes || every > DeferredLimits.MaxCheckMinutes)
            {
                throw new DeferredInputException($"check_every_minutes must be between {DeferredLimits.MinCheckMinutes} and {DeferredLimits.MaxCheckMinutes}.");
            }

            trigger.CheckEveryMinutes = every;
            RequireOnly(("condition", true), ("in", inText), ("at", atText), ("uptime_hours", uptime), ("event", eventText));
            return new DeferredSchedule(trigger, Normalize(repeat, allowed: DeferredRepeatKind.EveryBoot), null);
        }

        if (uptime is { } hours)
        {
            if (hours <= 0 || hours > 24 * 365)
            {
                throw new DeferredInputException("uptime_hours must be a positive number of hours, such as 48 for two days.");
            }

            trigger.Kind = DeferredTriggerKind.Uptime;
            trigger.UptimeHours = hours;
            RequireOnly(("uptime_hours", true), ("in", inText), ("at", atText), ("event", eventText));
            return new DeferredSchedule(trigger, Normalize(repeat, allowed: DeferredRepeatKind.EveryBoot), null);
        }

        if (repeatKind is DeferredRepeatKind.EveryAppStart or DeferredRepeatKind.EveryBoot || eventText is not null)
        {
            trigger.Kind = eventText switch
            {
                "next_boot" or "boot" => DeferredTriggerKind.NextBoot,
                "next_start" or "start" => DeferredTriggerKind.NextAppStart,
                null => repeatKind == DeferredRepeatKind.EveryBoot ? DeferredTriggerKind.NextBoot : DeferredTriggerKind.NextAppStart,
                _ => throw new DeferredInputException($"event \"{eventText}\" is unknown. Use next_boot (the PC is turned on) or next_start (the program starts).")
            };

            if (repeatKind is not (DeferredRepeatKind.Once or DeferredRepeatKind.EveryAppStart or DeferredRepeatKind.EveryBoot))
            {
                throw new DeferredInputException("An event repeats only as every_start or every_boot.");
            }

            RequireOnly(("event", true), ("in", inText), ("at", atText));
            repeat.Kind = repeatKind switch
            {
                DeferredRepeatKind.Once => DeferredRepeatKind.Once,
                _ => trigger.Kind == DeferredTriggerKind.NextBoot ? DeferredRepeatKind.EveryBoot : DeferredRepeatKind.EveryAppStart
            };
            return new DeferredSchedule(trigger, repeat, null);
        }

        trigger.Kind = DeferredTriggerKind.At;
        var local = TimeZoneInfo.ConvertTimeFromUtc(facts.NowUtc, facts.Zone);
        DateTime first;
        switch (repeatKind)
        {
            case DeferredRepeatKind.Daily or DeferredRepeatKind.Weekly:
                if (string.IsNullOrWhiteSpace(atText) || !TryTimeOfDay(atText, out var time))
                {
                    throw new DeferredInputException("A daily or weekly task needs at as the time of day, such as at=\"09:00\".");
                }

                repeat.Time = time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
                if (repeatKind == DeferredRepeatKind.Weekly)
                {
                    repeat.Days = Days(args);
                    if (repeat.Days.Count == 0)
                    {
                        throw new DeferredInputException("A weekly task needs days, such as days=[\"mon\",\"thu\"].");
                    }
                }

                first = DeferredClock.NextLocal(repeat, facts) ?? throw new DeferredInputException("No such day ever comes.");
                break;

            case DeferredRepeatKind.Every:
                var minutes = FileToolPaths.Int(args, "every_minutes") ?? (inText is not null ? (int)Math.Round(Duration(inText).TotalMinutes) : 0);
                var minimum = DeferredLimits.MinEveryMinutes(kind);
                if (minutes < minimum)
                {
                    throw new DeferredInputException($"every_minutes must be at least {minimum} for this kind of task.");
                }

                repeat.EveryMinutes = minutes;
                first = !string.IsNullOrWhiteSpace(atText) ? Moment(atText, local, facts) : facts.NowUtc.AddMinutes(minutes);
                break;

            default:
                if (!string.IsNullOrWhiteSpace(inText) && !string.IsNullOrWhiteSpace(atText))
                {
                    throw new DeferredInputException("Give either in or at, not both.");
                }

                if (!string.IsNullOrWhiteSpace(inText))
                {
                    var delay = Duration(inText);
                    if (delay < TimeSpan.FromSeconds(5))
                    {
                        throw new DeferredInputException("in is too short: give at least a few seconds, such as in=\"10m\".");
                    }

                    first = facts.NowUtc + delay;
                }
                else if (!string.IsNullOrWhiteSpace(atText))
                {
                    first = Moment(atText, local, facts);
                }
                else
                {
                    throw new DeferredInputException(
                        "Say when: in (\"90m\", \"2h\", \"1d 3h\"), at (\"18:30\" or \"2026-10-11 09:00\"), uptime_hours, event (next_boot, next_start) or condition.");
                }

                break;
        }

        if (first <= facts.NowUtc)
        {
            throw new DeferredInputException("That time has already passed. Give a moment in the future.");
        }

        if (first - facts.NowUtc > DeferredLimits.MaxAhead)
        {
            throw new DeferredInputException("That is more than a year away. Give a nearer moment.");
        }

        return new DeferredSchedule(trigger, repeat, first);
    }

    private static DeferredRepeat Normalize(DeferredRepeat repeat, DeferredRepeatKind allowed)
    {
        if (repeat.Kind is not (DeferredRepeatKind.Once) && repeat.Kind != allowed)
        {
            // «Каждый раз, когда условие сбудется» и «каждую загрузку, когда аптайм дорастёт» —
            // один вид повтора: снова при следующем случае.
            repeat.Kind = allowed;
        }

        return repeat;
    }

    private static void RequireOnly((string Name, object? Value) main, params (string Name, object? Value)[] others)
    {
        var extra = others.Where(other => other.Value is string text ? !string.IsNullOrWhiteSpace(text) : other.Value is not null)
            .Select(other => other.Name).ToList();
        if (extra.Count > 0)
        {
            throw new DeferredInputException($"{main.Name} cannot be combined with {string.Join(", ", extra)}: give one way of saying when.");
        }
    }

    private static DeferredRepeatKind RepeatOf(string? text) => (text ?? "once").Trim().ToLowerInvariant() switch
    {
        "" or "once" or "none" => DeferredRepeatKind.Once,
        "every" or "interval" => DeferredRepeatKind.Every,
        "daily" or "every_day" => DeferredRepeatKind.Daily,
        "weekly" or "every_week" => DeferredRepeatKind.Weekly,
        "every_start" or "every_app_start" => DeferredRepeatKind.EveryAppStart,
        "every_boot" or "every_power_on" => DeferredRepeatKind.EveryBoot,
        var other => throw new DeferredInputException($"repeat \"{other}\" is unknown. Use once, every, daily, weekly, every_start or every_boot.")
    };

    private static double? Number(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString()?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>Длительность: «90m», «2h», «1d 3h», «1ч 30мин», «PT2H», число — минуты.</summary>
    internal static TimeSpan Duration(string text)
    {
        var value = text.Trim();
        if (value.Length > 1 && char.ToUpperInvariant(value[0]) == 'P')
        {
            try
            {
                return XmlConvert.ToTimeSpan(value.ToUpperInvariant());
            }
            catch (FormatException)
            {
                throw new DeferredInputException($"\"{text}\" is not a duration. Use forms like \"90m\", \"2h\" or \"1d 3h\".");
            }
        }

        if (double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var bare))
        {
            return TimeSpan.FromMinutes(bare);
        }

        var total = TimeSpan.Zero;
        var matched = 0;
        foreach (Match match in DurationPart().Matches(value))
        {
            var amount = double.Parse(match.Groups["n"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            total += char.ToLowerInvariant(match.Groups["u"].Value[0]) switch
            {
                'd' or 'д' => TimeSpan.FromDays(amount),
                'h' or 'ч' => TimeSpan.FromHours(amount),
                's' or 'с' => TimeSpan.FromSeconds(amount),
                'w' or 'н' => TimeSpan.FromDays(amount * 7),
                _ => TimeSpan.FromMinutes(amount)
            };
            matched += match.Length;
        }

        if (matched == 0 || matched < value.Count(c => !char.IsWhiteSpace(c)) / 2)
        {
            throw new DeferredInputException($"\"{text}\" is not a duration. Use forms like \"90m\", \"2h\" or \"1d 3h\".");
        }

        return total;
    }

    [GeneratedRegex(@"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>w|wk|weeks?|d|days?|h|hrs?|hours?|m|mins?|minutes?|s|secs?|seconds?|нед\w*|д\w*|ч\w*|мин\w*|м|сек\w*|с)", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPart();

    /// <summary>Момент из <c>at</c>: «18:30» — ближайшее такое время, дата с временем — по местным часам, с поясом — как есть.</summary>
    private static DateTime Moment(string text, DateTime local, DeferredFacts facts)
    {
        var value = text.Trim();
        if (TryTimeOfDay(value, out var time) && TimeOnly().IsMatch(value))
        {
            var today = local.Date + time;
            return ToUtc(today > local ? today : today.AddDays(1), facts.Zone);
        }

        if (HasOffset().IsMatch(value) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
        {
            return withOffset.UtcDateTime;
        }

        if (DateTime.TryParseExact(value, MomentFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            // Дата без времени — девять утра: «напомни 15-го» значит утром, а не в полночь.
            return ToUtc(parsed.TimeOfDay == TimeSpan.Zero && !value.Contains(':', StringComparison.Ordinal) ? parsed.Date.AddHours(9) : parsed, facts.Zone);
        }

        throw new DeferredInputException($"\"{text}\" is not a time. Use \"18:30\", \"2026-10-11 09:00\" or an ISO time with offset.");
    }

    private static bool TryTimeOfDay(string text, out TimeSpan time)
    {
        var value = text.Trim();
        if (value.Length > 5 && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var full))
        {
            time = full.TimeOfDay;
            return true;
        }

        return TimeSpan.TryParseExact(value, TimeFormats, CultureInfo.InvariantCulture, out time) &&
               time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
    }

    [GeneratedRegex(@"^\d{1,2}:\d{2}$")]
    private static partial Regex TimeOnly();

    [GeneratedRegex(@"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex HasOffset();

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var value = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(value))
        {
            value = value.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(value, zone);
    }

    /// <summary>Дни недели: «mon», «пн», «понедельник», «weekdays», «будни», «weekends», «выходные».</summary>
    private static List<DayOfWeek> Days(JsonElement args)
    {
        var words = new List<string>();
        if (args.TryGetProperty("days", out var value))
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                words.AddRange(value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString()));
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                words.AddRange((value.GetString() ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries));
            }
        }

        var days = new SortedSet<DayOfWeek>();
        foreach (var raw in words)
        {
            var word = raw.Trim().ToLowerInvariant();
            if (word is "weekdays" or "будни")
            {
                days.UnionWith([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);
                continue;
            }

            if (word is "weekends" or "weekend" or "выходные")
            {
                days.UnionWith([DayOfWeek.Saturday, DayOfWeek.Sunday]);
                continue;
            }

            days.Add(word switch
            {
                _ when word.StartsWith("mo", StringComparison.Ordinal) || word.StartsWith("пн", StringComparison.Ordinal) || word.StartsWith("пон", StringComparison.Ordinal) => DayOfWeek.Monday,
                _ when word.StartsWith("tu", StringComparison.Ordinal) || word.StartsWith("вт", StringComparison.Ordinal) => DayOfWeek.Tuesday,
                _ when word.StartsWith("we", StringComparison.Ordinal) || word.StartsWith("ср", StringComparison.Ordinal) => DayOfWeek.Wednesday,
                _ when word.StartsWith("th", StringComparison.Ordinal) || word.StartsWith("чт", StringComparison.Ordinal) || word.StartsWith("чет", StringComparison.Ordinal) => DayOfWeek.Thursday,
                _ when word.StartsWith("fr", StringComparison.Ordinal) || word.StartsWith("пт", StringComparison.Ordinal) || word.StartsWith("пят", StringComparison.Ordinal) => DayOfWeek.Friday,
                _ when word.StartsWith("sa", StringComparison.Ordinal) || word.StartsWith("сб", StringComparison.Ordinal) || word.StartsWith("суб", StringComparison.Ordinal) => DayOfWeek.Saturday,
                _ when word.StartsWith("su", StringComparison.Ordinal) || word.StartsWith("вс", StringComparison.Ordinal) || word.StartsWith("вос", StringComparison.Ordinal) => DayOfWeek.Sunday,
                _ => throw new DeferredInputException($"\"{raw}\" is not a day of the week. Use mon, tue, wed, thu, fri, sat, sun.")
            });
        }

        return [.. days];
    }

    // ───────────────────────── описание для модели ─────────────────────────

    /// <summary>Когда задача сработает — по-английски, для ответа инструмента.</summary>
    public static string Describe(DeferredTask task, DeferredFacts facts)
    {
        var text = new StringBuilder();
        var trigger = task.Trigger;
        var repeating = task.Repeat.Kind != DeferredRepeatKind.Once;
        switch (trigger.Kind)
        {
            case DeferredTriggerKind.At:
                text.Append(task.Repeat.Kind switch
                {
                    DeferredRepeatKind.Every => $"every {Span(TimeSpan.FromMinutes(task.Repeat.EveryMinutes))}, ",
                    DeferredRepeatKind.Daily => $"every day at {task.Repeat.Time}, ",
                    DeferredRepeatKind.Weekly => $"every {string.Join(", ", task.Repeat.Days.Select(day => day.ToString()[..3]))} at {task.Repeat.Time}, ",
                    _ => "once, "
                });
                if (task.NextDueUtc is { } due)
                {
                    text.Append(repeating ? "next at " : "at ")
                        .Append(Local(due, facts))
                        .Append(" (in ").Append(Span(due - facts.NowUtc)).Append(')');
                }

                break;
            case DeferredTriggerKind.Uptime:
                text.Append(repeating ? "every boot, " : "").Append("when Windows uptime reaches ")
                    .Append(Span(TimeSpan.FromHours(trigger.UptimeHours ?? 0)))
                    .Append(" (uptime now ").Append(Span(facts.Uptime)).Append(')');
                break;
            case DeferredTriggerKind.NextBoot:
                text.Append(repeating ? "every time the PC is turned on" : "the next time the PC is turned on (signs in to Windows)");
                break;
            case DeferredTriggerKind.NextAppStart:
                text.Append(repeating ? "every time the program starts" : "the next time the program starts");
                break;
            case DeferredTriggerKind.Condition:
                text.Append(repeating ? "each time the condition becomes true" : "when the condition first becomes true")
                    .Append(", checked every ").Append(Span(DeferredClock.CheckInterval(task)));
                break;
        }

        return text.ToString();
    }

    public static string Local(DateTime utc, DeferredFacts facts) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc, facts.Zone).ToString("yyyy-MM-dd HH:mm (ddd)", CultureInfo.InvariantCulture);

    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalMinutes < 1)
        {
            return $"{Math.Max(1, (int)span.TotalSeconds)} s";
        }

        var parts = new List<string>();
        if (span.Days > 0)
        {
            parts.Add($"{span.Days} d");
        }

        if (span.Hours > 0)
        {
            parts.Add($"{span.Hours} h");
        }

        if (span.Minutes > 0 && span.Days == 0)
        {
            parts.Add($"{span.Minutes} min");
        }

        return parts.Count == 0 ? "0 min" : string.Join(" ", parts);
    }
}
