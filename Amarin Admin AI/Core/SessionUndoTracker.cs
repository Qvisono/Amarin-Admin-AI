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

        // Always clear undo point once attempted — partial failures must not leave a crashing loop.
        _undoSnapshotId = null;
        _undoUserRequest = null;
        _undoCreatedAt = null;

        try
        {
            ToolResult restore;
            try
            {
                restore = ChangeRollbackOperations.RestoreSnapshot(snapshotId);
            }
            catch (Exception ex)
            {
                return UndoResult.Fail($"Ошибка восстановления снимка {snapshotId}: {ex.Message}");
            }

            string? compareText = null;
            try
            {
                var compare = ChangeRollbackOperations.CompareSnapshot(snapshotId);
                compareText = compare.Success
                    ? compare.Output
                    : $"Сравнение снимка не удалось: {compare.Output}";
            }
            catch (Exception ex)
            {
                // Compare must never crash the process — restore may already have succeeded.
                compareText = $"Сравнение снимка не удалось: {ex.Message}";
            }

            return restore.Success
                ? UndoResult.Ok(restore.Output, compareText)
                : UndoResult.Fail(restore.Output);
        }
        catch (Exception ex)
        {
            return UndoResult.Fail($"Откат прерван: {ex.Message}");
        }
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
