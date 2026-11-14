using System.Globalization;
using System.Text.Json;

namespace Amarin.Core;

public sealed partial class VeniceClient
{
    private async Task<IReadOnlyList<VeniceModelInfo>> ListPolzaModelsAsync(
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        var body = await GetJsonAsync(
                "models?type=chat&include_providers=true&include_capabilities=true", credential,
                trackBalance: false, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return PolzaModels.Parse(body, _options.RublesPerUsd?.Invoke());
        }
        catch (JsonException ex)
        {
            throw new VeniceApiException(
                $"Polza.ai returned non-JSON models response ({ex.Message}). Body: {Preview(body)}");
        }
    }

    private ChatCompletionRequest BuildPolzaPayload(
        ChatCompletionRequest request,
        string model,
        bool prepareMessages,
        bool stream)
    {
        var reasoning = request.Reasoning;
        if (request.ReasoningChoice is { } choice)
        {
            var info = ResolveModelInfo?.Invoke(model);
            reasoning = PolzaReasoning(choice, info, model, request.Tools is { Count: > 0 });
        }
        else if (request.ReasoningEffort is { } effort)
        {
            // Верхний reasoning_effort Polza принимает, но молча игнорирует.
            reasoning = new ReasoningConfig { Effort = effort };
        }

        return new ChatCompletionRequest
        {
            Model = ModelRef.Bare(model),
            Messages = prepareMessages ? ApiContextLimiter.Prepare(request.Messages) : request.Messages,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            Temperature = null,
            Stream = stream,
            StreamOptions = stream ? new StreamOptions() : null,
            Reasoning = reasoning,
            ReasoningChoice = request.ReasoningChoice
        };
    }

    private static ReasoningConfig PolzaReasoning(
        ReasoningChoice choice, VeniceModelInfo? info, string model, bool withTools)
    {
        choice = ReasoningPolicy.Effective(choice, info, withTools);
        if (PolzaAdaptiveThinking(model, info))
        {
            if (choice.DisableThinking)
            {
                return new ReasoningConfig { Type = "disabled" };
            }

            var effort = PolzaEffort(choice, info, withTools);
            return new ReasoningConfig
            {
                Type = "adaptive",
                EffortLevel = effort switch { "minimal" => "low", "xhigh" => "high", _ => effort }
            };
        }

        return choice.DisableThinking
            ? new ReasoningConfig { Enabled = false }
            : new ReasoningConfig
            {
                Enabled = true,
                Effort = PolzaEffort(choice, info, withTools)
            };
    }

    private static string? PolzaEffort(ReasoningChoice choice, VeniceModelInfo? info, bool withTools) =>
        ReasoningPolicy.ClampEffort(choice.Effort, info, withTools) ??
        (ReasoningPolicy.EffortRank(choice.Effort) < 6 ? choice.Effort?.Trim().ToLowerInvariant() : null);

    /// <summary>Метаданные важнее имени; без них новые Claude используют адаптивный синтаксис.</summary>
    private static bool PolzaAdaptiveThinking(string model, VeniceModelInfo? info)
    {
        if (info?.ModelSpec?.Capabilities?.ThinkingModes is { } modes)
        {
            return modes.Contains("adaptive", StringComparer.OrdinalIgnoreCase);
        }

        var name = ModelFamily.Normalize(ModelRef.Bare(model));
        if (!name.StartsWith("claude-", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = name.Split('-');
        for (var index = 1; index < parts.Length; index++)
        {
            if (int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
            {
                return major >= 5 || major == 4 && index + 1 < parts.Length &&
                    int.TryParse(parts[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) && minor >= 7;
            }
        }

        return false;
    }
}
