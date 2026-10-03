namespace Amarin.Core;

/// <summary>
/// Отложенная запись чатов, которые меняет идущий ход: сохранение по таймеру, а не на каждый
/// кусочек ответа.
/// </summary>
/// <remarks>
/// <para>
/// До 1.30.0 — поля окна (<c>_dirtySessions</c> и <c>DispatcherTimer</c>). Ход сохраняется дважды:
/// сразу — на границах (вопрос записан, ответ кончился) и с задержкой — пока поток идёт. Задержка
/// сдвигается каждым новым кусочком, поэтому пишется последнее состояние, а не каждое.
/// </para>
/// <para>
/// Живёт на потоке окна: срабатывание таймера возвращается туда через <c>post</c> — у окна это
/// очередь диспетчера с фоновым приоритетом, как было у прежнего таймера, чтобы запись не
/// вклинивалась перед отрисовкой и вводом.
/// </para>
/// </remarks>
internal sealed class ChatPersistQueue : IDisposable
{
    /// <summary>Сколько ждать тишины в потоке, прежде чем записать чат.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(750);

    private readonly Dictionary<string, ChatSession> _dirty = new(StringComparer.Ordinal);
    private readonly Func<ChatStore?> _store;
    private readonly Action _saved;
    private readonly ITimer _timer;

    /// <param name="store">Хранилище чатов. Функцией: службы окна заводятся позже, чем оно само.</param>
    /// <param name="saved">Чат поставлен в запись — список чатов пора перерисовать.</param>
    /// <param name="post">Как вернуть срабатывание таймера на поток окна.</param>
    public ChatPersistQueue(Func<ChatStore?> store, Action saved, Action<Action> post, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(post);

        _store = store;
        _saved = saved;
        _timer = (time ?? TimeProvider.System).CreateTimer(
            _ => post(Flush),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>Ждёт ли чат отложенной записи.</summary>
    public bool IsPending(string sessionId) => _dirty.ContainsKey(sessionId);

    /// <summary>
    /// Записать сейчас. Возвращается сразу: сериализация и диск — в фоновой задаче хранилища.
    /// Пустой чат не пишется: его ещё нет ни для человека, ни для списка.
    /// </summary>
    public void Persist(ChatSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_store() is not { } store || string.IsNullOrWhiteSpace(session.Id) || session.Messages.Count == 0)
        {
            return;
        }

        _dirty.Remove(session.Id);
        store.Save(session);
        _saved();
    }

    /// <summary>Записать после паузы; каждый новый вызов отодвигает её.</summary>
    public void Schedule(ChatSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _dirty[session.Id] = session;
        _timer.Change(Delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Записать всё накопившееся одним проходом — закрытие, выход, смена профиля.</summary>
    public void Flush()
    {
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        foreach (var session in _dirty.Values.ToArray())
        {
            Persist(session);
        }
    }

    public void Dispose() => _timer.Dispose();
}
