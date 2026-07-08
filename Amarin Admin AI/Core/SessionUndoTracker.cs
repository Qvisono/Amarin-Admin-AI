using Amarin.Tools;

namespace Amarin.Core;

public sealed class SessionUndoTracker
{
    private string? _activeSnapshotId;
    private bool _activeHadMutations;
    private string? _activeUserRequest;

    private string? _undoSnapshotId;
    private string? _undoUserRequest;
    private DateTime? _undoCreatedAt;

    public bool HasUndoPoint => !string.IsNullOrWhiteSpace(_undoSnapshotId);

    public string? UndoSnapshotId => _undoSnapshotId;

    public string? LastCompletedUndoSnapshotId { get; private set; }

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
        LastCompletedUndoSnapshotId = null;

        if (_activeHadMutations && !string.IsNullOrWhiteSpace(_activeSnapshotId))
        {
            _undoSnapshotId = _activeSnapshotId;
            _undoUserRequest = _activeUserRequest;
            _undoCreatedAt = DateTime.Now;
            LastCompletedUndoSnapshotId = _activeSnapshotId;
        }
    }

    public string DescribeUndoPoint()
    {
        if (!HasUndoPoint)
        {
            return "Нет доступной точки отката.";
        }

        return
            $"Откат последнего запроса с изменениями\n\n" +
            $"Запрос: {Truncate(_undoUserRequest ?? "—", 200)}\n" +
            $"Снимок: {_undoSnapshotId}\n" +
            $"Создан: {_undoCreatedAt:yyyy-MM-dd HH:mm:ss}\n\n" +
            "Будут восстановлены службы, задачи планировщика и ключи реестра из снимка.";
    }

    public UndoResult Undo()
    {
        if (!HasUndoPoint)
        {
            return UndoResult.Fail("Нет точки отката для последнего запроса с изменениями.");
        }

        var snapshotId = _undoSnapshotId!;
        var restore = ChangeRollbackOperations.RestoreSnapshot(snapshotId);
        var compare = ChangeRollbackOperations.CompareSnapshot(snapshotId);

        _undoSnapshotId = null;
        _undoUserRequest = null;
        _undoCreatedAt = null;

        return restore.Success
            ? UndoResult.Ok(restore.Output, compare.Output)
            : UndoResult.Fail(restore.Output);
    }

    public void Clear()
    {
        _activeSnapshotId = null;
        _activeHadMutations = false;
        _activeUserRequest = null;
        _undoSnapshotId = null;
        _undoUserRequest = null;
        _undoCreatedAt = null;
        LastCompletedUndoSnapshotId = null;
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

public sealed record UndoResult(bool Success, string RestoreOutput, string? CompareOutput)
{
    public static UndoResult Ok(string restore, string? compare) => new(true, restore, compare);
    public static UndoResult Fail(string message) => new(false, message, null);
}