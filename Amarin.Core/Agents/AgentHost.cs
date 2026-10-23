using System.Diagnostics.CodeAnalysis;
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

    /// <summary>Библиотека инструкций: агент видит из неё только открытые ему (C6).</summary>
    private readonly InstructionLibrary? _instructions;

    /// <summary>Очередь планов на рассмотрение (C1); null — плана нет, некому его показать.</summary>
    private readonly PlanReviewQueue? _plans;

    /// <summary>Инструменты сверх своих — серверов MCP (C11). Берутся заново на каждый прогон.</summary>
    private readonly Func<IReadOnlyList<ITool>>? _extraTools;

    /// <summary>
    /// Экран и ввод этого ПК (1.36.0). Null — управлять компьютером нечем, и <c>computer</c>
    /// агенту не даётся, даже если человек включил галочку.
    /// </summary>
    private readonly Func<ComputerAccess?>? _computer;

    /// <summary>Встроенный браузер агента (1.37.0). Null — его нет, и <c>browser</c> агент не получает.</summary>
    private readonly BrowserHub? _browser;

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
        Func<string, VeniceModelInfo?>? resolveModelInfo = null,
        InstructionLibrary? instructions = null,
        PlanReviewQueue? plans = null,
        Func<IReadOnlyList<ITool>>? extraTools = null,
        Func<ComputerAccess?>? computer = null,
        BrowserHub? browser = null)
    {
        _extraTools = extraTools;
        _computer = computer;
        _browser = browser;
        _instructions = instructions;
        _plans = plans;
        _parentOptions = parentOptions;
        _downloadHttp = downloadHttp;
        _settings = settings;
        _confirmations = confirmations;
        _agents = agents;
        _resolveModelInfo = resolveModelInfo;
    }

    /// <summary>
    /// Одна попытка агента: чем она открывается (задание; у продолжения — сообщение чата, у
    /// подъёма после отказа — пусто), модель, отмена этой попытки. Подменяется только в тестах —
    /// живая уходит в сеть, а проверять надо то, что вокруг неё: остановку и пересадку.
    /// </summary>
    internal Func<string, string, CancellationToken, Task<AgentRunResult>>? Attempt { get; set; }

    /// <summary>
    /// Выбор уровня по заданию и пометкам. Подменяется только в тестах — по той же причине, что и
    /// <see cref="Attempt"/>: живая уходит в сеть, а проверять надо, кого в итоге запустили.
    /// </summary>
    internal Func<string, string?, CancellationToken, Task<AgentTierDecision>>? Route { get; set; }

    /// <summary>
    /// Клиент HTTP для агента. Подменяется только в тестах — чтобы пройти прогон с мышью целиком,
    /// ответами модели по списку и без сети.
    /// </summary>
    internal Func<TimeSpan, HttpClient>? Http { get; set; }

    private HttpClient CreateHttp(TimeSpan timeout) => Http?.Invoke(timeout) ?? HttpClients.Create(timeout);

    public Task<ToolResult> RunAsync(string prompt, string? notes, CancellationToken cancellationToken) =>
        RunCoreAsync((prompt ?? "").Trim(), notes, conversation: null, history: null, message: null, tier: null, cancellationToken);

    /// <summary>
    /// Сообщение чата агенту этого ответа (<c>message_agent</c>): агент продолжает свою переписку
    /// на той же модели и отвечает новым отчётом.
    /// </summary>
    public async Task<ToolResult> ContinueAsync(string agentId, string message, CancellationToken cancellationToken)
    {
        if (!TryEnter(agentId, out var conversation, out var refusal))
        {
            return refusal;
        }

        try
        {
            return await RunCoreAsync(
                    conversation.Prompt, null, conversation, conversation.History, message.Trim(), conversation.Tier, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            conversation.Exit();
        }
    }

    /// <summary>
    /// Задачу отказавшего агента берёт модель сильнее (<c>agent_refused</c>): лёгкая и быстрая →
    /// тяжёлая → обход. Все отказы агента из его истории убираются, новая модель продолжает с того же
    /// места (<see cref="AgentConversation.WithoutRefusals"/>).
    /// </summary>
    public async Task<ToolResult> EscalateAsync(string agentId, CancellationToken cancellationToken)
    {
        if (!TryEnter(agentId, out var conversation, out var refusal))
        {
            return refusal;
        }

        try
        {
            if (NextTier(conversation.Tier, _settings()) is not { } tier)
            {
                return ToolResult.Fail(
                    $"Агент {conversation.Id} уже работает на самой сильной модели (модель обхода): выше подниматься некуда. " +
                    "Это окончательный отказ: передай человеку причины агента.");
            }

            return await RunCoreAsync(
                    conversation.Prompt, null, conversation, conversation.WithoutRefusals(), message: null, tier, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            conversation.Exit();
        }
    }

    /// <summary>
    /// Агент этого чата, свободный для нового сообщения. Не нашёлся или занят — отказ, который
    /// читает модель чата: с тем, что делать дальше.
    /// </summary>
    private bool TryEnter(
        string agentId,
        [NotNullWhen(true)] out AgentConversation? conversation,
        [NotNullWhen(false)] out ToolResult? refusal)
    {
        var id = (agentId ?? "").Trim();
        conversation = AgentRunScope.Current?.Conversations?.Find(id);
        if (conversation is null)
        {
            // Срок разговора задаёт человек (1.37.0): модель должна понять, почему агента нет, и
            // не звать его снова.
            var why = _settings().AgentMemory switch
            {
                AgentMemory.Answer =>
                    $"Агента {id} в этом ответе нет: агент доступен только до конца ответа, в котором его запустили. ",
                AgentMemory.Chat =>
                    $"Агента {id} в этом чате нет: его запустили в другом чате, или он старше " +
                    $"{AgentMemoryBook.Limit} последних разговоров с агентами, которые хранит чат. ",
                _ =>
                    $"Агента {id} в этом чате нет: его запустили в другом чате, или разговор с ним уже недоступен " +
                    "(программу перезапускали, а разговоры с агентами не сохраняются). "
            };
            refusal = ToolResult.Fail(why + "Запусти нового через init_agent и передай ему всё, что нужно.");
            return false;
        }

        if (!conversation.TryEnter())
        {
            conversation = null;
            refusal = ToolResult.Fail(
                $"Агент {id} сейчас отвечает на другое сообщение этого же шага. Дождись его ответа и тогда пиши ему снова.");
            return false;
        }

        refusal = null;
        return true;
    }

    /// <summary>
    /// Ступень выше после отказа агента: быстрая и лёгкая → тяжёлая → обход. Ступень с той же
    /// моделью пропускается — та же модель откажет так же. <c>null</c> — выше некуда.
    /// </summary>
    internal static string? NextTier(string tier, AppSettings settings)
    {
        var current = ResolveModel(tier, settings);
        string[] above = tier.ToLowerInvariant() switch
        {
            BypassTier => [],
            "heavy" => [BypassTier],
            _ => ["heavy", BypassTier]
        };

        return above.FirstOrDefault(next =>
            !ResolveModel(next, settings).Equals(current, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Уровень модели обхода. Маршрутизатор уровней его не знает (<see cref="Tiers"/>): туда
    /// агента поднимает только отказ.
    /// </summary>
    internal const string BypassTier = "bypass";

    /// <param name="conversation">Агент этого ответа, которого продолжают; null — свежий прогон.</param>
    /// <param name="history">С чего продолжать; null — свежий прогон.</param>
    /// <param name="message">Сообщение чата агенту; null — продолжить без новой реплики.</param>
    /// <param name="tier">Уровень продолжения; null — его выбирают человек командой или маршрутизатор.</param>
    private async Task<ToolResult> RunCoreAsync(
        string prompt,
        string? notes,
        AgentConversation? conversation,
        IReadOnlyList<ChatMessage>? history,
        string? message,
        string? tier,
        CancellationToken cancellationToken)
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
        if (!string.IsNullOrWhiteSpace(tier))
        {
            // Продолжение: уровень агента уже известен, а после отказа его назначил подъём.
            currentComplexity = tier;
        }
        else if (!string.IsNullOrWhiteSpace(scope?.ForcedTier))
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

        // Один на весь прогон, а не на раунд, и свой, а не общий с агентом: VeniceClient ведёт
        // счёт потраченного у себя, и на общем клиенте деньги защитника слились бы с ценой
        // самого агента — то есть попали бы в итог дважды.
        using var guardLease = SynGuardLease.Build(settings, _parentOptions, _resolveModelInfo);
        var guard = guardLease?.Check;

        // Кого останавливает «Стоп» на плашке: текущую попытку этого прогона, а не весь ход чата, —
        // тем же путём, что остановка из чата.
        RunningAgent? entry = null;
        void Stop() => entry?.Request(new AgentInterrupt(AgentInterruptKind.Stop));

        // Мышь — только по галочке человека, только если есть чем управлять и только на этом ПК:
        // у удалённой цели окна чужие. Решается раз на прогон: выключенная посреди работы галочка
        // закрывает следующий прогон, а не рвёт этот. Сеанс один на весь прогон — пересадка на
        // другую модель не возвращает окно программы и не повторяет отсчёт.
        var computer = settings.ComputerControl && ExecutionTarget.Current is null ? _computer?.Invoke() : null;
        using var session = computer is null
            ? null
            : new ComputerSession(
                computer.Port,
                computer.Stage,
                new ComputerStart(SpendScope.Current?.Unattended == true, currentPrompt, Stop, scope?.SessionId),
                computer.Lease);

        // Браузер агента (1.37.0) — по тому же правилу, что мышь: по галочке человека и только на этом
        // ПК. Окно открывается первым действием и закрывается с концом прогона.
        using var browser = settings.AgentBrowser && ExecutionTarget.Current is null
            ? _browser?.Open(SpendScope.Current?.Unattended == true)
            : null;

        // Id агента для чата: у продолжения прежний, у свежего — первой записи в реестре, и с ним же
        // записываются все попытки, чтобы «стоп» человека и сообщение чата говорили об одном агенте.
        var agentId = conversation?.Id;

        while (true)
        {
            var modelId = ResolveModel(currentComplexity, settings);
            var options = CloneOptions(
                _parentOptions,
                modelId,
                ReasoningFor(currentComplexity, settings),
                ModelSlots.ReadKey(settings, SlotFor(currentComplexity)));
            // Работа в окнах и на сайтах идёт мелкими шагами: раундов нужно больше обычного.
            if (session is not null || browser is not null)
            {
                options = options with { MaxToolRounds = Math.Max(options.MaxToolRounds, ComputerPrompt.MaxRounds) };
            }

            record.ModelId = modelId;
            record.DisplayName = Loc.Format("S.Agent.Label", modelId);
            record.Status = AgentRunStatus.Running;
            notify();

            // Свой клиент на прогон: VeniceClient копит потраченное в своём RequestCost, и
            // агент читает оттуда цену именно своей работы. На общем с чатом клиенте он забрал
            // бы себе и деньги чужого хода.
            using var http = CreateHttp(TimeSpan.FromMinutes(5));
            var venice = new VeniceClient(http, options);
            var label = Loc.Format("S.Agent.Label", VeniceModelCatalog.GetDisplayName(modelId));
            var adapter = new AgentUiAdapter(record, _confirmations, notify, label, scope?.SessionId, _plans);

            // Снимок инструкций — один на прогон: по нему собираются и оглавление, и инструмент,
            // и правка на странице посреди работы не расщепит их надвое.
            var agentInstructions = _instructions?.AgentSnapshot() ?? [];
            var tools = AgentTools.Create(
                venice,
                _downloadHttp,
                options.Download,
                () => ModelSlots.WebSearch(_settings()),
                () => (options.Keys?.Keys.Select(key => (string?)key.Secret) ?? []).Append(options.ApiKey),
                _instructions,
                agentInstructions);
            if (session is not null)
            {
                // Инструмент — на попытку: видит ли модель снимки, зависит от модели попытки.
                tools = new ToolRegistry([.. tools.All, new ComputerTool(session, CanSee(modelId))]);
            }

            if (browser is not null)
            {
                tools = new ToolRegistry([.. tools.All, new BrowserTool(browser, CanSee(modelId))]);
            }

            if (_extraTools?.Invoke() is { Count: > 0 } extra)
            {
                // Два сервера с одинаковым именем дали бы одинаковые имена инструментов, а реестр
                // на повторе падает — оставляем первый.
                tools = new ToolRegistry([.. tools.All, .. extra.DistinctBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)]);
            }

            // Промпт только агента; TechAiPrompt чата сюда не передаётся никогда.
            var techAgent = settings.TechAgentPrompt;
            var agent = new Agent(
                venice,
                tools,
                options,
                adapter,
                new SessionUndoTracker(),
                string.IsNullOrWhiteSpace(techAgent) ? null : techAgent)
            {
                // Переписка сохраняется после отчёта: чат может продолжить разговор с агентом
                // (message_agent) или отдать его задачу модели сильнее (agent_refused).
                SessionMode = SessionMode.Continuous,
                Instructions = agentInstructions,
                // План — только если есть кому его показать и человек включил его для уровня.
                PlanFirst = _plans is not null && AgentPlanSettings.IsOn(settings, currentComplexity) &&
                            AgentRunScope.Current?.PlanAllowed != false,
                Guard = guard,
                Settings = _settings,
                AuditOrigin = new AuditOrigin(scope?.SessionId, scope?.ChatTitle, label),
                Addendum = string.Join("\n\n", new[]
                {
                    session is null ? null : ComputerPrompt.AgentRule,
                    browser is null ? null : BrowserPrompt.AgentRule
                }.OfType<string>()),
                GuardScreen = session is null ? null : session.DescribeForGuard,
                GuardPage = browser is null ? null : browser.DescribeForGuard,
                Prepare = session is null && browser is null ? null : static messages => ComputerHistory.Trim(messages)
            };

            // Своя отмена поверх отмены хода: прервать агента можно, не трогая сам ход.
            using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Новому агенту — номер, свободный в этом чате: разговоры прошлых ответов и из файла
            // чата (1.37.0) держат свои номера, а счётчик реестра на каждом запуске идёт с a1.
            if (agentId is null && scope?.Conversations is { } known)
            {
                agentId = known.Allocate(_agents is null ? known.NextId : _agents.NextId);
            }

            entry = _agents?.Register(
                scope?.SessionId ?? "", currentPrompt, currentComplexity, modelId, interrupt, agentId);
            agentId ??= entry?.Id;
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
                var attempt = Attempt ?? ((_, _, token) => history is null
                    ? agent.RunAsync(currentPrompt, token)
                    : agent.ContinueAsync(currentPrompt, history, message, token));
                var result = await attempt(history is null ? currentPrompt : message ?? "", modelId, interrupt.Token)
                    .ConfigureAwait(false);
                record.ReportText = result.Refusal is { } refused
                    ? Refusals.FinalText(modelId, refused, result.AssistantText)
                    : result.AssistantText ?? "";
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

                Keep(scope, conversation, agentId, currentPrompt, currentComplexity, agent.SessionHistory);
                return ToolResult.Ok(
                    (switched ? "Агент был пересажен на другую модель по просьбе пользователя. " : "") +
                    ReportFor(agentId, result, modelId, currentComplexity, settings));
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
                    _agents?.Unregister(entry);
                }
            }
        }
    }

    /// <summary>
    /// Запоминает агента, чтобы чат мог ему написать (как долго — <see cref="AppSettings.AgentMemory"/>).
    /// Без хода чата (расписание) запоминать некуда — и незачем.
    /// </summary>
    private static void Keep(
        AgentRunContext? scope,
        AgentConversation? conversation,
        string? agentId,
        string prompt,
        string tier,
        IReadOnlyList<ChatMessage> history)
    {
        if (scope?.Conversations is not { } conversations || agentId is null)
        {
            return;
        }

        if (conversation is null)
        {
            conversations.Keep(new AgentConversation { Id = agentId, Prompt = prompt, Tier = tier, History = history });
            return;
        }

        conversations.Update(conversation, tier, history);
    }

    /// <summary>
    /// Что чат получает в ответ: отчёт агента с его id — или отказ с тем, что можно сделать дальше.
    /// </summary>
    private static string ReportFor(string? agentId, AgentRunResult result, string modelId, string tier, AppSettings settings)
    {
        var who = agentId is null ? "Агент" : "Агент " + agentId;
        if (result.Refusal is { } reasons)
        {
            var why = !string.IsNullOrWhiteSpace(reasons)
                ? reasons.Trim()
                : string.IsNullOrWhiteSpace(result.AssistantText) ? "не названы" : result.AssistantText.Trim();
            var model = VeniceModelCatalog.GetDisplayName(modelId);
            return NextTier(tier, settings) is null
                ? $"{who} ({model}) отказался, и модели сильнее нет. Причины: {why}. " +
                  "Переубедить его можно через message_agent; не выйдет — передай человеку его причины."
                : $"{who} ({model}) отказался. Причины: {why}. " +
                  "Переубеди его через message_agent или передай задачу модели сильнее через agent_refused.";
        }

        var report = string.IsNullOrWhiteSpace(result.AssistantText)
            ? "Агент завершил работу без текстового отчёта."
            : result.AssistantText;
        return (agentId is null ? "Отчёт агента: " : $"Отчёт агента {agentId}: ") + report;
    }

    /// <summary>
    /// Видит ли модель картинки — снимок экрана отдаётся только такой. Модель, о которой каталог
    /// ещё ничего не знает, снимки получает: ложный отказ модели со зрением хуже.
    /// </summary>
    private bool CanSee(string modelId) =>
        _resolveModelInfo?.Invoke(modelId) is not { } info || VeniceModelCatalog.HasVision(info);

    internal const string ForcedAgentModelId = AppSettings.FactoryAgentHeavyModelId;

    private static readonly string[] Tiers = ["fast", "lite", "heavy"];

    /// <summary>
    /// Живой выбор уровня: тот же маршрутизатор, что и у «Авто», на той же модели и с тем же
    /// размышлением, но с тремя исходами.
    /// </summary>
    /// <remarks>
    /// Клиент свой и одноразовый: у <see cref="VeniceClient"/> свой счёт потраченного, и по
    /// нему видно, во сколько обошёлся именно выбор уровня. Считанные с него деньги в ход чата
    /// не попадают — <c>VeniceTurnScope</c> здесь уже подавлен, — поэтому их забирает
    /// вызывающий и кладёт в счёт агента. Таймаут у служебного запроса тоже свой, короткий.
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
        var options = CloneOptions(
            _parentOptions,
            routerId,
            settings.RouterReasoning ?? new ReasoningSettings(),
            ModelSlots.ReadKey(settings, ModelSlot.Router));

        using var http = HttpClients.Create(HttpClients.ServiceTimeout);
        var venice = new VeniceClient(http, options) { ResolveModelInfo = _resolveModelInfo };

        var models = ModelBriefing.ForAgentRouter(
            ResolveModel("fast", settings),
            ResolveModel("lite", settings),
            ResolveModel("heavy", settings),
            _resolveModelInfo);

        // Мышь дана — работа в окнах не должна достаться модели, которая не видит экран: правило
        // в блок моделей, а уровням без зрения — запрет прямо в критерий Decisions.
        HashSet<string>? blind = null;
        if (settings.ComputerControl && _computer is not null)
        {
            models += "\n" + AgentTierRouter.WindowsRule;
            blind = [.. Tiers.Where(tier => !CanSee(ResolveModel(tier, settings)))];
        }

        return await AgentTierRouter
            .DecideAsync(venice, routerId, models, prompt, notes, reasoning, cancellationToken, blind)
            .ConfigureAwait(false);
    }

    /// <summary>Слот, из которого этот ярус берёт модель, — а вместе с ней и ключ.</summary>
    private static ModelSlot SlotFor(string complexity) =>
        complexity.ToLowerInvariant() switch
        {
            "heavy" => ModelSlot.AgentHeavy,
            "fast" => ModelSlot.AgentFast,
            BypassTier => ModelSlot.Bypass,
            _ => ModelSlot.AgentLite
        };

    /// <summary>
    /// Модель уровня. switch, а не прежний тернарный оператор: при трёх уровнях «не тяжёлый» молча
    /// значил «лёгкий», и быстрый запрос тихо стоил бы денег флагмана.
    /// </summary>
    private static string ResolveModel(string complexity, AppSettings settings)
    {
        var model = complexity.ToLowerInvariant() switch
        {
            "heavy" => settings.AgentHeavyModelId,
            "fast" => settings.AgentFastModelId,
            BypassTier => settings.BypassModelId,
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
            BypassTier => settings.BypassReasoning,
            _ => settings.AgentLiteReasoning
        };

        return slot ?? new ReasoningSettings();
    }

    /// <param name="keyId">
    /// Ключ слота, чью модель этот прогон и берёт. Ставится здесь, в миг сборки копии: клиент
    /// у прогона свой, и внутренние вызовы агента про ключи ничего не знают.
    /// </param>
    internal static AgentOptions CloneOptions(
        AgentOptions source,
        string model,
        ReasoningSettings reasoning,
        string? keyId = null) =>
        source with
        {
            // Ключ, каким он виден сейчас, а не поле за ним: так копии делались и до записи.
            ApiKey = source.ApiKey,
            Binding = source.Keys?.CredentialFor(model, keyId),
            Model = model,
            EnableXSearch = false,
            DisableThinking = reasoning.DisableThinking,
            ReasoningEffort = reasoning.ReasoningEffort,
            ModelSpeed = reasoning.Speed
        };
}
