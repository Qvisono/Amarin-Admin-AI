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
/// Клиент один на прогон агента, а не на раунд: <see cref="VeniceClient"/> правит
/// <c>BaseAddress</c> и заголовки, поэтому делить чужой нельзя, но и заводить новый на каждый
/// раунд незачем.
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

            return new SynGuardReport(
                SynGuard.ParseReport(reply, calls.Count),
                response.Cost?.ToCost());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Цена пустая, но не null: денег не списали, а проверка была затеяна.
            return new SynGuardReport(SynGuard.ParseReport(null, calls.Count), new VeniceCost());
        }
    }
}
