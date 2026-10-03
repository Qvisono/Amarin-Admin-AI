using Amarin.Tools;
using System.Diagnostics.CodeAnalysis;

namespace Amarin.Core;

/// <summary>Кто разрешил вызов — для журнала аудита и для отказа модели.</summary>
public enum ApprovalSource
{
    /// <summary>Спрашивать не требовалось: чтение или запись, которую правила пропускают молча.</summary>
    NotRequired,

    /// <summary>Человек ответил в окне.</summary>
    Human,

    /// <summary>Режим «подтверждать всё автоматически».</summary>
    Auto,

    /// <summary>Человек разрешил этот инструмент до конца хода.</summary>
    AllowTurn,

    /// <summary>Человек разрешил этот инструмент для всего чата.</summary>
    AllowChat,

    /// <summary>Человек разрешил вызов в вопросе SynGuard, который счёл его атакой.</summary>
    SynGuardHuman,

    /// <summary>Вызов — шаг плана, который человек одобрил целиком (<see cref="AgentPlans"/>).</summary>
    Plan
}

/// <summary>Насколько хватает разрешения, данного в окне подтверждения.</summary>
public enum AllowanceScope
{
    Once,
    Turn,
    Chat
}

/// <summary>Ответ на вопрос: разрешено ли и кем.</summary>
public sealed record ConfirmationAnswer(bool Approved, ApprovalSource Source)
{
    public static ConfirmationAnswer Refused { get; } = new(false, ApprovalSource.Human);
}

internal sealed class ConfirmationRequest
{
    public required string AgentLabel { get; init; }

    public required DangerousActionInfo Info { get; init; }

    public required TaskCompletionSource<ConfirmationAnswer> Completion { get; init; }

    /// <summary>
    /// Чат, из которого пришёл вопрос. Нужен, чтобы отмена одного хода не убивала подтверждения
    /// соседнего и чтобы в окне подтверждения было видно, о каком разговоре речь.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>Можно ли разрешить инструмент впрок: у вопроса есть чат, и это не вопрос SynGuard.</summary>
    public bool CanAllowAhead => !string.IsNullOrWhiteSpace(SessionId) && !Info.AlwaysAsk;

    /// <summary>
    /// Можно ли разрешить инструмент на весь чат. PowerShell — только до конца хода: разрешить
    /// его на весь разговор значило бы почти выключить защиту.
    /// </summary>
    public bool CanAllowForChat =>
        CanAllowAhead && !Info.ToolName.Equals("run_powershell", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Очередь вопросов «выполнить опасное действие?» — по одному на экране, остальные ждут.
/// </summary>
/// <remarks>
/// <para>
/// Список под замком, а не <c>ConcurrentQueue</c>: с несколькими одновременными ходами нужно
/// уметь выбросить из середины вопросы одного чата, не трогая порядок остальных, а из
/// <c>ConcurrentQueue</c> элемент из середины не достать.
/// </para>
/// <para>
/// Здесь же живут разрешения «до конца хода» и «для этого чата». Только в памяти: на диск они не
/// пишутся и перезапуск программы их снимает — разрешение на запись в систему не должно
/// переживать тот разговор, в котором его дали.
/// </para>
/// </remarks>
internal sealed class ConfirmationQueue
{
    private readonly List<ConfirmationRequest> _queue = [];
    private readonly Lock _gate = new();
    private readonly Func<AppSettings> _settings;
    private readonly Dictionary<string, Allowances> _allowances = new(StringComparer.Ordinal);

    private sealed class Allowances
    {
        public HashSet<string> Turn { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Chat { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public ConfirmationQueue(Func<AppSettings> settings) => _settings = settings;

    public event Action? Changed;

    public async Task<bool> ConfirmAsync(
        string agentLabel,
        DangerousActionInfo info,
        string? sessionId = null,
        CancellationToken cancellationToken = default) =>
        (await ConfirmDetailedAsync(agentLabel, info, sessionId, cancellationToken).ConfigureAwait(false)).Approved;

    /// <summary>
    /// Спрашивает человека — или отвечает сама, если ответ уже известен: режим «подтверждать всё
    /// автоматически» или разрешение этого инструмента в этом чате.
    /// </summary>
    /// <remarks>
    /// Вопрос встаёт в очередь синхронно, до первого ожидания. Поэтому вызовы раунда, спрошенные
    /// подряд без ожидания, встают в очередь ровно в своём порядке.
    /// </remarks>
    public async Task<ConfirmationAnswer> ConfirmDetailedAsync(
        string agentLabel,
        DangerousActionInfo info,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // AlwaysAsk минует режим «подтверждать всё автоматически»: так спрашивает SynGuard про
        // вызов, который счёл атакой, и удобство не вправе отвечать за человека на этот вопрос.
        if (!info.AlwaysAsk && _settings().ApprovalMode == ApprovalMode.AlwaysApprove)
        {
            return new ConfirmationAnswer(true, ApprovalSource.Auto);
        }

        if (!info.AlwaysAsk && AllowedAhead(sessionId, info.ToolName) is { } source)
        {
            return new ConfirmationAnswer(true, source);
        }

        var tcs = new TaskCompletionSource<ConfirmationAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new ConfirmationRequest
        {
            AgentLabel = agentLabel,
            Info = info,
            Completion = tcs,
            SessionId = sessionId
        };

        lock (_gate)
        {
            _queue.Add(request);
        }

        Changed?.Invoke();

        await using var registration = cancellationToken.Register(() =>
        {
            tcs.TrySetCanceled(cancellationToken);
            Changed?.Invoke();
        });

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            TryDrop(request);
        }
    }

    /// <summary>Сколько вопросов ждут ответа — для значка в трее и его меню (G1).</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count(request => !request.Completion.Task.IsCompleted);
            }
        }
    }

    public bool TryPeek([NotNullWhen(true)] out ConfirmationRequest? request)
    {
        lock (_gate)
        {
            while (_queue.Count > 0)
            {
                var head = _queue[0];
                if (!head.Completion.Task.IsCompleted)
                {
                    request = head;
                    return true;
                }

                _queue.RemoveAt(0);
            }
        }

        request = null;
        return false;
    }

    /// <summary>
    /// Отвечает на конкретный вопрос — тот, что человек видел на экране.
    /// </summary>
    /// <remarks>
    /// Кнопки окна раньше отвечали голове очереди. Между показом и нажатием голова успевала
    /// смениться — соседний чат отменил свой вопрос, и следующий встал первым, — и «Да» уходило
    /// вопросу, которого человек не читал. Вопрос, уже снятый отменой, ответа не получает.
    /// </remarks>
    public bool Complete(ConfirmationRequest request, bool approved) =>
        Complete(request, approved, AllowanceScope.Once);

    /// <summary>
    /// Отвечает на показанный вопрос и, если человек разрешил инструмент впрок, запоминает это
    /// и отвечает тем же ждущим вопросам того же чата про тот же инструмент.
    /// </summary>
    public bool Complete(ConfirmationRequest request, bool approved, AllowanceScope scope)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (approved && scope != AllowanceScope.Once && request.CanAllowAhead &&
            (scope == AllowanceScope.Turn || request.CanAllowForChat) &&
            request.SessionId is { } sessionId)
        {
            Allow(sessionId, request.Info.ToolName, scope);
        }

        List<ConfirmationRequest> alsoAnswered = [];
        lock (_gate)
        {
            _queue.Remove(request);
            if (approved && scope != AllowanceScope.Once)
            {
                alsoAnswered = _queue
                    .Where(item => item.CanAllowAhead &&
                                   string.Equals(item.SessionId, request.SessionId, StringComparison.Ordinal) &&
                                   item.Info.ToolName.Equals(request.Info.ToolName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                _queue.RemoveAll(alsoAnswered.Contains);
            }
        }

        var source = scope switch
        {
            AllowanceScope.Turn => ApprovalSource.AllowTurn,
            AllowanceScope.Chat => ApprovalSource.AllowChat,
            _ => request.Info.AlwaysAsk ? ApprovalSource.SynGuardHuman : ApprovalSource.Human
        };

        var answered = request.Completion.TrySetResult(new ConfirmationAnswer(approved, source));
        foreach (var item in alsoAnswered)
        {
            item.Completion.TrySetResult(new ConfirmationAnswer(true, source));
        }

        Changed?.Invoke();
        return answered;
    }

    public void CompleteCurrent(bool approved)
    {
        while (true)
        {
            ConfirmationRequest? head;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    break;
                }

                head = _queue[0];
                _queue.RemoveAt(0);
            }

            var source = head.Info.AlwaysAsk ? ApprovalSource.SynGuardHuman : ApprovalSource.Human;
            if (head.Completion.TrySetResult(new ConfirmationAnswer(approved, source)))
            {
                break;
            }
        }

        Changed?.Invoke();
    }

    public void CancelAll()
    {
        ConfirmationRequest[] dropped;
        lock (_gate)
        {
            dropped = [.. _queue];
            _queue.Clear();
        }

        foreach (var request in dropped)
        {
            request.Completion.TrySetCanceled();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Отменяет вопросы одного чата. Порядок остальных сохраняется: соседний ход продолжает
    /// ждать своей очереди, а не начинает её заново.
    /// </summary>
    public void CancelForSession(string sessionId)
    {
        ConfirmationRequest[] dropped;
        lock (_gate)
        {
            dropped = [.. _queue.Where(item =>
                string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))];
            _queue.RemoveAll(item =>
                string.Equals(item.SessionId, sessionId, StringComparison.Ordinal));
        }

        if (dropped.Length == 0)
        {
            return;
        }

        foreach (var request in dropped)
        {
            request.Completion.TrySetCanceled();
        }

        Changed?.Invoke();
    }

    /// <summary>Запоминает разрешение инструмента в чате: до конца хода или до конца разговора.</summary>
    public void Allow(string sessionId, string toolName, AllowanceScope scope)
    {
        if (scope == AllowanceScope.Once || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(toolName))
        {
            return;
        }

        // PowerShell на весь чат не разрешается — только до конца хода (см. CanAllowForChat).
        if (scope == AllowanceScope.Chat && toolName.Trim().Equals("run_powershell", StringComparison.OrdinalIgnoreCase))
        {
            scope = AllowanceScope.Turn;
        }

        lock (_gate)
        {
            if (!_allowances.TryGetValue(sessionId, out var set))
            {
                set = new Allowances();
                _allowances[sessionId] = set;
            }

            (scope == AllowanceScope.Turn ? set.Turn : set.Chat).Add(toolName.Trim());
        }
    }

    /// <summary>Ход чата закончился: разрешения «до конца хода» снимаются.</summary>
    public void EndTurn(string sessionId)
    {
        lock (_gate)
        {
            if (_allowances.TryGetValue(sessionId, out var set))
            {
                set.Turn.Clear();
            }
        }
    }

    /// <summary>Чат удалён или закрыт навсегда: все его разрешения снимаются.</summary>
    public void ForgetSession(string sessionId)
    {
        lock (_gate)
        {
            _allowances.Remove(sessionId);
        }
    }

    /// <summary>Какое разрешение впрок действует для инструмента в чате; null — никакое.</summary>
    public ApprovalSource? AllowedAhead(string? sessionId, string toolName)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_allowances.TryGetValue(sessionId, out var set))
            {
                return null;
            }

            var tool = toolName.Trim();
            return set.Chat.Contains(tool) ? ApprovalSource.AllowChat
                : set.Turn.Contains(tool) ? ApprovalSource.AllowTurn
                : null;
        }
    }

    private void TryDrop(ConfirmationRequest request)
    {
        var removed = false;
        lock (_gate)
        {
            if (_queue.Count > 0 && ReferenceEquals(_queue[0], request))
            {
                _queue.RemoveAt(0);
                removed = true;
            }
        }

        if (removed)
        {
            Changed?.Invoke();
        }
    }
}
