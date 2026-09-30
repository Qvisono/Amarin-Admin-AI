using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Задачи по расписанию (C3): сроки, пропуски, «не чаще одного раза», только чтение и отчёт.
/// </summary>
public sealed class ScheduleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-schedule-" + Guid.NewGuid().ToString("N"));

    public ScheduleTests() => Directory.CreateDirectory(_root);

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

    private static readonly DateTime Monday = new(2026, 9, 28, 12, 0, 0);

    private static ScheduledJob Job(ScheduleKind kind, DateTime created, DateTime? lastRun = null) => new()
    {
        Id = "j1",
        Name = "Проверка",
        Prompt = "проверь диски",
        Kind = kind,
        Time = "09:00",
        Days = [DayOfWeek.Monday, DayOfWeek.Thursday],
        EveryHours = 6,
        Created = created,
        LastRun = lastRun
    };

    // ───────────────────────── сроки ─────────────────────────

    [Fact]
    public void A_new_daily_task_waits_for_its_first_time_instead_of_running_at_once()
    {
        var job = Job(ScheduleKind.Daily, created: Monday);

        Assert.False(ScheduleClock.IsDue(job, Monday.AddHours(1), Monday));
        Assert.True(ScheduleClock.IsDue(job, Monday.Date.AddDays(1).AddHours(9), Monday));
    }

    [Fact]
    public void Days_missed_while_the_pc_was_off_run_once_not_once_per_day()
    {
        var job = Job(ScheduleKind.Daily, created: Monday.AddDays(-10), lastRun: Monday.AddDays(-5));

        Assert.True(ScheduleClock.IsDue(job, Monday, Monday));

        job.LastRun = Monday;
        Assert.False(ScheduleClock.IsDue(job, Monday.AddMinutes(1), Monday));
        Assert.False(ScheduleClock.IsDue(job, Monday.AddHours(20), Monday));
        Assert.True(ScheduleClock.IsDue(job, Monday.Date.AddDays(1).AddHours(9), Monday));
    }

    [Fact]
    public void A_weekly_task_runs_only_on_its_days()
    {
        var job = Job(ScheduleKind.Weekly, created: Monday.AddDays(-1), lastRun: Monday.Date.AddHours(9));

        Assert.False(ScheduleClock.IsDue(job, Monday.Date.AddDays(2).AddHours(10), Monday));
        Assert.True(ScheduleClock.IsDue(job, Monday.Date.AddDays(3).AddHours(9), Monday));
        Assert.Equal(Monday.Date.AddDays(3).AddHours(9), ScheduleClock.Next(job, Monday));
    }

    [Fact]
    public void An_interval_task_counts_from_its_last_run()
    {
        var job = Job(ScheduleKind.EveryHours, created: Monday.AddDays(-1), lastRun: Monday);

        Assert.False(ScheduleClock.IsDue(job, Monday.AddHours(5.9), Monday));
        Assert.True(ScheduleClock.IsDue(job, Monday.AddHours(6), Monday));
    }

    [Fact]
    public void A_startup_task_runs_once_per_launch()
    {
        var job = Job(ScheduleKind.AtStartup, created: Monday.AddDays(-1));

        Assert.True(ScheduleClock.IsDue(job, Monday, appStartedAt: Monday));
        job.LastRun = Monday.AddSeconds(30);
        Assert.False(ScheduleClock.IsDue(job, Monday.AddHours(5), appStartedAt: Monday));
        Assert.True(ScheduleClock.IsDue(job, Monday.AddDays(1), appStartedAt: Monday.AddDays(1)));
    }

    [Fact]
    public void Disabled_or_empty_tasks_never_run()
    {
        var job = Job(ScheduleKind.EveryHours, created: Monday.AddDays(-2));
        job.Enabled = false;
        Assert.False(ScheduleClock.IsDue(job, Monday, Monday));

        job.Enabled = true;
        job.Prompt = "  ";
        Assert.False(ScheduleClock.IsDue(job, Monday, Monday));
    }

    [Theory]
    [InlineData("7:05", 7, 5)]
    [InlineData("25:00", 9, 0)]
    [InlineData("мусор", 9, 0)]
    public void A_broken_time_falls_back_to_nine(string time, int hours, int minutes) =>
        Assert.Equal(new TimeSpan(hours, minutes, 0), ScheduleClock.TimeOfDay(new ScheduledJob { Time = time }));

    // ───────────────────────── отчёт ─────────────────────────

    [Theory]
    [InlineData("Всё хорошо.\nSTATUS: OK", ScheduleStatus.Ok)]
    [InlineData("Диск почти полон.\n\n**STATUS: ATTENTION**", ScheduleStatus.Attention)]
    [InlineData("status: problem — SMART", ScheduleStatus.Problem)]
    [InlineData("Без отметки", ScheduleStatus.Unknown)]
    [InlineData("STATUS: OK\nпотом\nSTATUS: PROBLEM", ScheduleStatus.Problem)]
    public void The_last_status_line_is_the_verdict(string report, ScheduleStatus expected) =>
        Assert.Equal(expected, ScheduleReport.Parse(report));

    [Fact]
    public void The_status_line_is_not_shown_to_the_person() =>
        Assert.Equal("Диск почти полон.", ScheduleReport.Strip("Диск почти полон.\nSTATUS: ATTENTION"));

    [Fact]
    public void The_rule_for_the_agent_names_the_format_and_forbids_changes()
    {
        Assert.Contains("STATUS: OK", ScheduleReport.Rule, StringComparison.Ordinal);
        Assert.Contains("nothing on this PC may be changed", ScheduleReport.Rule, StringComparison.Ordinal);
    }

    // ───────────────────────── файлы ─────────────────────────

    [Fact]
    public void Tasks_and_runs_are_kept_in_the_profile()
    {
        var book = new ScheduleBook(_root);
        book.Save([Job(ScheduleKind.Daily, Monday)]);

        Assert.True(book.Update("j1", job => job.LastStatus = ScheduleStatus.Attention));
        book.Append(new ScheduleRunEntry(Monday, "j1", "Проверка", ScheduleStatus.Ok, 0.01m, "c1"));
        book.Append(new ScheduleRunEntry(Monday.AddDays(1), "j1", "Проверка", ScheduleStatus.Problem, 0.02m, "c2"));

        Assert.Equal(ScheduleStatus.Attention, Assert.Single(book.Load()).LastStatus);
        Assert.Equal(["c2", "c1"], book.Recent(5).Select(entry => entry.ChatId));
    }

    [Fact]
    public void A_broken_file_reads_as_no_tasks()
    {
        File.WriteAllText(Path.Combine(_root, ScheduleBook.FileName), "{ nope");

        Assert.Empty(new ScheduleBook(_root).Load());
    }

    [Fact]
    public void Schedule_files_travel_with_settings_and_go_with_the_profile()
    {
        Assert.Equal(DataUsage.SettingsKey, DataUsage.ClassifyAppFile(ScheduleBook.FileName));
        Assert.Contains(ScheduleBook.FileName, ProfileDataWiper.DefaultProfileFiles);
        Assert.Contains(ScheduleBook.LogName, ProfileDataWiper.DefaultProfileFiles);
    }

    // ───────────────────────── прогон ─────────────────────────

    [Fact]
    public async Task A_due_task_runs_once_and_its_verdict_is_written()
    {
        var book = new ScheduleBook(_root);
        book.Save([Job(ScheduleKind.EveryHours, created: Monday.AddDays(-1))]);
        var now = Monday;
        var runs = 0;
        using var runner = new ScheduleRunner(book, (job, _) =>
        {
            runs++;
            // Отметка о прогоне уже стоит: таймер посреди долгого прогона не запустит его снова.
            Assert.Equal(now, book.Load().Single().LastRun);
            return Task.FromResult(new ScheduleRunOutcome(ScheduleStatus.Attention, 0.03m, "chat-1"));
        }, () => now);

        await runner.TickAsync();
        await runner.TickAsync();

        Assert.Equal(1, runs);
        var stored = book.Load().Single();
        Assert.Equal(ScheduleStatus.Attention, stored.LastStatus);
        Assert.Equal("chat-1", stored.LastChatId);
        Assert.Equal(0.03m, Assert.Single(book.Recent(5)).CostUsd);
    }

    [Fact]
    public async Task A_crashing_run_is_recorded_as_failed_and_the_next_task_still_runs()
    {
        var book = new ScheduleBook(_root);
        var first = Job(ScheduleKind.EveryHours, created: Monday.AddDays(-1));
        var second = Job(ScheduleKind.EveryHours, created: Monday.AddDays(-1));
        second.Id = "j2";
        book.Save([first, second]);
        var ran = new List<string>();
        using var runner = new ScheduleRunner(book, (job, _) =>
        {
            ran.Add(job.Id);
            return job.Id == "j1"
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(new ScheduleRunOutcome(ScheduleStatus.Ok, 0m, null));
        }, () => Monday);

        await runner.TickAsync();

        Assert.Equal(["j1", "j2"], ran);
        Assert.Equal(ScheduleStatus.Failed, book.Load().Single(job => job.Id == "j1").LastStatus);
    }

    [Fact]
    public void Inside_a_scheduled_run_every_write_is_refused_and_outside_it_is_not()
    {
        var args = JsonSerializer.SerializeToElement(new { path = "C:\\Temp\\a.txt", content = "x" });
        var settings = new AppSettings { ApprovalMode = ApprovalMode.AlwaysApprove };

        GateCheck inside;
        using (ToolGate.ForceReadOnly())
        {
            inside = ToolGate.Check("write_file", args, settings);
        }

        var outside = ToolGate.Check("write_file", args, settings);

        Assert.NotNull(inside.Refusal);
        Assert.Contains("write_file", inside.Refusal, StringComparison.Ordinal);
        Assert.Null(outside.Refusal);
    }

    [Fact]
    public void Reads_still_run_inside_a_scheduled_run()
    {
        using var _ = ToolGate.ForceReadOnly();

        Assert.Null(ToolGate.Check("disk_space", JsonSerializer.SerializeToElement(new { action = "analyze" }), new AppSettings()).Refusal);
    }

    [Fact]
    public void The_run_leaves_a_chat_with_the_task_and_the_report()
    {
        var job = Job(ScheduleKind.Daily, Monday);
        var session = MainWindow.BuildScheduleChat(new ChatSession { Id = "s1" }, job, Monday, DateFormat.DayMonthShort,
            out var assistant, out var call);
        call.NestedAgent = new AgentRunRecord
        {
            ReportText = "Место на C: кончается.\nSTATUS: ATTENTION",
            Cost = new VeniceCost { Usd = 0.05m, HasData = true },
            ModelId = "m"
        };

        var outcome = MainWindow.CompleteScheduleChat(session, assistant, call, ToolResult.Ok("done"));

        Assert.Equal("j1", session.ScheduleJobId);
        Assert.Equal("проверь диски", session.Messages[0].Text);
        Assert.Equal("Место на C: кончается.", assistant.Text);
        Assert.Equal(ScheduleStatus.Attention, outcome.Status);
        Assert.Equal(0.05m, outcome.CostUsd);
        Assert.Equal(2, session.ApiMessages.Count);
        Assert.Equal(ToolCallStatus.Done, call.Status);
    }

    [Fact]
    public void A_failed_agent_run_is_a_failed_verdict()
    {
        var session = MainWindow.BuildScheduleChat(new ChatSession { Id = "s1" }, Job(ScheduleKind.Daily, Monday), Monday,
            DateFormat.DayMonthShort, out var assistant, out var call);

        var outcome = MainWindow.CompleteScheduleChat(session, assistant, call, ToolResult.Fail("нет ключа"));

        Assert.Equal(ScheduleStatus.Failed, outcome.Status);
        Assert.True(ScheduleReport.Notable(outcome.Status));
        Assert.Equal(AssistantStatus.Error, assistant.Status);
    }

    // ───────────────────────── страница без окна ─────────────────────────

    [Fact]
    public void A_draft_is_checked_before_it_is_saved()
    {
        Assert.NotNull(SchedulePanel.Validate("", "x", ScheduleKind.Daily, "09:00", "6", 1));
        Assert.NotNull(SchedulePanel.Validate("n", " ", ScheduleKind.Daily, "09:00", "6", 1));
        Assert.NotNull(SchedulePanel.Validate("n", "x", ScheduleKind.Daily, "9 утра", "6", 1));
        Assert.NotNull(SchedulePanel.Validate("n", "x", ScheduleKind.Weekly, "09:00", "6", 0));
        Assert.NotNull(SchedulePanel.Validate("n", "x", ScheduleKind.EveryHours, "", "0", 0));
        Assert.Null(SchedulePanel.Validate("n", "x", ScheduleKind.EveryHours, "", "12", 0));
        Assert.Null(SchedulePanel.Validate("n", "x", ScheduleKind.AtStartup, "", "", 0));
    }

    [Fact]
    public void The_card_says_when_the_task_runs()
    {
        Assert.Equal(Loc.Format("S.Schedule.When.Daily", "09:00"), SchedulePanel.Describe(Job(ScheduleKind.Daily, Monday)));
        Assert.Equal(
            Loc.Format("S.Schedule.When.Weekly", Loc.Get("S.Schedule.Day.Monday") + ", " + Loc.Get("S.Schedule.Day.Thursday"), "09:00"),
            SchedulePanel.Describe(Job(ScheduleKind.Weekly, Monday)));
        Assert.Equal(Loc.Format("S.Schedule.When.EveryHours", 6), SchedulePanel.Describe(Job(ScheduleKind.EveryHours, Monday)));
    }
}

/// <summary>Вкладка «Расписание» на живом WPF.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class SchedulePanelUiTests
{
    private readonly WpfFixture _wpf;

    public SchedulePanelUiTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void Saved_tasks_appear_as_cards_and_the_health_preset_opens_the_editor()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-schedule-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (cards, editor, name) = _wpf.Ui.Invoke(() =>
            {
                var services = UiServices.Build(root, "k", new HttpClientHandler());
                services.Schedule.Save([new ScheduledJob { Id = "a", Name = "Диски", Prompt = "p", Created = DateTime.Now }]);

                var panel = new SchedulePanel { Width = 500, Height = 460 };
                panel.Attach(services);
                panel.Load();
                var count = ((ItemsControl)panel.FindName("JobItems")).Items.Count;

                ((Button)panel.FindName("HealthPresetButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return (count,
                    ((FrameworkElement)panel.FindName("EditorPane")).Visibility,
                    ((TextBox)panel.FindName("NameBox")).Text);
            });

            Assert.Equal(1, cards);
            Assert.Equal(Visibility.Visible, editor);
            Assert.Equal(Loc.Get("S.Schedule.HealthName"), name);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
