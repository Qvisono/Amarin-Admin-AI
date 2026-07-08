namespace Amarin.Core;

public sealed record SessionReportTurn(
    int TurnId,
    DateTime StartedAt,
    string UserRequest,
    string? AssistantResponse,
    string? UndoSnapshotId,
    string? RequestCost);

public sealed class SessionReportCollector
{
    private readonly List<SessionReportTurn> _turns = [];

    public IReadOnlyList<SessionReportTurn> Turns => _turns;

    public void BeginTurn(int turnId, string userRequest)
    {
        _turns.Add(new SessionReportTurn(
            turnId,
            DateTime.Now,
            userRequest,
            null,
            null,
            null));
    }

    public void CompleteTurn(int turnId, string? assistantResponse, VeniceCost? cost, string? undoSnapshotId)
    {
        var index = _turns.FindIndex(t => t.TurnId == turnId);
        if (index < 0)
        {
            return;
        }

        var turn = _turns[index];
        _turns[index] = turn with
        {
            AssistantResponse = assistantResponse,
            UndoSnapshotId = undoSnapshotId,
            RequestCost = cost is { HasData: true } ? cost.Format() : null
        };
    }

    public void Clear() => _turns.Clear();
}