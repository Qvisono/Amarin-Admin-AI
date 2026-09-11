using Amarin.Tools;

namespace Amarin.Core;

internal sealed class AgentHost : IAgentHost
{
    private readonly AgentOptions _parentOptions;
    private readonly HttpClient _downloadHttp;
    private readonly Func<AppSettings> _settings;
    private readonly ConfirmationQueue _confirmations;

    public AgentHost(
        AgentOptions parentOptions,
        HttpClient downloadHttp,
        Func<AppSettings> settings,
        ConfirmationQueue confirmations)
    {
        _parentOptions = parentOptions;
        _downloadHttp = downloadHttp;
        _settings = settings;
        _confirmations = confirmations;
    }

    public async Task<ToolResult> RunAsync(string prompt, string complexity, CancellationToken cancellationToken)
    {
        var settings = _settings();
        var modelId = ResolveModel(complexity, settings);
        var options = CloneOptions(_parentOptions, modelId, ReasoningFor(complexity, settings));

        var record = new AgentRunRecord
        {
            ModelId = modelId,
            DisplayName = "Агент " + modelId,
            Status = AgentRunStatus.Running
        };

        var scope = AgentRunScope.Current;
        Action notify = () => { };
        if (scope is not null)
        {
            scope.Call.NestedAgent = record;
            var assistant = scope.Assistant;
            var observer = scope.Observer;
            notify = () => observer.OnToolsChanged(assistant);
            notify();
        }

        // Own HttpClient: VeniceClient mutates BaseAddress/Authorization and cannot share
        // a client that already sent requests (the chat engine's).
        using var http = HttpClients.Create(TimeSpan.FromMinutes(5));
        var venice = new VeniceClient(http, options);

        // Счёт агента — свой, и в ход чата он уже попадает через NestedAgent.Cost. Контекст хода
        // при этом протекает сюда по AsyncLocal, поэтому его надо закрыть: иначе те же деньги
        // лягут в ход дважды.
        using var isolatedCost = VeniceTurnScope.Suppress();
        var tools = AgentTools.Create(venice, _downloadHttp, options.Download);

        var label = "Агент " + VeniceModelCatalog.GetDisplayName(modelId);
        var adapter = new AgentUiAdapter(record, _confirmations, notify, label, scope?.SessionId);
        // Agent-only prompt. Chat companion TechAiPrompt is never passed here.
        var techAgent = settings.TechAgentPrompt;
        var agent = new Agent(
            venice,
            tools,
            options,
            adapter,
            new SessionUndoTracker(),
            string.IsNullOrWhiteSpace(techAgent) ? null : techAgent)
        {
            SessionMode = SessionMode.Isolated
        };

        try
        {
            var result = await agent.RunAsync(prompt, cancellationToken).ConfigureAwait(false);
            record.ReportText = result.AssistantText ?? "";
            record.Cost = result.Cost;
            record.Status = result.Cancelled
                ? AgentRunStatus.Cancelled
                : string.IsNullOrWhiteSpace(result.Error)
                    ? AgentRunStatus.Complete
                    : AgentRunStatus.Failed;
            notify();

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                return ToolResult.Fail(result.Error);
            }

            var report = string.IsNullOrWhiteSpace(result.AssistantText)
                ? "Агент завершил работу без текстового отчёта."
                : result.AssistantText;
            return ToolResult.Ok("Отчёт агента: " + report);
        }
        catch (OperationCanceledException)
        {
            record.Status = AgentRunStatus.Cancelled;
            record.Cost = venice.RequestCost;
            notify();
            throw;
        }
    }

    internal const string ForcedAgentModelId = "grok-4-3";

    private static string ResolveModel(string complexity, AppSettings settings)
    {
        var heavy = complexity.Equals("heavy", StringComparison.OrdinalIgnoreCase);
        var model = heavy ? settings.AgentHeavyModelId : settings.AgentLiteModelId;
        return string.IsNullOrWhiteSpace(model) ? ForcedAgentModelId : model.Trim();
    }

    private static ReasoningSettings ReasoningFor(string complexity, AppSettings settings)
    {
        var heavy = complexity.Equals("heavy", StringComparison.OrdinalIgnoreCase);
        var slot = heavy ? settings.AgentHeavyReasoning : settings.AgentLiteReasoning;
        return slot ?? new ReasoningSettings();
    }

    private static AgentOptions CloneOptions(AgentOptions source, string model, ReasoningSettings reasoning) =>
        new()
        {
            ApiKey = source.ApiKey,
            BaseUrl = source.BaseUrl,
            Model = model,
            MaxToolRounds = source.MaxToolRounds,
            WebSearch = source.WebSearch,
            EnableWebCitations = source.EnableWebCitations,
            EnableXSearch = false,
            Download = source.Download,
            DisableThinking = reasoning.DisableThinking,
            ReasoningEffort = reasoning.ReasoningEffort
        };
}
