namespace Amarin.Core;

/// <summary>Почему ход не начался.</summary>
internal enum TurnRefusal
{
    /// <summary>Начался.</summary>
    None,

    /// <summary>В этом чате ход уже идёт — новое сообщение встаёт в его очередь, а не заводит второй.</summary>
    AlreadyRunning,

    /// <summary>Одновременно идёт столько ходов, сколько можно (<see cref="TurnRegistry.MaxParallel"/>).</summary>
    LimitReached
}

/// <summary>Чем кончилась попытка начать ход: сам ход или причина отказа.</summary>
internal readonly record struct TurnStart(RunningTurn? Turn, TurnRefusal Refusal)
{
    public bool Started => Turn is not null;
}

/// <summary>
/// Идущие ходы чатов: по одному на чат, несколько одновременно; метки «ответ готов» у чатов,
/// которые сейчас не на экране; подписи под композером — у каждого чата своя.
/// </summary>
/// <remarks>
/// <para>
/// До 1.30.0 всё это было полями главного окна (<c>_turns</c>, <c>_attention</c>,
/// <c>_composerNotices</c>) и проверялось только оконными тестами, которые доставали словарь
/// отражением. Здесь — состояние и решения, без WPF; окно рисует по ним и передаёт нажатия.
/// </para>
/// <para>
/// «По одному на чат» осталось в силе и после того, как в идущий ход разрешили дописывать: новое
/// сообщение не заводит второй <see cref="RunningTurn"/>, а встаёт в очередь этого же
/// (<see cref="RunningTurn.Enqueue"/>) и вливается в контекст на границе раунда. Стенограмма
/// <c>ApiMessages</c> от этого остаётся линейной — два хода вперемешку дали бы историю, которую
/// не прочитают ни человек, ни модель.
/// </para>
/// <para>
/// Живёт на потоке окна: читается и меняется только оттуда. Очереди вопросов — функциями, потому
/// что службы окна заводятся позже, чем оно само.
/// </para>
/// </remarks>
internal sealed class TurnRegistry(Func<ConfirmationQueue?>? confirmations = null, Func<PlanReviewQueue?>? planReviews = null)
{
    /// <summary>
    /// Сколько ответов может идти одновременно.
    /// </summary>
    /// <remarks>
    /// Не настройка и не «сколько угодно»: каждый ход способен поднять до четырёх агентов, а
    /// <see cref="AgentSlotLimiter.MaxAgents"/> — общий на программу, и каждый агент гоняет
    /// PowerShell по этой же машине. При неограниченном числе ходов очередь подтверждений
    /// наполнялась бы вопросами из чатов, о которых человек уже забыл.
    /// </remarks>
    public const int MaxParallel = 3;

    private readonly Dictionary<string, RunningTurn> _turns = new(StringComparer.Ordinal);

    /// <summary>
    /// Чаты, в которых ответ доспел, пока смотрели другой. Здесь, а не на строке списка: список
    /// пересобирается целиком, и всё, что лежало бы на кнопке, пропадало бы при перерисовке.
    /// </summary>
    private readonly HashSet<string> _attention = new(StringComparer.Ordinal);

    /// <summary>
    /// Подписи под композером по чатам. Подпись одна на окно, а чатов много: «отправлено, учту» из
    /// одного разговора висела над всеми остальными, потому что гасить её было некому.
    /// </summary>
    private readonly Dictionary<string, string> _notices = new(StringComparer.Ordinal);

    /// <summary>Сколько ходов идёт сейчас.</summary>
    public int Count => _turns.Count;

    /// <summary>Занята ли программа целиком — для обновления, смены профиля, отката.</summary>
    public bool AnyRunning => _turns.Count > 0;

    /// <summary>Чаты, где идёт ход. Снимок: перечислять можно, даже снимая ходы с учёта.</summary>
    public IReadOnlyList<string> RunningChatIds => [.. _turns.Keys];

    /// <summary>Можно ли начать ещё один ход — проверка до развилки, а не после неё.</summary>
    public bool HasRoom => _turns.Count < MaxParallel;

    /// <summary>Чаты с меткой «ответ готов».</summary>
    public IReadOnlyCollection<string> Attention => _attention;

    /// <summary>Идёт ли ход в этом чате. В одном чате больше одного хода не бывает.</summary>
    public bool IsBusy(string sessionId) => _turns.ContainsKey(sessionId);

    public RunningTurn? Find(string sessionId) => _turns.GetValueOrDefault(sessionId);

    /// <summary>
    /// Завести ход. Отказ — не исключение: «в этом чате уже отвечают» и «три ответа сразу» — обычные
    /// ответы, и окно показывает их подписью.
    /// </summary>
    public TurnStart TryStart(ChatSession session, TurnKind kind, DateTime startedAt)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (IsBusy(session.Id))
        {
            return new TurnStart(null, TurnRefusal.AlreadyRunning);
        }

        if (!HasRoom)
        {
            return new TurnStart(null, TurnRefusal.LimitReached);
        }

        var turn = new RunningTurn
        {
            Session = session,
            Cancellation = new CancellationTokenSource(),
            Kind = kind,
            StartedAt = startedAt
        };

        _turns[session.Id] = turn;
        return new TurnStart(turn, TurnRefusal.None);
    }

    /// <summary>
    /// Снять ход с учёта. Единственное место, через которое проходит любой конец хода.
    /// </summary>
    /// <returns>
    /// <c>false</c> — этот ход уже снят или его место занял другой: поздний финал прежнего хода не
    /// должен снимать новый.
    /// </returns>
    public bool Finish(RunningTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (!_turns.TryGetValue(turn.SessionId, out var registered) || !ReferenceEquals(registered, turn))
        {
            return false;
        }

        _turns.Remove(turn.SessionId);
        turn.Finished = true;

        // «Разрешить до конца ответа» заканчивается вместе с ответом.
        confirmations?.Invoke()?.EndTurn(turn.SessionId);
        RescueQueued(turn);
        _notices.Remove(turn.SessionId);

        try
        {
            turn.Cancellation.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Уже освобождён — ничего страшного.
        }

        return true;
    }

    /// <summary>Оборвать ход чата и снять его вопросы.</summary>
    /// <returns><c>false</c> — в этом чате ход не шёл.</returns>
    public bool Cancel(string sessionId)
    {
        if (!_turns.TryGetValue(sessionId, out var turn))
        {
            return false;
        }

        try
        {
            turn.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ход мог завершиться сам и освободить CancellationTokenSource.
        }

        confirmations?.Invoke()?.CancelForSession(sessionId);
        planReviews?.Invoke()?.CancelForSession(sessionId);
        return true;
    }

    /// <summary>Оборвать все ходы: выход, смена профиля, «удалить все чаты».</summary>
    public void CancelAll()
    {
        foreach (var sessionId in RunningChatIds)
        {
            Cancel(sessionId);
        }

        confirmations?.Invoke()?.CancelAll();
    }

    /// <summary>Зажечь метку «ответ готов». Окно зовёт это, только если чат не на экране.</summary>
    /// <returns><c>true</c> — метки не было: список пора перерисовать.</returns>
    public bool MarkAttention(string sessionId) => _attention.Add(sessionId);

    /// <summary>
    /// Снять метку: чат открыли или удалили. Без этого идентификатор удалённого чата жил бы в
    /// наборе до перезапуска.
    /// </summary>
    public bool ForgetAttention(string sessionId) => _attention.Remove(sessionId);

    /// <summary>Снять все метки — после «удалить все чаты» и смены профиля.</summary>
    public void ForgetAllAttention() => _attention.Clear();

    public bool NeedsAttention(string sessionId) => _attention.Contains(sessionId);

    /// <summary>Подпись под композером этого чата.</summary>
    public void ShowNotice(string sessionId, string text) => _notices[sessionId] = text;

    /// <returns><c>true</c> — подпись была и снята.</returns>
    public bool ClearNotice(string sessionId) => _notices.Remove(sessionId);

    public string? NoticeFor(string sessionId) => _notices.GetValueOrDefault(sessionId);

    /// <summary>
    /// Дописанное, до чего ход не дожил, кладётся в стенограмму.
    /// </summary>
    /// <remarks>
    /// Пузырь такого сообщения человек уже видит: он рисуется в момент отправки. Если ход
    /// оборвали (отмена, сбой сети) раньше, чем движок забрал строку, она пропала бы только из
    /// контекста — на экране осталась бы, и следующий ответ выглядел бы так, будто модель её
    /// прочитала и пропустила мимо ушей.
    /// </remarks>
    public static void RescueQueued(RunningTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        while (turn.TryTakeQueued(out var text))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            turn.Session.ApiMessages.Add(new ChatMessage
            {
                Role = "user",
                Content = ChatContent.Text(text.Trim())
            });
        }
    }
}

/// <summary>
/// Окружение хода, которое видят все, кто работает внутри него: удалённая машина чата, «только
/// чтение» чата и счётчик лимитов трат.
/// </summary>
/// <remarks>
/// Цель чата (C10) видят шлюз, агент, исполнитель PowerShell и аудит; «только чтение» (D9) — шлюз
/// чата и агенты, запущенные из хода; счётчик (E1) — потолок хода вместе с агентами. Все три —
/// ambient (<see cref="AsyncLocal{T}"/>): метод синхронный, поэтому поставленное здесь видно
/// вызвавшему и всему, что он запустит, до <see cref="IDisposable.Dispose"/>.
/// </remarks>
internal static class TurnScopes
{
    public static IDisposable Enter(ChatSession session, RemoteMachine? target)
    {
        ArgumentNullException.ThrowIfNull(session);

        var machine = ExecutionTarget.Push(target);
        var readOnly = session.ReadOnly ? ToolGate.ForceReadOnly() : null;
        var spend = SpendScope.Push(new SpendMeter());
        return new Scopes(spend, readOnly, machine);
    }

    /// <summary>Снимает области в порядке, обратном установке, — как вложенные using.</summary>
    private sealed class Scopes(params IDisposable?[] inner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var scope in inner)
            {
                scope?.Dispose();
            }
        }
    }
}
