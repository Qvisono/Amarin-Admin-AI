using System.Collections.Concurrent;
using Amarin.Tools;

namespace Amarin.Core;

internal sealed class ConfirmationRequest
{
    public required string AgentLabel { get; init; }

    public required DangerousActionInfo Info { get; init; }

    public required TaskCompletionSource<bool> Completion { get; init; }
}

internal sealed class ConfirmationQueue
{
    private readonly ConcurrentQueue<ConfirmationRequest> _queue = new();
    private readonly Func<AppSettings> _settings;

    public ConfirmationQueue(Func<AppSettings> settings) => _settings = settings;

    public event Action? Changed;

    public async Task<bool> ConfirmAsync(
        string agentLabel,
        DangerousActionInfo info,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_settings().ApprovalMode == ApprovalMode.AlwaysApprove)
        {
            return true;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new ConfirmationRequest
        {
            AgentLabel = agentLabel,
            Info = info,
            Completion = tcs
        };
        _queue.Enqueue(request);
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
        while (_queue.TryPeek(out request!))
        {
            if (!request.Completion.Task.IsCompleted)
            {
                return true;
            }

            _queue.TryDequeue(out _);
        }

        request = null!;
        return false;
    }

    public void CompleteCurrent(bool approved)
    {
        while (_queue.TryDequeue(out var request))
        {
            if (request.Completion.TrySetResult(approved))
            {
                Changed?.Invoke();
                return;
            }
        }

        Changed?.Invoke();
    }

    public void CancelAll()
    {
        while (_queue.TryDequeue(out var request))
        {
            request.Completion.TrySetCanceled();
        }

        Changed?.Invoke();
    }

    private void TryDrop(ConfirmationRequest request)
    {
        if (_queue.TryPeek(out var head) && ReferenceEquals(head, request))
        {
            _queue.TryDequeue(out _);
            Changed?.Invoke();
        }
    }
}
