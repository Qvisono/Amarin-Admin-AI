using Amarin.Tools;

namespace Amarin.Core;

internal sealed class AgentUiAdapter : IAgentUi
{
    private readonly AgentRunRecord _record;
    private readonly ConfirmationQueue _confirmations;
    private readonly Action _changed;
    private readonly string _agentLabel;
    private readonly string? _sessionId;
    private readonly PlanReviewQueue? _plans;

    public AgentUiAdapter(
        AgentRunRecord record,
        ConfirmationQueue confirmations,
        Action changed,
        string agentLabel,
        string? sessionId = null,
        PlanReviewQueue? plans = null)
    {
        _plans = plans;
        _record = record;
        _confirmations = confirmations;
        _changed = changed;
        _agentLabel = agentLabel;
        _sessionId = sessionId;
    }

    public void Warn(string message) => AppendInfo(message);

    public void Error(string message) => AppendInfo(message);

    public void Info(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (message.StartsWith(EngineLines.ToolsDone, StringComparison.Ordinal))
        {
            var round = CurrentRound();
            if (round is not null)
            {
                round.InfoLine = message;
                Notify();
            }

            return;
        }

        AppendInfo(message);
    }

    public void UserNote(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var round = CurrentRound();
        if (round is null)
        {
            round = new ToolRound();
            _record.ToolRounds.Add(round);
        }

        round.FollowUpNote = Loc.Get("S.Tools.AgentHeard") + " " + text.Trim();
        Notify();
    }

    public void AssistantMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // ReportText остаётся: это итог агента, который уходит вызывающему результатом
        // инструмента (его путь сторожит тест). Но каждый следующий раунд его перезаписывает, и
        // он не рисуется, поэтому тот же текст закрепляется и на своём раунде — эту копию и
        // показывает раскрывашка.
        _record.ReportText = text;

        var round = CurrentRound();
        if (round is null || RoundIsSettled(round))
        {
            round = new ToolRound();
            _record.ToolRounds.Add(round);
        }

        round.ModelNote = ThinkingNote.Shorten(text);
        Notify();
    }

    public void GuardChecked(SynGuardOutcome outcome)
    {
        // Проверка идёт до первого вызова раунда, а раунд в записи агента заводит ToolCall.
        // Поэтому раунд заводится здесь же — тот, в который лягут вызовы этой проверки.
        var round = CurrentRound();
        if (round is null || RoundIsSettled(round))
        {
            round = new ToolRound { InfoLine = EngineLines.RunningTools };
            _record.ToolRounds.Add(round);
        }

        round.GuardOutcome = outcome;
        Notify();
    }

    public void ToolCall(string name, string argumentsJson)
    {
        var round = CurrentRound();
        if (round is null || RoundIsSettled(round))
        {
            round = new ToolRound { InfoLine = EngineLines.RunningTools };
            _record.ToolRounds.Add(round);
        }

        round.Calls.Add(new ToolCallRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            ArgumentsJson = argumentsJson,
            Status = ToolCallStatus.Running,
            StartedAt = DateTime.Now
        });
        Notify();
    }

    public void ToolResult(string name, ToolResult result)
    {
        var call = _record.ToolRounds
            .SelectMany(round => round.Calls)
            .FirstOrDefault(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                item.Status is ToolCallStatus.Pending or ToolCallStatus.Running);

        if (call is null)
        {
            ToolCall(name, "{}");
            call = _record.ToolRounds.SelectMany(round => round.Calls).Last();
        }

        call.Success = result.Success;
        call.Status = result.Success ? ToolCallStatus.Done : ToolCallStatus.Failed;
        call.ResultPreview = ChatToolPreview.Summarize(result);
        call.ResultText = ChatToolPreview.ForJournal(result);
        call.TruncatedForModel = ChatToolPreview.IsTruncatedForApi(result);
        call.SavedFiles = [.. result.GetFiles()];

        // Меряется по записи, а не секундомером: агент сообщает итог с того потока, где закончил,
        // и области, переживающей вызов, здесь нет.
        if (call.StartedAt != default)
        {
            call.Duration = DateTime.Now - call.StartedAt;
        }

        Notify();
    }

    public Task<T> RunBusyAsync<T>(
        string message,
        Func<Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return work();
    }

    public Task<bool> ConfirmDangerousActionAsync(
        DangerousActionInfo info,
        CancellationToken cancellationToken = default) =>
        _confirmations.ConfirmAsync(_agentLabel, info, _sessionId, cancellationToken);

    /// <summary>
    /// План — в запись хода (лента покажет его под блоком агента) и в очередь на рассмотрение.
    /// </summary>
    public async Task<PlanDecision> ReviewPlanAsync(AgentPlan plan, CancellationToken cancellationToken = default)
    {
        _record.Plan = plan;
        _record.PlanVerdict = null;
        Notify();
        if (_plans is null)
        {
            _record.PlanVerdict = PlanVerdict.Execute;
            Notify();
            return PlanDecision.Execute;
        }

        var decision = await _plans.ReviewAsync(_agentLabel, plan, _sessionId, cancellationToken).ConfigureAwait(false);
        _record.PlanVerdict = decision.Verdict;
        Notify();
        return decision;
    }

    public Task<ConfirmationAnswer> ConfirmDetailedAsync(
        DangerousActionInfo info,
        CancellationToken cancellationToken = default) =>
        _confirmations.ConfirmDetailedAsync(_agentLabel, info, _sessionId, cancellationToken);

    private void AppendInfo(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var round = CurrentRound() ?? new ToolRound();
        if (CurrentRound() is null)
        {
            _record.ToolRounds.Add(round);
        }

        if (string.IsNullOrWhiteSpace(round.InfoLine) ||
            round.InfoLine.Equals(EngineLines.RunningTools, StringComparison.Ordinal))
        {
            round.InfoLine = message;
        }

        Notify();
    }

    private ToolRound? CurrentRound() => _record.ToolRounds.Count == 0 ? null : _record.ToolRounds[^1];

    private static bool RoundIsSettled(ToolRound round) =>
        round.Calls.Count > 0 &&
        round.Calls.All(call => call.Status is ToolCallStatus.Done or ToolCallStatus.Failed) &&
        !string.IsNullOrWhiteSpace(round.InfoLine) &&
        !round.InfoLine.Equals(EngineLines.RunningTools, StringComparison.Ordinal);

    private void Notify() => _changed();
}
