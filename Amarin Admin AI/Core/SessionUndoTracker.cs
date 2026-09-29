using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>
/// Следит за тем, чтобы перед изменением в запросе агента в снимке системы было всё, что оно
/// тронет.
/// </summary>
/// <remarks>
/// <para>
/// Снимок снимается один раз на запрос, а не на каждый вызов инструмента: три правки реестра
/// подряд иначе дали бы три снимка служб и задач, каждый по несколько секунд.
/// </para>
/// <para>
/// Но дописывается снимок перед каждым изменением: раздел реестра, который сейчас поменяют,
/// копия задачи, которую удалят, имя службы, которую остановят. Раньше вторая правка реестра
/// за запрос — в другой раздел — в снимок не попадала, и откатывать её было нечем.
/// </para>
/// </remarks>
public sealed class SessionUndoTracker
{
    private string? _activeSnapshotId;
    private bool _activeHadMutations;
    private string? _activeUserRequest;

    private string? _undoSnapshotId;

    /// <summary>Как снимается состояние системы. Подменяется в тестах: живой снимок читает службы и реестр.</summary>
    internal Func<string, SnapshotResult> TakeSnapshot { get; init; } =
        label => ChangeRollbackOperations.CreateSnapshot(label);

    /// <summary>Дописывает в снимок то, что тронет вызов. Подменяется в тестах.</summary>
    internal Action<string, string, JsonElement> ExtendSnapshot { get; init; } =
        ChangeRollbackOperations.ExtendBeforeMutation;

    /// <summary>Запоминает сделанное вызовом (созданные задачи). Подменяется в тестах.</summary>
    internal Action<string, string, JsonElement> RecordInSnapshot { get; init; } =
        ChangeRollbackOperations.RecordAfterMutation;

    /// <summary>Был ли за сессию запрос, который что-то изменил и успел снять снимок.</summary>
    public bool HasUndoPoint => !string.IsNullOrWhiteSpace(_undoSnapshotId);

    public void BeginRequest(string userRequest)
    {
        _activeSnapshotId = null;
        _activeHadMutations = false;
        _activeUserRequest = userRequest;
    }

    public SnapshotEnsureResult EnsureSnapshotBeforeMutation(string toolName) =>
        EnsureSnapshotBeforeMutation(toolName, default);

    public SnapshotEnsureResult EnsureSnapshotBeforeMutation(string toolName, JsonElement arguments)
    {
        SnapshotEnsureResult result;
        if (!string.IsNullOrWhiteSpace(_activeSnapshotId))
        {
            result = SnapshotEnsureResult.Existing(_activeSnapshotId);
        }
        else
        {
            var label = $"session-undo: {Truncate(_activeUserRequest ?? toolName, 80)}";
            var taken = TakeSnapshot(label);
            if (!taken.Success)
            {
                return SnapshotEnsureResult.Failed(taken.Message);
            }

            _activeSnapshotId = taken.SnapshotId;
            result = SnapshotEnsureResult.Created(taken.SnapshotId, taken.Message);
        }

        if (arguments.ValueKind == JsonValueKind.Object)
        {
            ExtendSnapshot(_activeSnapshotId!, toolName, arguments);
        }

        return result;
    }

    public void RecordMutation() => _activeHadMutations = true;

    /// <summary>Изменение состоялось: отмечает его и дописывает в снимок, что создано.</summary>
    public void RecordMutation(string toolName, JsonElement arguments)
    {
        _activeHadMutations = true;
        if (!string.IsNullOrWhiteSpace(_activeSnapshotId) && arguments.ValueKind == JsonValueKind.Object)
        {
            RecordInSnapshot(_activeSnapshotId, toolName, arguments);
        }
    }

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
