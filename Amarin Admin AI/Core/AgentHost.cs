using Amarin.Tools;

namespace Amarin.Core;

internal sealed class AgentHost : IAgentHost
{
    private readonly AgentOptions _parentOptions;
    private readonly HttpClient _downloadHttp;
    private readonly Func<AppSettings> _settings;
    private readonly ConfirmationQueue _confirmations;
    private readonly AgentRegistry? _agents;
    private readonly Func<string, VeniceModelInfo?>? _resolveModelInfo;

    /// <param name="agents">
    /// Куда записываться на время работы, чтобы агента можно было остановить или пересадить на
    /// другую модель, пока он работает. Null — прежнее поведение «запустили и ждём».
    /// </param>
    /// <param name="resolveModelInfo">
    /// Общий кэш каталога моделей: из него маршрутизатор уровня узнаёт окно и возможности тех
    /// трёх моделей, между которыми выбирает. Null — в блоке останутся одни имена, и это
    /// штатный исход до того, как каталог подтянулся.
    /// </param>
    public AgentHost(
        AgentOptions parentOptions,
        HttpClient downloadHttp,
        Func<AppSettings> settings,
        ConfirmationQueue confirmations,
        AgentRegistry? agents = null,
        Func<string, VeniceModelInfo?>? resolveModelInfo = null)
    {
        _parentOptions = parentOptions;
        _downloadHttp = downloadHttp;
        _settings = settings;
        _confirmations = confirmations;
        _agents = agents;
        _resolveModelInfo = resolveModelInfo;
    }

    /// <summary>
    /// Одна попытка агента: задание, модель, отмена этой попытки. Подменяется только в тестах —
    /// живая уходит в сеть, а проверять надо то, что вокруг неё: остановку и пересадку.
    /// </summary>
    internal Func<string, string, CancellationToken, Task<AgentRunResult>>? Attempt { get; set; }

    /// <summary>
    /// Выбор уровня по заданию и пометкам. Подменяется только в тестах — по той же причине, что и
    /// <see cref="Attempt"/>: живая уходит в сеть, а проверять надо, кого в итоге запустили.
    /// </summary>
    internal Func<string, string?, CancellationToken, Task<AgentTierDecision>>? Route { get; set; }

    public async Task<ToolResult> RunAsync(string prompt, string? notes, CancellationToken cancellationToken)
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

        // Потраченное прошлыми попытками. Пересадка на другую модель не отменяет того, что уже
        // списали: показать только последнюю попытку значило бы занизить счёт. Сюда же ложится
        // цена самого выбора уровня: маршрутизатор работает на клиенте агента, ход чата от него
        // закрыт, и без этого его деньги не попали бы в счёт вообще.
        var spent = VeniceCost.Zero;
        var switched = false;

        string currentComplexity;
        if (!string.IsNullOrWhiteSpace(scope?.ForcedTier))
        {
            // Уровень назвал человек командой. Спрашивать о нём модель — тратить его деньги на
            // уже принятое решение.
            currentComplexity = scope.ForcedTier.Trim().ToLowerInvariant();
        }
        else
        {
            var decision = await (Route ?? RouteAsync)(currentPrompt, notes, cancellationToken)
                .ConfigureAwait(false);
            currentComplexity = decision.Tier;
            spent = decision.Cost ?? VeniceCost.Zero;
        }

        // Уточнения, не дошедшие до прошлой попытки, потому что её оборвали пересадкой.
        // Потерять их значило бы промолчать в ответ на то, что человек написал.
        IReadOnlyList<string> carried = [];

        // Один на весь прогон, а не на раунд: VeniceClient правит BaseAddress и заголовки, поэтому
        // делить клиент агента нельзя — иначе деньги защитника слились бы с ценой самого агента и
        // попали бы в итог дважды.
        using var guardHttp = settings.SynGuardEnabled
            ? HttpClients.Create(HttpClients.ServiceTimeout)
            : null;
        var guard = BuildGuard(settings, guardHttp);

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
                SessionMode = SessionMode.Isolated,
                Guard = guard
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
    /// Живой выбор уровня: тот же маршрутизатор, что и у «Авто», на той же модели и с тем же
    /// размышлением, но с тремя исходами.
    /// </summary>
    /// <remarks>
    /// Клиент свой и одноразовый: <see cref="VeniceClient"/> правит BaseAddress и Authorization и
    /// не делится с тем, кто уже слал запросы. Считанные с него деньги в ход чата не попадают —
    /// <c>VeniceTurnScope</c> здесь уже подавлен, — поэтому их забирает вызывающий и кладёт в
    /// счёт агента.
    /// </remarks>
    private async Task<AgentTierDecision> RouteAsync(
        string prompt,
        string? notes,
        CancellationToken cancellationToken)
    {
        var settings = _settings();
        var routerId = string.IsNullOrWhiteSpace(settings.RouterModelId) ||
                       VeniceModelCatalog.IsAuto(settings.RouterModelId)
            ? ResolveModel("lite", settings)
            : settings.RouterModelId.Trim();

        var reasoning = (settings.RouterReasoning ?? new ReasoningSettings()).ToChoice();
        var options = CloneOptions(_parentOptions, routerId, settings.RouterReasoning ?? new ReasoningSettings());

        using var http = HttpClients.Create(HttpClients.ServiceTimeout);
        var venice = new VeniceClient(http, options) { ResolveModelInfo = _resolveModelInfo };

        var models = ModelBriefing.ForAgentRouter(
            ResolveModel("fast", settings),
            ResolveModel("lite", settings),
            ResolveModel("heavy", settings),
            _resolveModelInfo);

        return await AgentTierRouter
            .DecideAsync(venice, routerId, models, prompt, notes, reasoning, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Живая проверка SynGuard для этого прогона, либо <c>null</c>, если защита выключена.
    /// </summary>
    /// <remarks>
    /// Собирается один раз на прогон и передаётся всем его попыткам: пересадка агента на другую
    /// модель защитника не меняет.
    /// </remarks>
    private Func<IReadOnlyList<SynGuardCall>, CancellationToken, Task<SynGuardReport>>? BuildGuard(
        AppSettings settings,
        HttpClient? http)
    {
        if (!settings.SynGuardEnabled || http is null)
        {
            return null;
        }

        var modelId = SynGuard.ResolveModel(settings);
        var reasoningSlot = settings.SynGuardReasoning ?? new ReasoningSettings();
        var options = CloneOptions(_parentOptions, modelId, reasoningSlot);
        var venice = new VeniceClient(http, options) { ResolveModelInfo = _resolveModelInfo };
        var checker = new SynGuardChecker(venice, modelId, reasoningSlot.ToChoice());
        return checker.CheckAsync;
    }

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
