using Amarin.Tools;

namespace Amarin.Core;

internal sealed class ConfirmationRequest
{
    public required string AgentLabel { get; init; }

    public required DangerousActionInfo Info { get; init; }

    public required TaskCompletionSource<bool> Completion { get; init; }

    /// <summary>
    /// Чат, из которого пришёл вопрос. Нужен, чтобы отмена одного хода не убивала подтверждения
    /// соседнего и чтобы в окне подтверждения было видно, о каком разговоре речь.
    /// </summary>
    public string? SessionId { get; init; }
}

/// <summary>
/// Очередь вопросов «выполнить опасное действие?» — по одному на экране, остальные ждут.
/// </summary>
/// <remarks>
/// Список под замком, а не <c>ConcurrentQueue</c>: с несколькими одновременными ходами нужно
/// уметь выбросить из середины вопросы одного чата, не трогая порядок остальных, а из
/// <c>ConcurrentQueue</c> элемент из середины не достать.
/// </remarks>
internal sealed class ConfirmationQueue
{
    private readonly List<ConfirmationRequest> _queue = [];
    private readonly Lock _gate = new();
    private readonly Func<AppSettings> _settings;

    public ConfirmationQueue(Func<AppSettings> settings) => _settings = settings;

    public event Action? Changed;

    public async Task<bool> ConfirmAsync(
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
            return true;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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

    public bool TryPeek(out ConfirmationRequest request)
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

        request = null!;
        return false;
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

            if (head.Completion.TrySetResult(approved))
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
