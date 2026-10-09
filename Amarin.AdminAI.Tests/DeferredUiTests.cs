using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Отложенные задачи в окне (1.33.0): стопка карточек напоминаний, прогон в чате задачи и
/// отметки в ленте.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class DeferredUiTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-deferred-ui-" + Guid.NewGuid().ToString("N"));

    public DeferredUiTests(WpfFixture wpf) => _wpf = wpf;

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

    // ───────────────────────── стопка карточек ─────────────────────────

    /// <summary>Стопка без показа окна: окно поверх всех встало бы в угол настоящего экрана.</summary>
    private static ReminderStack Stack() => new(() => 100, () => IntPtr.Zero) { ShowWindow = _ => { } };

    private static void Fill(ReminderItem item, string text) => item.Heading = text;

    [Fact]
    public void Three_cards_are_on_screen_and_the_top_one_counts_the_rest()
    {
        var (shown, more, restMore) = _wpf.Ui.Invoke(() =>
        {
            var stack = Stack();
            foreach (var id in new[] { "a", "b", "c", "d", "e" })
            {
                stack.Put(id, ReminderLook.Reminder, item => Fill(item, id));
            }

            var window = stack.Window!;
            var result = (window.Items.Select(item => item.TaskId).ToList(), window.Items[0].More, window.Items[1].More);
            stack.Clear();
            return result;
        });

        Assert.Equal(["a", "b", "c"], shown);
        Assert.Equal(Loc.Format("S.Deferred.Toast.More", 2), more);
        Assert.Equal("", restMore);
    }

    [Fact]
    public void A_task_keeps_one_card_and_a_removed_card_makes_room_for_the_next()
    {
        var (same, count, shown, more, closed) = _wpf.Ui.Invoke(() =>
        {
            var stack = Stack();
            var first = stack.Put("a", ReminderLook.Reminder, item => Fill(item, "first"));
            foreach (var id in new[] { "b", "c", "d" })
            {
                stack.Put(id, ReminderLook.Reminder, item => Fill(item, id));
            }

            // Повторный срок неподтверждённого напоминания обновляет его карточку, а не ставит вторую.
            first.Snoozing = true;
            var again = stack.Put("a", ReminderLook.Reminder, item => Fill(item, "again"));
            var sameCard = ReferenceEquals(first, again) && again.Heading == "again" && !again.Snoozing;
            var total = stack.Items.Count;

            stack.Remove("a");
            var window = stack.Window!;
            var result = (sameCard, total, window.Items.Select(item => item.TaskId).ToList(), window.Items[0].More, false);
            stack.Clear();
            return result with { Item5 = stack.Window is null };
        });

        Assert.True(same);
        Assert.Equal(4, count);
        Assert.Equal(["b", "c", "d"], shown);
        Assert.Equal("", more);
        Assert.True(closed);
    }

    [Fact]
    public void The_cross_on_a_reminder_means_done_and_on_anything_else_just_hides_it()
    {
        var actions = _wpf.Ui.Invoke(() =>
        {
            var toast = new ReminderToast();
            var seen = new List<ReminderAction>();
            toast.Acted += (_, action, _) => seen.Add(action);
            foreach (var item in new[]
                     {
                         new ReminderItem("r", ReminderLook.Reminder),
                         new ReminderItem("h", ReminderLook.Reminder) { Hidden = true },
                         new ReminderItem("i", ReminderLook.Interrupted)
                     })
            {
                Call(toast, "Close_Click", new Button { DataContext = item }, new RoutedEventArgs());
            }

            toast.Close();
            return seen;
        });

        Assert.Equal([ReminderAction.Done, ReminderAction.Dismiss, ReminderAction.Dismiss], actions);
    }

    [Fact]
    public void Snooze_opens_its_choices_in_place_and_a_choice_carries_how_long()
    {
        var (snoozing, picked) = _wpf.Ui.Invoke(() =>
        {
            var toast = new ReminderToast();
            var item = new ReminderItem("r", ReminderLook.Reminder);
            var chosen = ReminderSnooze.None;
            toast.Acted += (_, action, snooze) => chosen = action == ReminderAction.Snooze ? snooze : chosen;

            Call(toast, "Snooze_Click", new Button { DataContext = item }, new RoutedEventArgs());
            var opened = item.Snoozing;
            Call(toast, "SnoozeFor_Click", new Button { DataContext = item, Tag = ReminderSnooze.OneHour }, new RoutedEventArgs());
            toast.Close();
            return (opened, chosen);
        });

        Assert.True(snoozing);
        Assert.Equal(ReminderSnooze.OneHour, picked);
    }

    [Fact]
    public void A_reminder_answered_elsewhere_leaves_the_screen()
    {
        var left = With((window, services) =>
        {
            var task = services.Deferred.Add(new DeferredTask
            {
                Kind = DeferredKind.Reminder,
                Title = "Call",
                Status = DeferredStatus.Done,
                AwaitingAckSinceUtc = DateTime.UtcNow,
                CreatedUtc = DateTime.UtcNow
            });
            var stack = Stack();
            typeof(MainWindow).GetField("_reminders", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, stack);
            stack.Put(task.Id, ReminderLook.Reminder, item => Fill(item, task.Title));

            // «Готово» нажали на вкладке или в другой карточке: книга изменилась, карточка уходит.
            services.Deferred.Update(task.Id, stored => stored.AwaitingAckSinceUtc = null);
            Call(window, "OnDeferredChanged");
            Idle();
            return stack.Items.Count;
        });

        Assert.Equal(0, left);
    }

    // ───────────────────────── прогон в чате ─────────────────────────

    private static DeferredTask Restore(AppServices services, string chatId)
    {
        var task = services.Deferred.Add(new DeferredTask
        {
            ChatId = chatId,
            Kind = DeferredKind.RestorePrograms,
            Title = "Reopen programs",
            Status = DeferredStatus.Running,
            CreatedUtc = DateTime.UtcNow.AddHours(-1),
            Trigger = new DeferredTrigger { Kind = DeferredTriggerKind.NextBoot }
        });
        var seal = services.Deferred.Seal(task);
        services.Deferred.Update(task.Id, stored => stored.Seal = seal);
        return services.Deferred.Peek(task.Id)!;
    }

    private static DeferredOutcome? Run(MainWindow window, DeferredTask task) =>
        Pump(((IDeferredHost)window).RunAsync(task, TimeSpan.FromHours(2), CancellationToken.None));

    [Fact]
    public void A_task_runs_in_its_own_chat_with_a_marked_message_and_a_recorded_answer()
    {
        var (outcome, saved) = With((window, services) =>
        {
            var task = Restore(services, "b");
            var result = Run(window, task);
            services.ChatStore.Flush();
            return (result, services.ChatStore.TryLoad("b"));
        });

        Assert.NotNull(outcome);
        Assert.True(outcome.Success);
        Assert.Equal("b", outcome.ChatId);
        Assert.NotNull(saved);
        var user = saved.Messages[^2];
        var answer = saved.Messages[^1];
        Assert.Equal("user", user.Role);
        Assert.NotNull(user.Deferred);
        Assert.Equal(TimeSpan.FromHours(2), user.Deferred.LateBy);
        Assert.Equal(AssistantStatus.Complete, answer.Status);
        Assert.Equal(Loc.Get("S.Deferred.Result.RestoreNone"), answer.Text);

        // Продолживший разговор знает, что произошло: пара записей в истории модели.
        Assert.Equal(2, saved.ApiMessages.Count);
    }

    [Fact]
    public void A_task_whose_chat_was_deleted_reports_into_a_new_one()
    {
        var (outcome, title) = With((window, services) =>
        {
            var task = Restore(services, "gone");
            var result = Run(window, task);
            services.ChatStore.Flush();
            return (result, result?.ChatId is { } id ? services.ChatStore.TryLoad(id)?.Title : null);
        });

        Assert.NotNull(outcome);
        Assert.NotEqual("gone", outcome.ChatId);
        Assert.Equal(Loc.Format("S.Deferred.ChatTitle", "Reopen programs"), title);
    }

    [Fact]
    public void A_busy_chat_says_later_instead_of_running_a_second_turn()
    {
        var (outcome, messages) = With((window, services) =>
        {
            var task = Restore(services, "a");
            var session = services.ChatStore.TryLoad("a")!;
            var busy = window.Turns.TryStart(session, TurnKind.Send, DateTime.Now).Turn!;
            try
            {
                return (Run(window, task), session.Messages.Count);
            }
            finally
            {
                window.FinishTurn(busy);
            }
        });

        Assert.Null(outcome);
        Assert.Equal(0, messages);
    }

    [Fact]
    public void A_task_of_the_open_chat_writes_into_what_is_on_screen()
    {
        var (sameObject, count) = With((window, services) =>
        {
            Call(window, "OpenChat", "c");
            var open = (ChatSession)typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Run(window, Restore(services, "c"));
            var after = (ChatSession)typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            return (ReferenceEquals(open, after), open.Messages.Count);
        });

        Assert.True(sameObject);
        Assert.Equal(2, count);
    }

    [Fact]
    public void A_command_without_a_valid_seal_is_not_run_and_the_chat_says_so()
    {
        var (outcome, answer) = With((window, services) =>
        {
            var task = services.Deferred.Add(new DeferredTask
            {
                ChatId = "a",
                Kind = DeferredKind.Command,
                Title = "Flush DNS",
                Command = "Clear-DnsClientCache",
                Status = DeferredStatus.Running,
                CreatedUtc = DateTime.UtcNow,
                Seal = "00"
            });
            var result = Run(window, task);
            services.ChatStore.Flush();
            return (result, services.ChatStore.TryLoad("a")!.Messages[^1]);
        });

        Assert.NotNull(outcome);
        Assert.False(outcome.Success);
        Assert.Equal(Loc.Get("S.Deferred.Result.Unsealed"), outcome.Error);
        Assert.Equal(AssistantStatus.Error, answer.Status);
        Assert.Contains(Loc.Get("S.Deferred.Result.Unsealed"), answer.Text);
    }

    // ───────────────────────── отметки в ленте ─────────────────────────

    [Fact]
    public void The_chip_under_an_answer_names_the_task_and_where_it_stands()
    {
        var (texts, enabled) = With((window, _) =>
        {
            var opened = false;
            var actions = new MessageActions { OpenDeferred = () => opened = true, DeferredState = _ => "tomorrow, 09:00" };
            var strip = ChatMessageViews.CreateDeferredStrip(window, [new DeferredRef("t1", "Restart the PC", DeferredKind.Reminder)], actions);
            var chip = Descendants<Button>(strip).Single();
            chip.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, chip));
            return (Descendants<TextBlock>(strip).Select(block => block.Text ?? "").ToList(), opened && chip.IsEnabled);
        });

        Assert.Contains("Restart the PC", texts);
        Assert.Contains("· tomorrow, 09:00", texts);
        Assert.True(enabled);
    }

    [Fact]
    public void A_message_a_task_wrote_carries_its_due_time_and_lateness()
    {
        var texts = With((window, _) =>
        {
            var mark = new DeferredMark("t1", "Check disks", DateTime.UtcNow.AddHours(-2), TimeSpan.FromHours(2));
            var chip = ChatMessageViews.CreateDeferredMark(window, mark, null);
            return Descendants<TextBlock>(chip).Select(block => block.Text ?? "").ToList();
        });

        Assert.Contains("Check disks", texts);
        Assert.Contains(texts, text => text.Contains(DeferredText.Late(TimeSpan.FromHours(2)), StringComparison.Ordinal));
    }

    // ───────────────────────── помощники ─────────────────────────

    private T With<T>(Func<MainWindow, AppServices, T> body) => _wpf.Ui.Invoke(() =>
    {
        var services = UiServices.Build(Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]), "k", new HttpClientHandler());
        var now = DateTime.Now;
        foreach (var id in new[] { "a", "b", "c" })
        {
            services.ChatStore.Save(new ChatSession { Id = id, Title = "Chat " + id, CreatedAt = now, UpdatedAt = now });
        }

        services.ChatStore.Flush();
        var window = new MainWindow
        {
            Width = 1100,
            Height = 900,
            Left = -32000,
            Top = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.AttachServices(services);
        window.Show();
        try
        {
            return body(window, services);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Крутит очередь окна, пока задача не кончится: прогон сам ходит на поток окна.</summary>
    private static T Pump<T>(Task<T> task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Доводит до конца всё, что окно отложило на простой.</summary>
    private static void Idle() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    private static object? Call(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, args);

    /// <summary>Потомки по логическому дереву: отметки в тесте не разложены, и визуального дерева у них ещё нет.</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
