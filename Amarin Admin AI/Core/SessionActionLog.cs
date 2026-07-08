namespace Amarin.Core;

public sealed record SessionActionEntry(
    int TurnId,
    DateTime Time,
    string Tool,
    string? Action,
    bool Success,
    string Summary,
    string FullOutput);

public sealed class SessionActionLog
{
    private readonly List<SessionActionEntry> _entries = [];
    private const int MaxEntries = 200;
    private int _turnCounter;
    private int _currentTurnId;

    public IReadOnlyList<SessionActionEntry> Entries => _entries;

    public int CurrentTurnId => _currentTurnId;

    public int BeginTurn()
    {
        _currentTurnId = ++_turnCounter;
        return _currentTurnId;
    }

    public void Record(string tool, string? action, bool success, string fullOutput)
    {
        var summary = fullOutput.Length > 120 ? fullOutput[..120] + "…" : fullOutput;
        _entries.Add(new SessionActionEntry(
            _currentTurnId,
            DateTime.Now,
            tool,
            action,
            success,
            summary,
            fullOutput));

        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
    }

    public IReadOnlyList<SessionActionEntry> GetEntriesForTurn(int turnId) =>
        _entries.Where(e => e.TurnId == turnId).ToList();

    public void Clear()
    {
        _entries.Clear();
        _turnCounter = 0;
        _currentTurnId = 0;
    }
}