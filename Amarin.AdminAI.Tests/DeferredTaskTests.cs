using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Сроки отложенных задач: разовые, повторяющиеся, по времени работы Windows, при включении ПК и
/// запуске программы, по условию. Часы подставляются, а не ждутся.
/// </summary>
public sealed class DeferredClockTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    internal static DeferredFacts Facts(DateTime? now = null, TimeSpan? uptime = null, DateTime? session = null, DateTime? app = null, TimeZoneInfo? zone = null)
    {
        var at = now ?? Now;
        var up = uptime ?? TimeSpan.FromHours(5);
        return new DeferredFacts(at, up, at - up, session ?? at - up, app ?? at.AddMinutes(-10), zone ?? TimeZoneInfo.Utc);
    }

    private static DeferredTask At(DateTime due, DeferredRepeat? repeat = null) => new()
    {
        Id = "t",
        Kind = DeferredKind.Reminder,
        Status = DeferredStatus.Pending,
        CreatedUtc = Now.AddHours(-1),
        Trigger = new DeferredTrigger { Kind = DeferredTriggerKind.At },
        Repeat = repeat ?? new DeferredRepeat(),
        NextDueUtc = due
    };

    [Fact]
    public void A_one_time_task_is_due_from_its_moment_on_and_then_done()
    {
        var task = At(Now.AddMinutes(5));

        Assert.False(DeferredClock.IsDue(task, Facts()));
        Assert.True(DeferredClock.IsDue(task, Facts(Now.AddMinutes(5))));
        Assert.False(DeferredClock.Rearm(task, Facts(Now.AddMinutes(5))));
        Assert.Null(task.NextDueUtc);
    }

    [Fact]
    public void A_missed_period_fires_once_and_the_next_one_stays_on_the_grid()
    {
        // Каждый час в :00; компьютер спал с 10:00 до 13:30 — одно срабатывание, следующее в 14:00.
        var task = At(new DateTime(2026, 10, 10, 10, 0, 0, DateTimeKind.Utc), new DeferredRepeat { Kind = DeferredRepeatKind.Every, EveryMinutes = 60 });
        var late = Facts(new DateTime(2026, 10, 10, 13, 30, 0, DateTimeKind.Utc));

        Assert.True(DeferredClock.IsDue(task, late));
        Assert.True(DeferredClock.Rearm(task, late));
        Assert.Equal(new DateTime(2026, 10, 10, 14, 0, 0, DateTimeKind.Utc), task.NextDueUtc);
        Assert.False(DeferredClock.IsDue(task, late));
    }

    [Fact]
    public void Every_day_at_nine_stays_nine_in_the_morning_across_daylight_saving()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var repeat = new DeferredRepeat { Kind = DeferredRepeatKind.Daily, Time = "09:00" };

        // 28 марта 2026 — ещё зимнее время (UTC+1); 29 марта — уже летнее (UTC+2).
        var saturday = Facts(new DateTime(2026, 3, 28, 10, 0, 0, DateTimeKind.Utc), zone: berlin);
        Assert.Equal(new DateTime(2026, 3, 29, 7, 0, 0, DateTimeKind.Utc), DeferredClock.NextLocal(repeat, saturday));
    }

    [Fact]
    public void A_weekly_task_comes_on_its_days()
    {
        // 10 октября 2026 — суббота.
        var repeat = new DeferredRepeat { Kind = DeferredRepeatKind.Weekly, Time = "08:30", Days = [DayOfWeek.Monday, DayOfWeek.Thursday] };

        Assert.Equal(new DateTime(2026, 10, 12, 8, 30, 0, DateTimeKind.Utc), DeferredClock.NextLocal(repeat, Facts()));
    }

    [Fact]
    public void An_uptime_task_fires_once_per_boot()
    {
        var task = new DeferredTask
        {
            Id = "u",
            Status = DeferredStatus.Pending,
            CreatedUtc = Now.AddDays(-3),
            Trigger = new DeferredTrigger { Kind = DeferredTriggerKind.Uptime, UptimeHours = 48 },
            Repeat = new DeferredRepeat { Kind = DeferredRepeatKind.EveryBoot }
        };

        Assert.False(DeferredClock.IsDue(task, Facts(uptime: TimeSpan.FromHours(47))));
        var reached = Facts(uptime: TimeSpan.FromHours(48.5));
        Assert.True(DeferredClock.IsDue(task, reached));
        Assert.True(DeferredClock.Rearm(task, reached));
        Assert.False(DeferredClock.IsDue(task, Facts(Now.AddHours(1), uptime: TimeSpan.FromHours(49.5))));

        // Перезагрузили — новый сеанс, и через двое суток снова пора.
        Assert.True(DeferredClock.IsDue(task, Facts(Now.AddDays(3), uptime: TimeSpan.FromHours(50))));
    }

    [Fact]
    public void The_next_power_on_is_the_next_sign_in_not_the_next_boot()
    {
        // Быстрый запуск: загрузка та же, а вход в Windows — новый.
        var task = new DeferredTask
        {
            Id = "b",
            Status = DeferredStatus.Pending,
            CreatedUtc = Now.AddHours(-2),
            Trigger = new DeferredTrigger { Kind = DeferredTriggerKind.NextBoot }
        };

        Assert.False(DeferredClock.IsDue(task, Facts(uptime: TimeSpan.FromDays(3), session: Now.AddHours(-3))));
        Assert.True(DeferredClock.IsDue(task, Facts(uptime: TimeSpan.FromDays(3), session: Now.AddMinutes(-1))));
    }

    [Fact]
    public void The_next_start_of_the_program_counts_from_when_the_task_was_set()
    {
        var task = new DeferredTask
        {
            Id = "s",
            Status = DeferredStatus.Pending,
            CreatedUtc = Now.AddMinutes(-5),
            Trigger = new DeferredTrigger { Kind = DeferredTriggerKind.NextAppStart }
        };

        Assert.False(DeferredClock.IsDue(task, Facts(app: Now.AddMinutes(-10))));
        Assert.True(DeferredClock.IsDue(task, Facts(app: Now.AddMinutes(-1))));
    }

    [Fact]
    public void A_snoozed_task_waits_for_the_snooze_and_then_lateness_counts_from_it()
    {
        var task = At(Now.AddHours(-3));
        task.SnoozedUntilUtc = Now.AddMinutes(10);

        Assert.False(DeferredClock.IsDue(task, Facts()));
        Assert.True(DeferredClock.IsDue(task, Facts(Now.AddMinutes(10))));
        Assert.Null(DeferredClock.Lateness(task, Facts(Now.AddMinutes(10))));
    }

    [Fact]
    public void Lateness_is_named_only_when_it_matters()
    {
        var task = At(Now.AddHours(-2));

        Assert.Equal(TimeSpan.FromHours(2), DeferredClock.Lateness(task, Facts()));
        Assert.Null(DeferredClock.Lateness(At(Now.AddSeconds(-30)), Facts()));
    }

    [Fact]
    public void The_wake_time_is_the_nearest_time_based_due()
    {
        var soon = At(Now.AddMinutes(30));
        var later = At(Now.AddHours(5));
        var uptime = new DeferredTask
        {
            Status = DeferredStatus.Pending,
            Trigger = new DeferredTrigger { Kind = DeferredTriggerKind.Uptime, UptimeHours = 5.25 }
        };

        Assert.Equal(Now.AddMinutes(15), DeferredClock.WakeAtUtc([later, soon, uptime], Facts()));
    }
}

/// <summary>Сроки так, как их передаёт модель: «через 90 минут», «в 18:30», «каждую пятницу».</summary>
public sealed class DeferredTimeParserTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 19, 0, 0, DateTimeKind.Utc);

    private static DeferredSchedule Parse(object args, DeferredKind kind = DeferredKind.Reminder) =>
        DeferredTimeParser.Resolve(JsonSerializer.SerializeToElement(args), kind, DeferredClockTests.Facts(Now));

    [Theory]
    [InlineData("90m", 90)]
    [InlineData("2h", 120)]
    [InlineData("1d 3h", 27 * 60)]
    [InlineData("1ч 30мин", 90)]
    [InlineData("45", 45)]
    [InlineData("PT2H30M", 150)]
    public void A_delay_is_counted_from_now(string text, int minutes)
    {
        Assert.Equal(Now.AddMinutes(minutes), Parse(new { @in = text }).FirstDueUtc);
    }

    [Fact]
    public void A_clock_time_already_passed_today_means_tomorrow()
    {
        Assert.Equal(new DateTime(2026, 10, 11, 18, 30, 0, DateTimeKind.Utc), Parse(new { at = "18:30" }).FirstDueUtc);
        Assert.Equal(new DateTime(2026, 10, 10, 21, 15, 0, DateTimeKind.Utc), Parse(new { at = "21:15" }).FirstDueUtc);
    }

    [Fact]
    public void A_date_with_time_is_local_and_a_bare_date_is_nine_in_the_morning()
    {
        Assert.Equal(new DateTime(2026, 10, 20, 9, 45, 0, DateTimeKind.Utc), Parse(new { at = "2026-10-20 09:45" }).FirstDueUtc);
        Assert.Equal(new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc), Parse(new { at = "2026-10-20" }).FirstDueUtc);
    }

    [Fact]
    public void A_moment_in_the_past_or_too_far_ahead_is_refused_with_a_reason()
    {
        Assert.Contains("passed", Assert.Throws<DeferredInputException>(() => Parse(new { at = "2026-10-01 09:00" })).Message, StringComparison.Ordinal);
        Assert.Contains("year", Assert.Throws<DeferredInputException>(() => Parse(new { at = "2028-01-01 09:00" })).Message, StringComparison.Ordinal);
        Assert.Contains("Say when", Assert.Throws<DeferredInputException>(() => Parse(new { })).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Daily_and_weekly_take_their_time_of_day_from_at()
    {
        var daily = Parse(new { repeat = "daily", at = "09:00" });
        Assert.Equal(DeferredRepeatKind.Daily, daily.Repeat.Kind);
        Assert.Equal("09:00", daily.Repeat.Time);
        Assert.Equal(new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc), daily.FirstDueUtc);

        var weekly = Parse(new { repeat = "weekly", at = "10:00", days = new[] { "пн", "fri" } });
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], weekly.Repeat.Days);
    }

    [Fact]
    public void An_agent_cannot_be_scheduled_more_often_than_every_fifteen_minutes()
    {
        Assert.Throws<DeferredInputException>(() => Parse(new { repeat = "every", every_minutes = 5 }, DeferredKind.Agent));
        Assert.Equal(60, Parse(new { repeat = "every", every_minutes = 60 }, DeferredKind.Agent).Repeat.EveryMinutes);
        Assert.Equal(1, Parse(new { repeat = "every", every_minutes = 1 }).Repeat.EveryMinutes);
    }

    [Fact]
    public void Uptime_power_on_program_start_and_condition_are_their_own_triggers()
    {
        Assert.Equal(DeferredTriggerKind.Uptime, Parse(new { uptime_hours = 48 }).Trigger.Kind);
        Assert.Equal(DeferredTriggerKind.NextBoot, Parse(new { @event = "next_boot" }).Trigger.Kind);
        var everyBoot = Parse(new { repeat = "every_boot" });
        Assert.Equal((DeferredTriggerKind.NextBoot, DeferredRepeatKind.EveryBoot), (everyBoot.Trigger.Kind, everyBoot.Repeat.Kind));
        Assert.Equal(DeferredTriggerKind.NextAppStart, Parse(new { @event = "next_start" }).Trigger.Kind);
        Assert.Equal(DeferredTriggerKind.Condition, Parse(new { condition = "Test-Path C:\\x" }).Trigger.Kind);
    }

    [Fact]
    public void Two_ways_of_saying_when_are_refused()
    {
        Assert.Throws<DeferredInputException>(() => Parse(new { condition = "$true", @in = "2h" }));
        Assert.Throws<DeferredInputException>(() => Parse(new { @in = "2h", at = "18:00" }));
    }
}

/// <summary>Хранение задач: файл с запасной копией и печать того, что выполнится без человека.</summary>
public sealed class DeferredBookTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-deferred-" + Guid.NewGuid().ToString("N"));

    public DeferredBookTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Tasks_survive_a_restart_and_a_damaged_file_comes_back_from_its_copy()
    {
        var book = new DeferredBook(_root);
        var added = book.Add(new DeferredTask { Title = "Перезагрузить", Kind = DeferredKind.Reminder, Status = DeferredStatus.Pending });
        book.Update(added.Id, task => task.Text = "второй раз");

        File.WriteAllText(Path.Combine(_root, DeferredBook.FileName), "{ не json");
        var reopened = new DeferredBook(_root).Snapshot();

        Assert.Equal("второй раз", Assert.Single(reopened).Text);
    }

    [Fact]
    public void The_seal_holds_for_the_approved_command_only()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var book = new DeferredBook(_root);
        var task = new DeferredTask { Id = "c1", Kind = DeferredKind.Command, Command = "Clear-RecycleBin -Force", CreatedUtc = DateTime.UtcNow };
        task.Seal = book.Seal(task);

        Assert.True(book.Verify(task));
        task.Command = "Remove-Item C:\\Users -Recurse";
        Assert.False(book.Verify(task));

        // Другой профиль — другой ключ: задача, перенесённая туда руками, не выполнится.
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        task.Command = "Clear-RecycleBin -Force";
        Assert.False(new DeferredBook(other).Verify(task));
    }

    [Fact]
    public void Finished_tasks_are_trimmed_but_active_ones_never()
    {
        var book = new DeferredBook(_root);
        for (var i = 0; i < DeferredBook.HistoryKeep + 20; i++)
        {
            book.Add(new DeferredTask { Status = DeferredStatus.Done, CreatedUtc = DateTime.UtcNow.AddMinutes(-i) });
        }

        var pending = book.Add(new DeferredTask { Status = DeferredStatus.Pending, CreatedUtc = DateTime.UtcNow.AddYears(-1) });

        var tasks = book.Snapshot();
        Assert.Equal(DeferredBook.HistoryKeep + 1, tasks.Count);
        Assert.Contains(tasks, task => task.Id == pending.Id);
    }
}

/// <summary>
/// Проверка и выполнение: напоминание ждёт «Готово», прерванное не повторяется молча, занятый чат
/// не тратит попытку, неудачи подряд останавливают повтор, условие срабатывает на переходе к «да».
/// </summary>
public sealed class DeferredRunnerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-runner-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHost _host = new();
    private DateTime _now = Now;

    public DeferredRunnerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private DeferredRunner Runner(DeferredBook book, Func<string, CancellationToken, Task<bool?>>? probe = null) =>
        new(book, _host, new NoWake(), probe ?? ((_, _) => Task.FromResult<bool?>(null)), () => DeferredClockTests.Facts(_now));

    private static DeferredTask Due(DeferredKind kind, DeferredRepeat? repeat = null) => new()
    {
        Kind = kind,
        Title = kind.ToString(),
        Status = DeferredStatus.Pending,
        CreatedUtc = Now.AddHours(-1),
        Trigger = new DeferredTrigger { Kind = DeferredTriggerKind.At },
        Repeat = repeat ?? new DeferredRepeat(),
        NextDueUtc = Now.AddMinutes(-1)
    };

    [Fact]
    public async Task A_reminder_shows_and_waits_for_done_even_across_a_restart()
    {
        var book = new DeferredBook(_root);
        var task = book.Add(Due(DeferredKind.Reminder));
        using (var runner = Runner(book))
        {
            await runner.TickAsync();
        }

        Assert.Single(_host.Reminders);
        var stored = book.Peek(task.Id)!;
        Assert.Equal(DeferredStatus.Done, stored.Status);
        Assert.NotNull(stored.AwaitingAckSinceUtc);

        // Программу закрыли, не нажав «Готово»: при запуске напоминание встаёт снова.
        using (var again = Runner(new DeferredBook(_root)))
        {
            await again.TickAsync();
            again.Acknowledge(task.Id);
        }

        Assert.Equal(2, _host.Reminders.Count);
        Assert.Null(new DeferredBook(_root).Peek(task.Id)!.AwaitingAckSinceUtc);
    }

    [Fact]
    public async Task A_run_cut_short_is_offered_again_rather_than_repeated_silently()
    {
        var book = new DeferredBook(_root);
        var task = Due(DeferredKind.Command);
        task.Status = DeferredStatus.Running;
        var stored = book.Add(task);

        using var runner = Runner(book);
        await runner.TickAsync();

        Assert.Equal(DeferredStatus.Interrupted, book.Peek(stored.Id)!.Status);
        Assert.Single(_host.Interrupted);
        Assert.Empty(_host.Runs);
    }

    [Fact]
    public async Task The_running_mark_is_on_disk_before_the_run_starts()
    {
        var book = new DeferredBook(_root);
        var task = book.Add(Due(DeferredKind.Agent));
        _host.OnRun = _ => _host.StatusSeen = new DeferredBook(_root).Peek(task.Id)!.Status;

        using var runner = Runner(book);
        await runner.TickAsync();
        await _host.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DeferredStatus.Running, _host.StatusSeen);
        Assert.Equal(DeferredStatus.Done, book.Peek(task.Id)!.Status);
    }

    [Fact]
    public async Task A_busy_chat_puts_the_task_back_without_spending_an_attempt()
    {
        var book = new DeferredBook(_root);
        var task = book.Add(Due(DeferredKind.Agent));
        _host.Answer = null;

        using var runner = Runner(book);
        await runner.TickAsync();
        await WaitFor(() => book.Peek(task.Id)!.Status == DeferredStatus.Pending);

        Assert.Equal(0, book.Peek(task.Id)!.ConsecutiveFailures);
        Assert.Single(_host.Runs);

        // Следующая проверка сразу не дёргает занятый чат снова — передышка.
        await runner.TickAsync();
        Assert.Single(_host.Runs);
    }

    [Fact]
    public async Task Three_failures_in_a_row_stop_a_repeating_task()
    {
        var book = new DeferredBook(_root);
        var task = book.Add(Due(DeferredKind.Command, new DeferredRepeat { Kind = DeferredRepeatKind.Every, EveryMinutes = 5 }));
        _host.Answer = new DeferredOutcome(false, "boom", 0m, null);

        using var runner = Runner(book);
        for (var i = 0; i < 3; i++)
        {
            _host.Finished = new TaskCompletionSource();
            await runner.TickAsync();
            await _host.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitFor(() => book.Peek(task.Id)!.Status != DeferredStatus.Running);
            _now = _now.AddMinutes(6);
        }

        Assert.Equal(DeferredStatus.Paused, book.Peek(task.Id)!.Status);
    }

    [Fact]
    public async Task A_condition_fires_when_it_turns_true_not_while_it_stays_true()
    {
        var book = new DeferredBook(_root);
        var task = book.Add(new DeferredTask
        {
            Kind = DeferredKind.Reminder,
            Status = DeferredStatus.Pending,
            CreatedUtc = Now.AddHours(-1),
            Trigger = new DeferredTrigger { Kind = DeferredTriggerKind.Condition, Condition = "x", CheckEveryMinutes = 1 },
            Repeat = new DeferredRepeat { Kind = DeferredRepeatKind.EveryBoot }
        });
        var answers = new Queue<bool?>([false, true, true, false, true]);

        using var runner = Runner(book, (_, _) => Task.FromResult(answers.Dequeue()));
        for (var i = 0; i < 5; i++)
        {
            await runner.TickAsync();
            _now = _now.AddMinutes(2);
        }

        Assert.Equal(2, _host.Reminders.Count);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }

    private sealed class NoWake : IDeferredWake
    {
        public void Reconcile(bool pending, DateTime? dueUtc)
        {
        }
    }

    private sealed class FakeHost : IDeferredHost
    {
        public List<DeferredTask> Reminders { get; } = [];

        public List<DeferredTask> Interrupted { get; } = [];

        public List<DeferredTask> Runs { get; } = [];

        public DeferredOutcome? Answer { get; set; } = new(true, null, 0.01m, "chat");

        public Action<DeferredTask>? OnRun { get; set; }

        public DeferredStatus? StatusSeen { get; set; }

        public TaskCompletionSource Finished { get; set; } = new();

        public void ShowReminder(DeferredTask task, TimeSpan? late) => Reminders.Add(task);

        public void ShowInterrupted(DeferredTask task) => Interrupted.Add(task);

        public Task<DeferredOutcome?> RunAsync(DeferredTask task, TimeSpan? late, CancellationToken cancellationToken)
        {
            lock (Runs)
            {
                Runs.Add(task);
            }

            OnRun?.Invoke(task);
            return Task.FromResult(Answer);
        }

        public void Completed(DeferredTask task, DeferredOutcome outcome) => Finished.TrySetResult();
    }
}

/// <summary>Инструмент чата: ставит задачу в своём чате, печатает одобренное и честно отказывает.</summary>
public sealed class DeferredTaskToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-deferred-tool-" + Guid.NewGuid().ToString("N"));

    public DeferredTaskToolTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static IDisposable Chat(string id) => AgentRunScope.Push(new AgentRunContext
    {
        Call = new ToolCallRecord { Id = "call", Name = DeferredTaskTool.ToolName },
        Assistant = new ChatDisplayMessage { Role = "assistant" },
        Observer = SilentTurnObserver.Instance,
        SessionId = id
    });

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task Outside_a_chat_nothing_is_scheduled()
    {
        var book = new DeferredBook(_root);

        var result = await new DeferredTaskTool(book).ExecuteAsync(Args(new { action = "remind", text = "x", @in = "5m" }));

        Assert.False(result.Success);
        Assert.Empty(book.Snapshot());
    }

    [Fact]
    public async Task A_reminder_is_set_in_its_chat_and_the_answer_names_the_resolved_time()
    {
        var book = new DeferredBook(_root);
        ToolResult result;
        using (Chat("chat-7"))
        {
            result = await new DeferredTaskTool(book).ExecuteAsync(Args(new { action = "remind", title = "Перезагрузка", text = "Перезагрузи ПК", uptime_hours = 48 }));
        }

        Assert.True(result.Success, result.Output);
        Assert.Contains("Now:", result.Output, StringComparison.Ordinal);
        Assert.Contains("uptime", result.Output, StringComparison.Ordinal);
        var task = Assert.Single(book.Snapshot());
        Assert.Equal(("chat-7", DeferredTriggerKind.Uptime), (task.ChatId, task.Trigger.Kind));
        Assert.Equal(task.Id, result.Deferred?.Id);
    }

    [Fact]
    public async Task An_approved_command_is_sealed_and_a_writing_condition_is_refused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var book = new DeferredBook(_root);
        using (Chat("chat-1"))
        {
            var command = await new DeferredTaskTool(book).ExecuteAsync(Args(new { action = "run_command", title = "Корзина", command = "Clear-RecycleBin -Force", repeat = "daily", at = "03:00" }));
            Assert.True(command.Success, command.Output);

            var condition = await new DeferredTaskTool(book).ExecuteAsync(Args(new { action = "remind", text = "x", condition = "Remove-Item C:\\temp -Recurse" }));
            Assert.False(condition.Success);
        }

        var task = Assert.Single(book.Snapshot());
        Assert.True(book.Verify(task));
    }

    [Fact]
    public async Task Tasks_are_listed_and_cancelled_by_id()
    {
        var book = new DeferredBook(_root);
        var tool = new DeferredTaskTool(book);
        using (Chat("chat-1"))
        {
            await tool.ExecuteAsync(Args(new { action = "remind", title = "Чай", text = "Чай готов", @in = "5m" }));
        }

        var id = Assert.Single(book.Snapshot()).Id;
        Assert.Contains(id, (await tool.ExecuteAsync(Args(new { action = "list" }))).Output, StringComparison.Ordinal);
        Assert.True((await tool.ExecuteAsync(Args(new { action = "cancel", id }))).Success);
        Assert.Equal(DeferredStatus.Cancelled, book.Peek(id)!.Status);
    }
}

/// <summary>Шлюз и отложенные задачи: что спрашивается, что нельзя разрешить впрок.</summary>
public sealed class DeferredGateTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void A_reminder_or_an_agent_task_is_set_without_a_question()
    {
        Assert.Null(ToolGate.Check(DeferredTaskTool.ToolName, Args(new { action = "remind", text = "x", @in = "5m" }), new AppSettings()).Question);
        Assert.Null(ToolGate.Check(DeferredTaskTool.ToolName, Args(new { action = "run_agent", text = "x", @in = "5m" }), new AppSettings()).Question);
    }

    [Fact]
    public void A_command_for_later_is_asked_now_and_cannot_be_allowed_ahead()
    {
        var check = ToolGate.Check(DeferredTaskTool.ToolName,
            Args(new { action = "run_command", title = "Очистка", command = "Remove-Item $env:TEMP\\* -Recurse", repeat = "daily", at = "03:00" }),
            new AppSettings());

        var question = Assert.IsType<DangerousActionInfo>(check.Question);
        Assert.True(question.Standing);
        Assert.Contains("03:00", question.ChangeSummary, StringComparison.Ordinal);
        Assert.False(new ConfirmationRequest { AgentLabel = "chat", Info = question, Completion = new TaskCompletionSource<ConfirmationAnswer>(), SessionId = "chat" }.CanAllowAhead);
    }

    [Fact]
    public async Task Always_approve_mode_does_not_answer_for_a_command_that_will_run_alone()
    {
        var queue = new ConfirmationQueue(() => new AppSettings { ApprovalMode = ApprovalMode.AlwaysApprove });
        var info = new DangerousActionInfo(DeferredTaskTool.ToolName, "x", "", DangerousRiskLevel.High);

        var answer = queue.ConfirmDetailedAsync("chat", info, "s1", CancellationToken.None);
        await Task.Delay(50);

        Assert.False(answer.IsCompleted);
        Assert.Equal(1, queue.PendingCount);
        queue.CancelForSession("s1");
    }

    [Fact]
    public void Read_only_mode_refuses_a_command_for_later_but_not_a_reminder()
    {
        var readOnly = new AppSettings { ApprovalMode = ApprovalMode.ReadOnly };

        Assert.NotNull(ToolGate.Check(DeferredTaskTool.ToolName, Args(new { action = "run_command", command = "Restart-Computer", @in = "1h" }), readOnly).Refusal);
        Assert.Null(ToolGate.Check(DeferredTaskTool.ToolName, Args(new { action = "remind", text = "x", @in = "1h" }), readOnly).Refusal);
    }
}
