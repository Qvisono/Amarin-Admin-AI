using Amarin.Tools;

namespace Amarin.Core;

/// <summary>
/// Следит за тем, чтобы перед первым изменением в запросе агента появился снимок системы.
/// </summary>
/// <remarks>
/// Снимок снимается один раз на запрос, а не на каждый вызов инструмента: три правки реестра
/// подряд иначе дали бы три снимка служб и задач, каждый по несколько секунд.
/// </remarks>
public sealed class SessionUndoTracker
{
    private string? _activeSnapshotId;
    private bool _activeHadMutations;
    private string? _activeUserRequest;

    private string? _undoSnapshotId;

    /// <summary>Был ли за сессию запрос, который что-то изменил и успел снять снимок.</summary>
    public bool HasUndoPoint => !string.IsNullOrWhiteSpace(_undoSnapshotId);

    public void BeginRequest(string userRequest)
    {
        _activeSnapshotId = null;
        _activeHadMutations = false;
        _activeUserRequest = userRequest;
    }

    public SnapshotEnsureResult EnsureSnapshotBeforeMutation(string toolName)
    {
        if (!string.IsNullOrWhiteSpace(_activeSnapshotId))
        {
            return SnapshotEnsureResult.Existing(_activeSnapshotId);
        }

        var label = $"session-undo: {Truncate(_activeUserRequest ?? toolName, 80)}";
        var result = ChangeRollbackOperations.CreateSnapshot(label);

        if (!result.Success)
        {
            return SnapshotEnsureResult.Failed(result.Message);
        }

        _activeSnapshotId = result.SnapshotId;
        return SnapshotEnsureResult.Created(result.SnapshotId, result.Message);
    }

    public void RecordMutation() => _activeHadMutations = true;

    public void CompleteRequest()
    {
        // Снимок засчитывается точкой отката только если изменения действительно случились:
        // инструмент мог запросить снимок и тут же отказаться от записи.
        if (_activeHadMutations && !string.IsNullOrWhiteSpace(_activeSnapshotId))
        {
            _undoSnapshotId = _activeSnapshotId;
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}

public sealed record SnapshotEnsureResult(
    bool Success,
    bool IsNew,
    string? SnapshotId,
    string Message)
{
    public static SnapshotEnsureResult Existing(string snapshotId) =>
        new(true, false, snapshotId, $"Используется снимок: {snapshotId}");

    public static SnapshotEnsureResult Created(string snapshotId, string message) =>
        new(true, true, snapshotId, message);

    public static SnapshotEnsureResult Failed(string message) =>
        new(false, false, null, message);
}
