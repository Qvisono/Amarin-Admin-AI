using Amarin.Tools;

namespace Amarin.Core;

internal sealed class AgentHost : IAgentHost
{
    private readonly AgentOptions _parentOptions;
    private readonly HttpClient _downloadHttp;
    private readonly Func<AppSettings> _settings;
    private readonly ConfirmationQueue _confirmations;
    private readonly AgentRegistry? _agents;

    /// <param name="agents">
    /// Куда записываться на время работы, чтобы агента можно было остановить или пересадить на
    /// другую модель, пока он работает. Null — прежнее поведение «запустили и ждём».
    /// </param>
    public AgentHost(
        AgentOptions parentOptions,
        HttpClient downloadHttp,
        Func<AppSettings> settings,
        ConfirmationQueue confirmations,
        AgentRegistry? agents = null)
    {
        _parentOptions = parentOptions;
        _downloadHttp = downloadHttp;
        _settings = settings;
        _confirmations = confirmations;
        _agents = agents;
    }

    /// <summary>
    /// Одна попытка агента: задание, модель, отмена этой попытки. Подменяется только в тестах —
    /// живая уходит в сеть, а проверять надо то, что вокруг неё: остановку и пересадку.
    /// </summary>
    internal Func<string, string, CancellationToken, Task<AgentRunResult>>? Attempt { get; set; }

    public async Task<ToolResult> RunAsync(string prompt, string complexity, CancellationToken cancellationToken)
    {
        var settings = _settings();
        var record = new AgentRunRecord { Status = AgentRunStatus.Running };

        var scope = AgentRunScope.Current;
        Action notify = () => { };
        if (scope is not null)
        {
            scope.Call.NestedAgent = record;
            var assistant = scope.Assistant;
            var observer = scope.Observer;
            notify = () => observer.OnToolsChanged(assistant);
        }

        // Счёт агента — свой, и в ход чата он уже попадает через NestedAgent.Cost. Контекст хода
        // при этом протекает сюда по AsyncLocal, поэтому его надо закрыть: иначе те же деньги
        // лягут в ход дважды.
        using var isolatedCost = VeniceTurnScope.Suppress();

        var currentPrompt = (prompt ?? "").Trim();
        var currentComplexity = complexity;

        // Потраченное прошлыми попытками. Пересадка на другую модель не отменяет того, что уже
        // списали: показать только последнюю попытку значило бы занизить счёт.
        var spent = VeniceCost.Zero;
        var switched = false;

        // Уточнения, не дошедшие до прошлой попытки, потому что её оборвали пересадкой.
        // Потерять их значило бы промолчать в ответ на то, что человек написал.
        IReadOnlyList<string> carried = [];

        while (true)
        {
            var modelId = ResolveModel(currentComplexity, settings);
            var options = CloneOptions(_parentOptions, modelId, ReasoningFor(currentComplexity, settings));
            record.ModelId = modelId;
            record.DisplayName = "Агент " + modelId;
            record.Status = AgentRunStatus.Running;
            notify();

            // Own HttpClient: VeniceClient mutates BaseAddress/Authorization and cannot share
            // a client that already sent requests (the chat engine's).
            using var http = HttpClients.Create(TimeSpan.FromMinutes(5));
            var venice = new VeniceClient(http, options);
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

            // Своя отмена поверх отмены хода: прервать агента можно, не трогая сам ход.
            using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var entry = _agents?.Register(
                scope?.SessionId ?? "", currentPrompt, currentComplexity, modelId, interrupt);
            if (entry is not null)
            {
                foreach (var note in carried)
                {
                    entry.Tell(note);
                }

                agent.TakeNotes = entry.TakeNotes;
            }

            carried = [];

            try
            {
                var attempt = Attempt ?? ((task, _, token) => agent.RunAsync(task, token));
                var result = await attempt(currentPrompt, modelId, interrupt.Token)
                    .ConfigureAwait(false);
                record.ReportText = result.AssistantText ?? "";
                record.Cost = spent.Add(result.Cost);
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
                return ToolResult.Ok(
                    switched
                        ? "Агент был пересажен на другую модель по просьбе пользователя. Отчёт агента: " + report
                        : "Отчёт агента: " + report);
            }
            catch (OperationCanceledException) when (
                entry is not null && !cancellationToken.IsCancellationRequested)
            {
                // Не отмена хода, а просьба человека, пришедшая уже во время работы.
                spent = spent.Add(venice.RequestCost);
                var pending = entry.Pending;
                if (pending.Kind == AgentInterruptKind.Switch)
                {
                    carried = entry.TakeNotes();
                    currentComplexity = pending.Complexity;
                    if (!string.IsNullOrWhiteSpace(pending.Prompt))
                    {
                        currentPrompt = pending.Prompt.Trim();
                    }

                    switched = true;
                    continue;
                }

                record.Status = AgentRunStatus.Cancelled;
                record.Cost = spent;
                notify();
                return ToolResult.Ok(
                    "Агент остановлен по просьбе пользователя и ничего не довёл до конца. " +
                    "Не запускай его заново сам: скажи, на чём остановились, и спроси, что дальше.");
            }
            catch (OperationCanceledException)
            {
                record.Status = AgentRunStatus.Cancelled;
                record.Cost = spent.Add(venice.RequestCost);
                notify();
                throw;
            }
            finally
            {
                if (entry is not null)
                {
                    _agents!.Unregister(entry.Id);
                }
            }
        }
    }

    internal const string ForcedAgentModelId = "grok-4-3";

    /// <summary>
    /// Tier to model. A switch rather than the ternary it replaced: with three tiers, "not heavy"
    /// silently meant "lite", so a fast request would have quietly cost flagship money.
    /// </summary>
    private static string ResolveModel(string complexity, AppSettings settings)
    {
        var model = complexity.ToLowerInvariant() switch
        {
            "heavy" => settings.AgentHeavyModelId,
            "fast" => settings.AgentFastModelId,
            _ => settings.AgentLiteModelId
        };

        return string.IsNullOrWhiteSpace(model) ? ForcedAgentModelId : model.Trim();
    }

    private static ReasoningSettings ReasoningFor(string complexity, AppSettings settings)
    {
        var slot = complexity.ToLowerInvariant() switch
        {
            "heavy" => settings.AgentHeavyReasoning,
            "fast" => settings.AgentFastReasoning,
            _ => settings.AgentLiteReasoning
        };

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
