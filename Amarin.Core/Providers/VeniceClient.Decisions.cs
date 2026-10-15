using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amarin.Core;

public sealed partial class VeniceClient
{
    /// <summary>
    /// Выбор уровня: обычная модель отвечает словом, Decisions — типизированным choice.
    /// Правила Decisions передаются структурированным вопросом; текстовая модель сохраняет свой промпт.
    /// </summary>
    internal async Task<ChatCompletionResponse> CreateRoutingCompletionAsync(
        string model,
        string instructions,
        string state,
        IReadOnlyList<string> tiers,
        JsonObject decisionQuestion,
        VeniceParameters veniceParameters,
        CancellationToken cancellationToken,
        ReasoningChoice reasoning,
        ApiCredential? credential = null)
    {
        var key = Resolve(credential);
        if (!await UsesDecisionsAsync(model, cancellationToken, key).ConfigureAwait(false))
        {
            return await CreateChatCompletionAsync(
                model,
                [
                    new ChatMessage { Role = "system", Content = ChatContent.Text(instructions) },
                    new ChatMessage { Role = "user", Content = ChatContent.Text(state) }
                ],
                tools: null, toolChoice: null, veniceParameters, cancellationToken, reasoning, credential)
                .ConfigureAwait(false);
        }

        var questions = new JsonObject
        {
            ["tier"] = decisionQuestion
        };
        var (decision, _) = await CreateDecisionsAsync(model, state, questions, cancellationToken, key)
            .ConfigureAwait(false);
        var answer = decision.Answers?.GetValueOrDefault("tier");
        var choice = answer is { Type: "choice", Choice: { } picked } && tiers.Contains(picked)
            ? picked : "";
        return new ChatCompletionResponse
        {
            Choices = [new ChatChoice { Message = new ChatMessage { Role = "assistant", Content = ChatContent.Text(choice) } }],
            Usage = DecisionUsage(decision)
        };
    }

    internal async Task<bool> UsesDecisionsAsync(string model, CancellationToken cancellationToken, ApiCredential? credential = null)
    {
        var key = Resolve(credential);
        if (ModelRef.Of(model, key.Provider) != LlmProvider.OpenRouter)
        {
            return false;
        }

        var info = ResolveModelInfo?.Invoke(model);
        if (info is null && ResolveModelInfo is not null)
        {
            var models = await ListTextModelsAsync(key, cancellationToken).ConfigureAwait(false);
            info = ReasoningPolicy.Find(models, model);
        }

        return VeniceModelCatalog.IsDecisionModel(info);
    }

    internal async Task<(OpenRouterDecisionResponse Response, VeniceCost? Cost)> CreateDecisionsAsync(
        string model, string state, JsonObject questions, CancellationToken cancellationToken,
        ApiCredential? credential = null)
    {
        var key = RequireKey(Resolve(credential), model);
        if (key.Provider != LlmProvider.OpenRouter)
        {
            throw new VeniceApiException("Decisions requires an OpenRouter key.");
        }

        await GuardSpendAsync(key, cancellationToken).ConfigureAwait(false);
        var payload = new JsonObject
        {
            ["model"] = ModelRef.Bare(model), ["state"] = state, ["questions"] = questions
        };

        // Decisions живёт рядом с /v1, а не внутри него. Request сохраняет общий выбор
        // адреса и ключа; относительный путь не меняет получателя секрета.
        using var request = Request(HttpMethod.Post, "../alpha/decisions", key);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ApiError(key, (int)response.StatusCode, ExtractErrorMessage(body));
        }

        var decision = JsonSerializer.Deserialize(body, VeniceJsonContext.Default.OpenRouterDecisionResponse)
            ?? throw new VeniceApiException("Empty Decisions response.");
        if (decision.Error is not null)
        {
            throw new VeniceApiException(decision.Error.Message ?? "Decisions API error.");
        }

        var result = new ChatCompletionResponse { Usage = DecisionUsage(decision) };

        // Даже непонятный выбор мог быть оплачен: учитываем его, затем вызывающий берёт запас.
        RecordCost(result, model, key);
        return (decision, result.ReportedCost());
    }

    private static VeniceUsage? DecisionUsage(OpenRouterDecisionResponse decision) =>
        decision.Usage is { } usage ? new VeniceUsage
        {
            PromptTokens = usage.InputTokens, CompletionTokens = usage.OutputTokens, Cost = usage.Cost
        } : null;
}
