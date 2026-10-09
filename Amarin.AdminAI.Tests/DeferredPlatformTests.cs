using System.Buffers.Binary;
using System.Xml.Linq;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Задача Планировщика, которая будит закрытую программу к сроку: каждое значение XML выбрано
/// против умолчания Планировщика, которое здесь вредит.
/// </summary>
public sealed class DeferredWakeTaskTests
{
    private static readonly XNamespace Task = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const string Sid = "S-1-5-21-1-2-3-1001";

    private static XElement Parse(string exe = @"C:\Apps\Amarin Admin AI.exe", DateTime? due = null) =>
        XDocument.Parse(DeferredWakeTask.Build(exe, Sid, due)).Root!;

    private static string Value(XElement root, params string[] path) =>
        path.Aggregate(root, (node, name) => node.Element(Task + name)!).Value;

    [Fact]
    public void The_program_is_never_killed_slowed_or_skipped_on_battery()
    {
        var root = Parse();

        // По умолчанию 72 часа: через трое суток Планировщик убил бы работающую программу.
        Assert.Equal("PT0S", Value(root, "Settings", "ExecutionTimeLimit"));
        // По умолчанию 7, ниже обычного: окно программы тормозило бы.
        Assert.Equal("4", Value(root, "Settings", "Priority"));
        Assert.Equal("false", Value(root, "Settings", "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value(root, "Settings", "StopIfGoingOnBatteries"));
        Assert.Equal("true", Value(root, "Settings", "StartWhenAvailable"));
        Assert.Equal("false", Value(root, "Settings", "WakeToRun"));
        Assert.Equal("IgnoreNew", Value(root, "Settings", "MultipleInstancesPolicy"));
    }

    [Fact]
    public void It_runs_as_the_signed_in_person_with_their_own_rights()
    {
        var root = Parse();

        Assert.Equal(Sid, Value(root, "Principals", "Principal", "UserId"));
        Assert.Equal("InteractiveToken", Value(root, "Principals", "Principal", "LogonType"));
        Assert.Equal("LeastPrivilege", Value(root, "Principals", "Principal", "RunLevel"));
        Assert.Equal(Sid, Value(root, "Triggers", "LogonTrigger", "UserId"));
    }

    [Fact]
    public void The_action_starts_this_exe_quietly_even_with_odd_characters_in_its_path()
    {
        var root = Parse(@"C:\Tools & Co\Amarin <beta>.exe");

        Assert.Equal(@"C:\Tools & Co\Amarin <beta>.exe", Value(root, "Actions", "Exec", "Command"));
        Assert.Equal("--wake", Value(root, "Actions", "Exec", "Arguments"));
    }

    [Fact]
    public void A_due_time_is_a_utc_moment_and_without_one_only_sign_in_starts_the_program()
    {
        var due = new DateTime(2026, 10, 11, 6, 30, 0, DateTimeKind.Utc);

        Assert.Equal("2026-10-11T06:30:00Z", Value(Parse(due: due), "Triggers", "TimeTrigger", "StartBoundary"));
        var withoutDue = Parse().Element(Task + "Triggers")!;
        Assert.Null(withoutDue.Element(Task + "TimeTrigger"));
        Assert.NotNull(withoutDue.Element(Task + "LogonTrigger"));
    }

    [Fact]
    public void The_task_name_belongs_to_one_windows_user()
    {
        Assert.Equal(@"\Amarin Admin AI deferred (" + Sid + ")", DeferredWakeTask.NameFor(Sid));
        Assert.Equal(DeferredWakeTask.NameFor(Sid), Value(Parse(), "RegistrationInfo", "URI"));
    }
}

/// <summary>
/// Планировщик зовётся, только когда нужное отличается от поставленного: проверка сроков идёт раз в
/// минуту, а <c>schtasks</c> — это процесс.
/// </summary>
public sealed class SchtasksDeferredWakeTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-wake-" + Guid.NewGuid().ToString("N"));
    private readonly List<string[]> _calls = [];
    private readonly List<string> _xml = [];
    private int _exitCode;

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

    private string Marker => Path.Combine(_root, "deferred-wake");

    private SchtasksDeferredWake Wake() => new(@"C:\Apps\amarin.exe", "S-1-5-21-9", Marker, Schtasks, () => Now);

    private (int, string) Schtasks(IReadOnlyList<string> arguments)
    {
        _calls.Add([.. arguments]);
        var xml = arguments.ToList().IndexOf("/XML");
        if (xml >= 0)
        {
            // Файл живёт, пока идёт вызов: читаем его тут же.
            _xml.Add(File.ReadAllText(arguments[xml + 1]));
        }

        return (_exitCode, _exitCode == 0 ? "" : "ERROR: Access is denied.");
    }

    [Fact]
    public void The_same_due_time_is_not_set_twice_and_a_new_one_replaces_it()
    {
        var wake = Wake();

        wake.Reconcile(true, Now.AddHours(3));
        wake.Reconcile(true, Now.AddHours(3));
        wake.Reconcile(true, Now.AddHours(5));

        Assert.Equal(2, _calls.Count);
        Assert.All(_calls, call => Assert.Equal("/Create", call[0]));
        Assert.Contains("2026-10-10T17:00:00Z", _xml[1]);
    }

    [Fact]
    public void Nothing_left_to_wait_for_removes_the_task_once()
    {
        var wake = Wake();
        wake.Reconcile(true, Now.AddHours(1));

        wake.Reconcile(false, null);
        wake.Reconcile(false, null);

        Assert.Equal(["/Create", "/Delete"], _calls.Select(call => call[0]));
    }

    [Fact]
    public void A_program_that_never_set_a_task_never_calls_the_scheduler()
    {
        Wake().Reconcile(false, null);

        Assert.Empty(_calls);
    }

    [Fact]
    public void What_was_set_is_remembered_across_restarts()
    {
        Wake().Reconcile(true, Now.AddHours(2));

        Wake().Reconcile(true, Now.AddHours(2));

        Assert.Single(_calls);
    }

    [Fact]
    public void A_due_time_already_passed_is_moved_a_minute_ahead_and_not_reset_on_every_check()
    {
        var wake = Wake();

        wake.Reconcile(true, Now.AddMinutes(-5));
        wake.Reconcile(true, Now.AddMinutes(-5));

        Assert.Single(_calls);
        Assert.Contains("2026-10-10T12:01:00Z", _xml[0]);
    }

    [Fact]
    public void A_refusal_is_reported_and_retried_until_it_passes()
    {
        var wake = Wake();
        var changes = 0;
        wake.ProblemChanged += () => changes++;

        _exitCode = 1;
        wake.Reconcile(true, Now.AddHours(1));
        Assert.Equal("ERROR: Access is denied.", wake.Problem);

        _exitCode = 0;
        wake.Reconcile(true, Now.AddHours(1));

        Assert.Null(wake.Problem);
        Assert.Equal(2, changes);
        Assert.Equal(2, _calls.Count);
    }
}

/// <summary>Мелодии: тихо, без щелчков в начале и в конце, настоящий WAV.</summary>
public sealed class ChimeSynthTests
{
    private const int Header = 44;

    private static short[] Samples(byte[] wav)
    {
        var samples = new short[(wav.Length - Header) / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(Header + i * 2));
        }

        return samples;
    }

    [Theory]
    [InlineData(ChimeTune.Reminder)]
    [InlineData(ChimeTune.Done)]
    [InlineData(ChimeTune.Failed)]
    public void Every_tune_is_a_mono_16_bit_wav_at_44_1_khz(ChimeTune tune)
    {
        var wav = ChimeSynth.Render(tune);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(44_100, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
    }

    [Theory]
    [InlineData(ChimeTune.Reminder)]
    [InlineData(ChimeTune.Done)]
    [InlineData(ChimeTune.Failed)]
    public void It_is_quiet_and_has_no_click_at_either_end(ChimeTune tune)
    {
        var samples = Samples(ChimeSynth.Render(tune));
        var peak = samples.Max(sample => Math.Abs((int)sample)) / (double)short.MaxValue;

        // Около −14 дБ: слышно, но не громче системного звука.
        Assert.InRange(peak, ChimeSynth.Peak * 0.9, ChimeSynth.Peak * 1.01);

        // Первая миллисекунда — ещё атака, последние десять — уже тишина (ниже −30 дБ от пика).
        var millisecond = ChimeSynth.SampleRate / 1000;
        Assert.True(samples.Take(millisecond).Max(sample => Math.Abs((int)sample)) < short.MaxValue * ChimeSynth.Peak * 0.15);
        Assert.True(samples.TakeLast(10 * millisecond).Max(sample => Math.Abs((int)sample)) < short.MaxValue * ChimeSynth.Peak * 0.03);
    }

    [Fact]
    public void Done_and_failed_sound_different()
    {
        Assert.NotEqual(ChimeSynth.Render(ChimeTune.Done), ChimeSynth.Render(ChimeTune.Failed));
    }
}

/// <summary>Итог прогона отложенной задачи по переписке и мелочи вокруг него.</summary>
public sealed class DeferredChatTests
{
    private static (ChatSession Session, ChatDisplayMessage User) Chat(params ChatDisplayMessage[] after)
    {
        var session = new ChatSession { Id = "c" };
        var user = new ChatDisplayMessage { Role = "user", Id = "u", Text = "go" };
        session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Id = "old", Status = AssistantStatus.Complete, Text = "earlier" });
        session.Messages.Add(user);
        session.Messages.AddRange(after);
        return (session, user);
    }

    [Fact]
    public void A_closed_answer_is_success_with_its_price_and_first_line()
    {
        var (session, user) = Chat(new ChatDisplayMessage
        {
            Role = "assistant",
            Status = AssistantStatus.Complete,
            Text = "\n  Disk C: 41 GB free.\nMore lines.",
            Cost = new VeniceCost { Usd = 0.012m, HasData = true }
        });

        var outcome = DeferredChat.OutcomeAfter(session, user);

        Assert.True(outcome.Success);
        Assert.Equal(0.012m, outcome.CostUsd);
        Assert.Equal("Disk C: 41 GB free.", outcome.Output);
        Assert.Equal("c", outcome.ChatId);
    }

    [Fact]
    public void An_error_or_no_answer_at_all_is_a_failure_and_says_why()
    {
        var (failed, user) = Chat(new ChatDisplayMessage { Role = "assistant", Status = AssistantStatus.Error, Text = "Provider refused: 402" });
        var (silent, alone) = Chat();

        Assert.Equal("Provider refused: 402", DeferredChat.OutcomeAfter(failed, user).Error);
        var none = DeferredChat.OutcomeAfter(silent, alone);
        Assert.False(none.Success);
        Assert.Equal(Loc.Get("S.Deferred.Result.NoReply"), none.Error);
    }

    [Fact]
    public void The_head_of_a_report_is_one_line_of_at_most_two_hundred_characters()
    {
        Assert.Equal(201, DeferredChat.Head(new string('x', 500)).Length);
        Assert.Equal("", DeferredChat.Head(null));
    }

    [Fact]
    public void The_date_line_names_the_day_and_the_offset_from_utc()
    {
        Assert.Equal(
            "Local date: 2026-10-09, Friday (UTC+03:00).",
            ChatEngine.DateLine(new DateTimeOffset(2026, 10, 9, 23, 59, 0, TimeSpan.FromHours(3))));
        Assert.Equal(
            "Local date: 2026-10-09, Friday (UTC-05:00).",
            ChatEngine.DateLine(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.FromHours(-5))));
    }

    [Fact]
    public void A_seal_edited_by_hand_into_garbage_does_not_verify_and_does_not_throw()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-seal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var book = new DeferredBook(root);
            var task = new DeferredTask { Id = "t", Kind = DeferredKind.Command, Command = "dir", Seal = "not hex at all" };

            Assert.False(book.Verify(task));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_scheduler_launch_is_its_own_quiet_start()
    {
        Assert.Equal(StartupAction.Wake, StartupArgs.Parse(["--wake"]).Action);
    }

    [Theory]
    [InlineData(DeferredBook.FileName)]
    [InlineData(DeferredBook.KeyName)]
    [InlineData(SessionSnapshot.FileName)]
    public void Deferred_files_stay_out_of_the_archive_and_go_with_the_profile(string name)
    {
        // Чужой архив не должен привезти заранее одобренные команды.
        Assert.Equal(DataCategory.None, DataBundle.CategoryOf(name));
        Assert.Contains(name, ProfileDataWiper.DefaultProfileFiles);
    }
}

/// <summary>Подписи карточек и вкладки «Отложенные» — без окна.</summary>
public sealed class DeferredLabelTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Snoozing_is_ten_minutes_an_hour_or_nine_tomorrow_morning()
    {
        var now = new DateTime(2026, 10, 10, 23, 30, 0, DateTimeKind.Local);

        Assert.Equal(now.AddMinutes(10).ToUniversalTime(), MainWindow.SnoozeUntil(ReminderSnooze.TenMinutes, now));
        Assert.Equal(now.AddHours(1).ToUniversalTime(), MainWindow.SnoozeUntil(ReminderSnooze.OneHour, now));
        Assert.Equal(new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Local).ToUniversalTime(), MainWindow.SnoozeUntil(ReminderSnooze.TomorrowMorning, now));
    }

    [Fact]
    public void The_reminder_line_says_how_late_and_how_many_were_missed()
    {
        var task = new DeferredTask { AwaitingAckSinceUtc = Now, MissedWhileUnacked = 2 };

        var meta = MainWindow.ReminderMeta(task, TimeSpan.FromHours(3), DeferredClockTests.Facts(Now));

        Assert.Contains(DeferredText.Late(TimeSpan.FromHours(3)), meta);
        Assert.Contains(Loc.Format("S.Deferred.Toast.Missed", 2), meta);
        Assert.DoesNotContain(Loc.Format("S.Deferred.Toast.Missed", 0), MainWindow.ReminderMeta(new DeferredTask(), null, DeferredClockTests.Facts(Now)));
    }

    [Fact]
    public void Restoring_programs_reports_what_opened_and_what_did_not()
    {
        Assert.Equal(Loc.Get("S.Deferred.Result.RestoreNone"), MainWindow.RestoreSummary(new RestoreResult([], [], NothingToDo: true)));
        var mixed = MainWindow.RestoreSummary(new RestoreResult(["Word", "Excel"], ["Telegram"], NothingToDo: false));
        Assert.StartsWith(Loc.Format("S.Deferred.Result.Restored", 2), mixed);
        Assert.Contains("Telegram", mixed);
    }

    private static DeferredTask Task(string id, DeferredStatus status, DateTime? due = null, DeferredRepeatKind repeat = DeferredRepeatKind.Once, DateTime? fired = null) => new()
    {
        Id = id,
        Title = id,
        Kind = DeferredKind.Reminder,
        Status = status,
        CreatedUtc = Now.AddDays(-1),
        NextDueUtc = due,
        LastFiredUtc = fired,
        Trigger = new DeferredTrigger { Kind = DeferredTriggerKind.At },
        Repeat = new DeferredRepeat { Kind = repeat, EveryMinutes = 60 }
    };

    [Fact]
    public void Waiting_tasks_come_by_due_time_and_finished_ones_newest_first_and_capped()
    {
        var tasks = new List<DeferredTask>
        {
            Task("later", DeferredStatus.Pending, Now.AddHours(5)),
            Task("sooner", DeferredStatus.Pending, Now.AddHours(1)),
            Task("paused", DeferredStatus.Paused, Now.AddHours(-1), DeferredRepeatKind.Every)
        };
        tasks.AddRange(Enumerable.Range(0, 20).Select(i => Task("done" + i, DeferredStatus.Done, fired: Now.AddMinutes(-i))));

        var (waiting, history) = DeferredPanel.Rows(tasks, DeferredClockTests.Facts(Now), DateFormat.DayMonthShort);

        Assert.Equal(["paused", "sooner", "later"], waiting.Select(row => row.Id));
        Assert.Equal(DeferredPanel.HistoryShown, history.Count);
        Assert.Contains("done0", history[0].Text);
    }

    [Fact]
    public void A_one_time_task_names_its_moment_once_and_a_repeating_one_adds_the_next()
    {
        var facts = DeferredClockTests.Facts(Now);
        var once = new DeferredRow(Task("once", DeferredStatus.Pending, Now.AddHours(2)), facts);
        var repeating = new DeferredRow(Task("every", DeferredStatus.Pending, Now.AddHours(2), DeferredRepeatKind.Every), facts);

        Assert.Equal(Loc.Get("S.Deferred.Status.Pending"), once.State);
        Assert.Equal(Loc.Get("S.Deferred.Status.Pending") + " · " + DeferredText.Moment(Now.AddHours(2), facts), repeating.State);
    }

    [Fact]
    public void Each_state_offers_the_action_that_fits_it()
    {
        var facts = DeferredClockTests.Facts(Now);

        Assert.Equal(Loc.Get("S.Deferred.RunNow"), new DeferredRow(Task("p", DeferredStatus.Pending, Now), facts).ActionText);
        Assert.Equal(Loc.Get("S.Deferred.Toast.Retry"), new DeferredRow(Task("i", DeferredStatus.Interrupted, Now), facts).ActionText);
        Assert.Equal(Loc.Get("S.Deferred.Resume"), new DeferredRow(Task("z", DeferredStatus.Paused, Now), facts).ActionText);

        // Идущую задачу не отменяют отсюда: её останавливают «Стопом» в её чате.
        var running = new DeferredRow(Task("r", DeferredStatus.Running, Now), facts);
        Assert.Equal(System.Windows.Visibility.Collapsed, running.CancelVisibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, running.ActionVisibility);
    }
}
