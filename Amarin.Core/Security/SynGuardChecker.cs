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

    public SynGuardChecker(VeniceClient venice, string modelId, ReasoningChoice reasoning)
    {
        _venice = venice;
        _modelId = modelId;
        _reasoning = reasoning;
    }

    /// <summary>
    /// Проверяет весь раунд одним запросом. Любой сбой — весь раунд безопасен: защита, которая
    /// легла вместе с сетью, не должна останавливать работу человека.
    /// </summary>
    public async Task<SynGuardReport> CheckAsync(
        SynGuardRequest request,
        CancellationToken cancellationToken)
    {
        var calls = request.Calls;
        ArgumentNullException.ThrowIfNull(calls);
        if (calls.Count == 0)
        {
            return new SynGuardReport([], null);
        }

        using var charge = VeniceClient.ChargeAs(VeniceSku.Guard);
        try
        {
            var response = await _venice.CreateChatCompletionAsync(
                    _modelId,
                    [
                        new ChatMessage
                        {
                            Role = "system",
                            Content = ChatContent.Text(SynGuard.SystemPrompt)
                        },
                        new ChatMessage
                        {
                            Role = "user",
                            Content = ChatContent.Text(SynGuard.BuildUserMessage(request))
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

            var safe = SynGuard.ParseReport(reply, calls.Count, out var complete);
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
                SynGuard.ParseReport(null, calls.Count),
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
        if (!settings.SynGuardEnabled)
        {
            return null;
        }

        var http = HttpClients.Create(HttpClients.ServiceTimeout);
        var modelId = SynGuard.ResolveModel(settings);
        var reasoning = settings.SynGuardReasoning ?? new ReasoningSettings();
        var options = AgentHost.CloneOptions(
            parentOptions, modelId, reasoning, ModelSlots.ReadKey(settings, ModelSlot.SynGuard));
        var venice = new VeniceClient(http, options) { ResolveModelInfo = resolveModelInfo };
        var checker = new SynGuardChecker(venice, modelId, reasoning.ToChoice());
        return new SynGuardLease(http, checker.CheckAsync);
    }

    public void Dispose() => _http.Dispose();
}
