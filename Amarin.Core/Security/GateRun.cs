using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>
/// Вызов инструмента без модели — рецептом или отложенной командой: шлюз, вопрос тем способом,
/// который дал вызывающий, снимок перед записью, исполнение и строка аудита.
/// </summary>
/// <remarks>
/// Один путь на двоих, а не копия: рецепт и отложенная команда обязаны проходить ровно те же
/// жёсткие запреты, режим «только чтение» и снимок, что и вызов из чата. Различаются они только
/// тем, кто отвечает на вопрос: рецепт спрашивает человека сейчас, отложенная команда отвечает
/// согласием, которое он дал, когда её ставил.
/// </remarks>
internal static class GateRun
{
    public static async Task<RecipeRunOutcome> RunAsync(
        ToolRegistry tools,
        string tool,
        JsonElement arguments,
        AppSettings settings,
        Func<DangerousActionInfo, CancellationToken, Task<ConfirmationAnswer>> ask,
        AuditOrigin origin,
        string callId,
        Func<SessionUndoTracker> undo,
        AuditLog? audit,
        CancellationToken cancellationToken)
    {
        var label = origin.Agent ?? tool;
        var check = ToolGate.Check(tool, arguments, settings);
        var decision = await ToolGate.DecideAsync(check, ask, guardApproved: false, cancellationToken).ConfigureAwait(false);

        if (!decision.Allowed)
        {
            var refused = ToolResult.Fail(decision.Refusal ?? ToolGate.DeniedReply);
            Audit(AuditOutcome.Refused, refused);
            return new RecipeRunOutcome(refused, false, decision.Approval);
        }

        SessionUndoTracker? tracker = null;
        if (decision.NeedsSnapshot)
        {
            tracker = undo();
            tracker.BeginRequest(label);
            await Task.Run(() => tracker.EnsureSnapshotBeforeMutation(check.ToolName, decision.Arguments), cancellationToken)
                .ConfigureAwait(false);
        }

        ToolResult result;
        try
        {
            result = await tools.ExecuteAsync(check.ToolName, decision.Arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Audit(AuditOutcome.Cancelled, null);
            throw;
        }

        if (tracker is not null && result.Success)
        {
            tracker.RecordMutation(check.ToolName, decision.Arguments);
            tracker.CompleteRequest();
        }

        Audit(result.Success ? AuditOutcome.Ok : AuditOutcome.Failed, result);
        return new RecipeRunOutcome(result, true, decision.Approval);

        void Audit(AuditOutcome outcome, ToolResult? output) =>
            audit?.Record(
                origin,
                callId,
                check.ToolName,
                decision?.Arguments.GetRawText() ?? arguments.GetRawText(),
                decision?.Effect ?? check.Effect,
                outcome,
                decision?.Approval ?? ApprovalSource.NotRequired,
                AuditGuard.Off,
                output?.Output);
    }
}
