using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Реестр ходов без окна. До 1.30.0 это были поля главного окна, и тесты доставали словарь ходов
/// отражением — переименуй поле, и они упали бы только на Windows.
/// </summary>
public sealed class TurnRegistryTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void A_chat_has_at_most_one_turn()
    {
        var turns = new TurnRegistry();
        var chat = Chat("s1");

        var first = turns.TryStart(chat, TurnKind.Send, Now);
        var second = turns.TryStart(chat, TurnKind.Send, Now);

        Assert.True(first.Started);
        Assert.Equal(TurnRefusal.AlreadyRunning, second.Refusal);
        Assert.Null(second.Turn);
        Assert.Same(first.Turn, turns.Find("s1"));
        Assert.True(turns.IsBusy("s1"));
        Assert.Equal(1, turns.Count);
    }

    [Fact]
    public void No_more_than_three_turns_run_at_once()
    {
        var turns = new TurnRegistry();
        var started = Enumerable.Range(0, TurnRegistry.MaxParallel)
            .Select(i => turns.TryStart(Chat("s" + i), TurnKind.Send, Now).Turn)
            .ToList();

        Assert.False(turns.HasRoom);
        Assert.Equal(TurnRefusal.LimitReached, turns.TryStart(Chat("extra"), TurnKind.Send, Now).Refusal);
        Assert.False(turns.IsBusy("extra"));

        // Кончился один — место освободилось.
        Assert.True(turns.Finish(Assert.IsType<RunningTurn>(started[0])));
        Assert.True(turns.TryStart(Chat("extra"), TurnKind.Send, Now).Started);
    }

    [Fact]
    public void A_late_finish_of_an_old_turn_does_not_remove_the_new_one()
    {
        var turns = new TurnRegistry();
        var chat = Chat("s1");
        var old = Started(turns, chat);
        Assert.True(turns.Finish(old));
        var fresh = Started(turns, chat);

        Assert.False(turns.Finish(old));
        Assert.Same(fresh, turns.Find("s1"));
        Assert.True(fresh.Cancellation.Token.CanBeCanceled);
    }

    [Fact]
    public void Finishing_marks_the_turn_done_and_releases_its_cancellation()
    {
        var turns = new TurnRegistry();
        var turn = Started(turns, Chat("s1"));

        Assert.True(turns.Finish(turn));

        Assert.True(turn.Finished);
        Assert.False(turns.IsBusy("s1"));
        Assert.Throws<ObjectDisposedException>(() => turn.Cancellation.Token.WaitHandle);
    }

    [Fact]
    public void What_was_typed_during_a_cut_turn_still_reaches_the_history()
    {
        // Пузырь дописанного человек уже видит; оборванный ход не должен выкинуть строку из
        // контекста — иначе следующий ответ выглядел бы так, будто модель её проигнорировала.
        var turns = new TurnRegistry();
        var chat = Chat("s1");
        var turn = Started(turns, chat);
        turn.Enqueue("первое");
        turn.Enqueue("  второе  ");
        turn.Enqueue("   ");

        turns.Finish(turn);

        Assert.Equal<string?>(["первое", "второе"], chat.ApiMessages.Select(message => message.Content?.GetString()));
        Assert.All(chat.ApiMessages, message => Assert.Equal("user", message.Role));
        Assert.False(turn.HasQueued);
    }

    [Fact]
    public void Finishing_ends_what_was_allowed_until_the_end_of_the_answer()
    {
        var confirmations = new ConfirmationQueue(() => new AppSettings());
        var turns = new TurnRegistry(() => confirmations);
        var turn = Started(turns, Chat("s1"));
        confirmations.Allow("s1", "write_file", AllowanceScope.Turn);
        confirmations.Allow("s1", "registry", AllowanceScope.Chat);

        turns.Finish(turn);

        Assert.Null(confirmations.AllowedAhead("s1", "write_file"));
        Assert.Equal(ApprovalSource.AllowChat, confirmations.AllowedAhead("s1", "registry"));
    }

    [Fact]
    public async Task Cancelling_a_chat_stops_its_turn_and_its_questions_only()
    {
        var confirmations = new ConfirmationQueue(() => new AppSettings());
        var turns = new TurnRegistry(() => confirmations);
        var mine = Started(turns, Chat("s1"));
        var other = Started(turns, Chat("s2"));
        var myQuestion = confirmations.ConfirmDetailedAsync("Чат", Question(), "s1");
        var otherQuestion = confirmations.ConfirmDetailedAsync("Чат", Question(), "s2");

        Assert.True(turns.Cancel("s1"));

        Assert.True(mine.Cancellation.IsCancellationRequested);
        Assert.False(other.Cancellation.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => myQuestion);
        Assert.False(otherQuestion.IsCompleted);
        Assert.False(turns.Cancel("nobody"));

        // Отмена не снимает ход с учёта: это делает его финал.
        Assert.True(turns.IsBusy("s1"));
        confirmations.CancelAll();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => otherQuestion);
    }

    [Fact]
    public async Task Cancelling_everything_stops_every_turn_and_every_question()
    {
        var confirmations = new ConfirmationQueue(() => new AppSettings());
        var turns = new TurnRegistry(() => confirmations);
        var first = Started(turns, Chat("s1"));
        var second = Started(turns, Chat("s2"));
        var stray = confirmations.ConfirmDetailedAsync("Агент", Question(), sessionId: null);

        turns.CancelAll();

        Assert.True(first.Cancellation.IsCancellationRequested);
        Assert.True(second.Cancellation.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stray);
    }

    [Fact]
    public void A_cancelled_turn_can_still_finish_cleanly()
    {
        var turns = new TurnRegistry();
        var turn = Started(turns, Chat("s1"));

        turns.Cancel("s1");

        Assert.True(turns.Finish(turn));
        Assert.False(turns.Cancel("s1"));
    }

    [Fact]
    public void Attention_marks_are_set_once_and_can_be_forgotten()
    {
        var turns = new TurnRegistry();

        Assert.True(turns.MarkAttention("s1"));
        Assert.False(turns.MarkAttention("s1"));
        Assert.True(turns.MarkAttention("s2"));
        Assert.True(turns.NeedsAttention("s1"));

        Assert.True(turns.ForgetAttention("s1"));
        Assert.False(turns.NeedsAttention("s1"));
        Assert.False(turns.ForgetAttention("s1"));

        turns.ForgetAllAttention();
        Assert.Empty(turns.Attention);
    }

    [Fact]
    public void A_composer_notice_belongs_to_its_chat_and_leaves_with_its_turn()
    {
        var turns = new TurnRegistry();
        var turn = Started(turns, Chat("s1"));
        turns.ShowNotice("s1", "отправлено, учту");

        Assert.Equal("отправлено, учту", turns.NoticeFor("s1"));
        Assert.Null(turns.NoticeFor("s2"));

        turns.Finish(turn);

        Assert.Null(turns.NoticeFor("s1"));
        Assert.False(turns.ClearNotice("s1"));
    }

    [Fact]
    public void Running_chat_ids_are_a_snapshot()
    {
        var turns = new TurnRegistry();
        Started(turns, Chat("s1"));
        Started(turns, Chat("s2"));

        // Снимок перечисляется, даже когда ходы по ходу дела снимаются с учёта.
        foreach (var id in turns.RunningChatIds)
        {
            turns.Finish(turns.Find(id) ?? throw new InvalidOperationException(id));
        }

        Assert.False(turns.AnyRunning);
    }

    [Fact]
    public void A_turn_runs_with_its_chat_machine_read_only_flag_and_spend_meter()
    {
        var machine = new RemoteMachine { Id = "m1", Name = "Сервер", Address = "srv" };
        var chat = Chat("s1");
        chat.ReadOnly = true;

        bool readOnlyInside;
        RemoteMachine? targetInside;
        SpendMeter? meterInside;
        using (TurnScopes.Enter(chat, machine))
        {
            readOnlyInside = ToolGate.IsReadOnly(new AppSettings());
            targetInside = ExecutionTarget.Current;
            meterInside = SpendScope.Current;
        }

        Assert.True(readOnlyInside);
        Assert.Same(machine, targetInside);
        Assert.NotNull(meterInside);

        Assert.False(ToolGate.IsReadOnly(new AppSettings()));
        Assert.Null(ExecutionTarget.Current);
        Assert.Null(SpendScope.Current);
    }

    [Fact]
    public void A_writable_chat_on_this_pc_is_not_forced_read_only()
    {
        using (TurnScopes.Enter(Chat("s1"), target: null))
        {
            Assert.False(ToolGate.IsReadOnly(new AppSettings()));
            Assert.Null(ExecutionTarget.Current);
            Assert.NotNull(SpendScope.Current);
        }
    }

    private static ChatSession Chat(string id) => new() { Id = id, Title = "Чат", CreatedAt = Now, UpdatedAt = Now };

    private static RunningTurn Started(TurnRegistry turns, ChatSession chat) =>
        turns.TryStart(chat, TurnKind.Send, Now).Turn ?? throw new InvalidOperationException("ход не завёлся");

    private static DangerousActionInfo Question() =>
        new("write_file", "Записать файл", "C:\\temp\\a.txt", DangerousRiskLevel.Medium);
}

/// <summary>
/// Отложенная запись чатов идущего хода. До 1.30.0 — словарь и таймер диспетчера в окне.
/// </summary>
public sealed class ChatPersistQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-persist-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly UiQueue _ui = new();
    private readonly ChatStore _store;
    private int _saved;

    public ChatPersistQueueTests()
    {
        Directory.CreateDirectory(_root);
        _store = new ChatStore(_root);
    }

    [Fact]
    public void A_scheduled_chat_is_written_after_a_quiet_pause()
    {
        using var queue = Queue();
        var chat = Chat("s1");

        queue.Schedule(chat);
        _time.Advance(ChatPersistQueue.Delay - TimeSpan.FromMilliseconds(1));
        _ui.Run();
        Assert.Equal(0, _saved);
        Assert.True(queue.IsPending("s1"));

        _time.Advance(TimeSpan.FromMilliseconds(1));
        _ui.Run();

        Assert.Equal(1, _saved);
        Assert.False(queue.IsPending("s1"));
        Assert.Contains(_store.List(), entry => entry.Id == "s1");
    }

    [Fact]
    public void Every_new_piece_of_the_answer_moves_the_pause()
    {
        using var queue = Queue();
        var chat = Chat("s1");

        queue.Schedule(chat);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        queue.Schedule(chat);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        _ui.Run();
        Assert.Equal(0, _saved);

        _time.Advance(ChatPersistQueue.Delay);
        _ui.Run();
        Assert.Equal(1, _saved);
    }

    [Fact]
    public void Flush_writes_everything_at_once_and_the_timer_stays_quiet()
    {
        using var queue = Queue();
        queue.Schedule(Chat("s1"));
        queue.Schedule(Chat("s2"));

        queue.Flush();
        Assert.Equal(2, _saved);

        _time.Advance(ChatPersistQueue.Delay * 2);
        _ui.Run();
        Assert.Equal(2, _saved);
    }

    [Fact]
    public void An_empty_chat_or_a_window_without_storage_writes_nothing()
    {
        using var queue = Queue();
        queue.Persist(new ChatSession { Id = "empty" });
        Assert.Equal(0, _saved);

        using var detached = new ChatPersistQueue(() => null, () => _saved++, _ui.Post, _time);
        detached.Persist(Chat("s1"));
        Assert.Equal(0, _saved);
    }

    public void Dispose()
    {
        _store.Flush();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка — уберёт система.
        }
    }

    private ChatPersistQueue Queue() => new(() => _store, () => _saved++, _ui.Post, _time);

    private static ChatSession Chat(string id)
    {
        var now = DateTime.Now;
        return new ChatSession
        {
            Id = id,
            Title = "Чат",
            CreatedAt = now,
            UpdatedAt = now,
            Messages = [new ChatDisplayMessage { Role = "user", Id = id + "-u", CreatedAt = now, Text = "привет" }]
        };
    }
}
