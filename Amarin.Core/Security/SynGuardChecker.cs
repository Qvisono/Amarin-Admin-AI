namespace Amarin.Core;

/// <summary>
/// Спрашивает модель-защитника про раунд инструментов агента.
/// </summary>
/// <remarks>
/// <para>
/// Свой <see cref="VeniceClient"/>, а не клиент агента, — ради счёта. Деньги защитника должны
/// стать отдельной строкой «Guard» в разбивке трат, а на клиенте агента они слились бы с ценой
/// самого агента и попали бы в итог дважды: один раз через <c>NestedAgent.Cost</c>, второй —
/// как трата защиты.
/// </para>
/// <para>
/// Клиент один на прогон агента, а не на раунд: у <see cref="VeniceClient"/> свой счёт
/// потраченного, и делить его с агентом нельзя — иначе деньги защитника попали бы в итог
/// дважды, — но и заводить новый клиент на каждый раунд незачем.
/// </para>
/// </remarks>
internal sealed class SynGuardChecker
{
    private readonly VeniceClient _venice;
    private readonly string _modelId;
    private readonly ReasoningChoice _reasoning;
    private readonly bool _checkSafety;
    private readonly bool _checkApproval;

    public SynGuardChecker(VeniceClient venice, string modelId, ReasoningChoice reasoning,
        bool checkSafety = true, bool checkApproval = false)
    {
        _venice = venice;
        _modelId = modelId;
        _reasoning = reasoning;
        _checkSafety = checkSafety;
        _checkApproval = checkApproval;
    }

    public async Task<SynGuardReport> CheckAsync(SynGuardRequest request, CancellationToken cancellationToken)
    {
        var count = request.Calls.Count;
        if (count == 0)
        {
            return new SynGuardReport([], null);
        }

        using var charge = VeniceClient.ChargeAs(VeniceSku.Guard);
        try
        {
            if (await _venice.UsesDecisionsAsync(_modelId, cancellationToken).ConfigureAwait(false))
            {
                return await CheckDecisionsAsync(request, cancellationToken).ConfigureAwait(false);
            }

            // Оба вопроса разом: раньше второй ждал первого, и проверка раунда стоила двух запросов
            // подряд, а не одного.
            var safetyTask = _checkSafety
                ? CheckTextAsync(SynGuard.SystemPrompt, SynGuard.BuildUserMessage(request), count, cancellationToken)
                : Task.FromResult(new SynGuardReport(Enumerable.Repeat(true, count).ToArray(), null));
            var approvalTask = _checkApproval
                ? CheckTextAsync(SynGuard.ApprovalSystemPrompt, SynGuard.DecisionState(request), count, cancellationToken)
                : null;
            var safety = await safetyTask.ConfigureAwait(false);
            if (approvalTask is null)
            {
                return safety;
            }

            var approval = await approvalTask.ConfigureAwait(false);
            return safety with
            {
                WithoutApproval = approval.Outcome == SynGuardOutcome.Checked ? approval.Safe : new bool[count],
                Cost = (safety.Cost ?? VeniceCost.Zero).Add(approval.Cost ?? VeniceCost.Zero),
                Outcome = safety.Outcome != SynGuardOutcome.Checked ? safety.Outcome : approval.Outcome
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Проверка не состоялась, и неизвестно даже, Decisions ли это: каждый вызов с записью
            // идёт к человеку, а не молча запрещается (тупик) и не молча пропускается.
            return new SynGuardReport(Enumerable.Repeat(!_checkSafety, count).ToArray(), new VeniceCost(),
                SynGuardOutcome.Failed, _checkApproval ? new bool[count] : null);
        }
    }

    /// <summary>
    /// Оба вопроса одним запросом Decisions. Ответ «нет» на безопасность запрещает вызов, а
    /// отсутствующий ответ — не разрешает: такой вызов уходит человеку.
    /// </summary>
    private async Task<SynGuardReport> CheckDecisionsAsync(SynGuardRequest request, CancellationToken cancellationToken)
    {
        var count = request.Calls.Count;
        var (response, cost) = await _venice.CreateDecisionsAsync(_modelId,
            SynGuard.DecisionState(request), SynGuard.DecisionQuestions(count, _checkSafety, _checkApproval),
            cancellationToken).ConfigureAwait(false);

        var safety = _checkSafety ? Answers("safe_to_run") : null;
        var approval = _checkApproval ? Answers("safe_without_approval") : null;
        var complete = (safety ?? []).Concat(approval ?? []).All(answer => answer.HasValue);
        return new SynGuardReport(
            safety?.Select(answer => answer == true).ToArray() ?? Enumerable.Repeat(true, count).ToArray(),
            cost,
            complete ? SynGuardOutcome.Checked : SynGuardOutcome.Unparsed,
            approval?.Select(answer => answer == true).ToArray(),
            safety?.Select(answer => answer == false).ToArray());

        bool?[] Answers(string name) =>
        [
            .. Enumerable.Range(0, count).Select(i =>
                response.Answers?.GetValueOrDefault(SynGuard.DecisionQuestionId(name, i, count))?.BooleanValue)
        ];
    }

    /// <summary>
    /// Один текстовый запрос на весь раунд. Любой сбой — весь раунд безопасен: защита, которая
    /// легла вместе с сетью, не должна останавливать работу человека.
    /// </summary>
    private async Task<SynGuardReport> CheckTextAsync(
        string systemPrompt,
        string userMessage,
        int count,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _venice.CreateChatCompletionAsync(
                    _modelId,
                    [
                        new ChatMessage
                        {
                            Role = "system",
                            Content = ChatContent.Text(systemPrompt)
                        },
                        new ChatMessage
                        {
                            Role = "user",
                            Content = ChatContent.Text(userMessage)
                        }
                    ],
                    tools: null,
                    toolChoice: null,
                    new VeniceParameters
                    {
                        IncludeVeniceSystemPrompt = false,
                        EnableWebSearch = "off",
                        EnableXSearch = false,
                        StripThinkingResponse = true
                    },
                    cancellationToken,
                    _reasoning)
                .ConfigureAwait(false);

            var reply = ReasoningSplit.Split(
                ChatContent.ReadText(response.Choices.FirstOrDefault()?.Message.Content) ?? "").Answer;

            var safe = SynGuard.ParseReport(reply, count, out var complete);
            return new SynGuardReport(
                safe,
                response.ReportedCost(),
                complete ? SynGuardOutcome.Checked : SynGuardOutcome.Unparsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Сюда же — таймаут HTTP: он приходит как TaskCanceledException, хотя ход никто не
            // отменял, и раньше обрывал весь раунд так, будто человек нажал «Стоп».
            // Цена пустая, но не null: денег не списали, а проверка была затеяна.
            return new SynGuardReport(
                SynGuard.ParseReport(null, count),
                new VeniceCost(),
                SynGuardOutcome.Failed);
        }
    }
}

/// <summary>
/// Живая проверка SynGuard вместе с клиентом, на котором она ходит в сеть.
/// </summary>
/// <remarks>
/// Одна сборка на агента и на чат: прежде она жила в <c>AgentHost</c>, и инструменты чата
/// SynGuard не видел вовсе. Клиент свой, а не общий, по той же причине, что у агента: у
/// <see cref="VeniceClient"/> свой счёт потраченного, и деньги защитника должны лечь отдельной
/// строкой.
/// </remarks>
internal sealed class SynGuardLease : IDisposable
{
    private readonly HttpClient _http;

    private SynGuardLease(HttpClient http, Func<SynGuardRequest, CancellationToken, Task<SynGuardReport>> check)
    {
        _http = http;
        Check = check;
    }

    public Func<SynGuardRequest, CancellationToken, Task<SynGuardReport>> Check { get; }

    /// <summary>Проверка по настройкам; null — защита выключена, и ни одного запроса не уйдёт.</summary>
    public static SynGuardLease? Build(
        AppSettings settings,
        AgentOptions parentOptions,
        Func<string, VeniceModelInfo?>? resolveModelInfo)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.SynGuardEnabled && settings.ApprovalMode != ApprovalMode.Normal)
        {
            return null;
        }

        var http = HttpClients.Create(HttpClients.ServiceTimeout);
        var modelId = SynGuard.ResolveModel(settings);
        var reasoning = settings.SynGuardReasoning ?? new ReasoningSettings();
        var options = AgentHost.CloneOptions(
            parentOptions, modelId, reasoning, ModelSlots.ReadKey(settings, ModelSlot.SynGuard));
        var venice = new VeniceClient(http, options) { ResolveModelInfo = resolveModelInfo };
        var checker = new SynGuardChecker(venice, modelId, reasoning.ToChoice(),
            settings.SynGuardEnabled, settings.ApprovalMode == ApprovalMode.Normal);
        return new SynGuardLease(http, checker.CheckAsync);
    }

    public void Dispose() => _http.Dispose();
}
