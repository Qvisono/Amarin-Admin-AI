using System.Globalization;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>Каталог Polza.ai: возможности выбранного провайдера и его собственные цены в рублях.</summary>
internal static class PolzaModels
{
    public static IReadOnlyList<VeniceModelInfo> Parse(string json, decimal? rublesPerUsd)
    {
        using var document = JsonDocument.Parse(json);
        var items = Property(document.RootElement, "data");
        var models = new List<VeniceModelInfo>();
        if (items.ValueKind != JsonValueKind.Array)
        {
            return models;
        }

        foreach (var item in items.EnumerateArray())
        {
            var id = Text(item, "id");
            var type = Text(item, "type");
            if (string.IsNullOrWhiteSpace(id) ||
                (type is not null && !type.Equals("chat", StringComparison.OrdinalIgnoreCase)) ||
                (type is null && !ModelListParser.IsForChat(id)))
            {
                continue;
            }

            var architecture = Property(item, "architecture");
            var input = Strings(architecture, "input_modalities");
            var output = Strings(architecture, "output_modalities");
            if ((input is { Count: > 0 } && !Contains(input, "text")) ||
                (output is { Count: > 0 } && !Contains(output, "text")))
            {
                continue;
            }

            var provider = Property(item, "top_provider");
            if (provider.ValueKind != JsonValueKind.Object)
            {
                // Прежний ответ держит возможности и цены прямо в записи модели.
                provider = item;
            }

            var parameters = Strings(provider, "supported_parameters");
            var capabilities = Property(provider, "capabilities");
            var reasoningInfo = Property(provider, "reasoning");
            var efforts = Strings(Property(capabilities, "reasoning_effort"), "values") ??
                          Strings(reasoningInfo, "supported_efforts");
            var reasoning = Contains(parameters, "reasoning") || Contains(parameters, "reasoning_effort") ||
                            Supported(capabilities, "reasoning_toggle") == true ||
                            Supported(capabilities, "reasoning_effort") == true ||
                            efforts is { Count: > 0 } || Boolean(reasoningInfo, "mandatory") == true;
            var effort = Supported(capabilities, "reasoning_effort") ??
                         (Contains(parameters, "reasoning_effort") || efforts is { Count: > 0 });
            var context = PositiveInt(provider, "context_length");
            var slash = id.IndexOf('/', StringComparison.Ordinal);

            models.Add(new VeniceModelInfo
            {
                Id = ModelRef.Qualify(LlmProvider.Polza, id),
                Object = "model",
                Type = "text",
                OwnedBy = Text(item, "owned_by") ?? (slash > 0 ? id[..slash] : null),
                Created = Integer(item, "created"),
                ContextLength = context,
                ModelSpec = new VeniceModelSpec
                {
                    Name = Text(item, "name"),
                    Description = Text(item, "short_description") ?? Text(item, "description"),
                    AvailableContextTokens = context,
                    MaxOutputTokens = PositiveInt(provider, "max_completion_tokens"),
                    Pricing = Pricing(Property(provider, "pricing"), rublesPerUsd),
                    Capabilities = new VeniceModelCapabilities
                    {
                        SupportsFunctionCalling = Supported(capabilities, "tools") ??
                                                  (parameters is null || Contains(parameters, "tools")),
                        SupportsVision = Supported(capabilities, "input_image") ?? Contains(input, "image"),
                        SupportsReasoning = reasoning,
                        SupportsReasoningEffort = effort,
                        SupportsReasoningEffortWithTools = true,
                        ReasoningEffortOptions = effort && efforts is not null
                            ? [.. efforts.Where(value => value.Equals(ReasoningPolicy.None, StringComparison.OrdinalIgnoreCase) ||
                                                        ReasoningPolicy.EffortRank(value) < ReasoningPolicy.EffortRank(null))
                                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(ReasoningPolicy.EffortRank)]
                            : null,
                        DefaultReasoningEffort = Text(reasoningInfo, "default_effort")?.ToLowerInvariant(),
                        ReasoningMandatory = Boolean(reasoningInfo, "mandatory") == true ||
                                             reasoning && Supported(capabilities, "reasoning_toggle") == false
                    }
                }
            });
        }

        return models;
    }

    private static VeniceModelPricing? Pricing(JsonElement pricing, decimal? rate)
    {
        var currency = Text(pricing, "currency");
        if (currency is not null && !currency.Equals("RUB", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var input = Price(pricing, "prompt_per_million", "prompt", rate);
        var output = Price(pricing, "completion_per_million", "completion", rate);
        return input is not null && output is not null
            ? new VeniceModelPricing
            {
                Input = input,
                Output = output,
                CacheInput = Price(pricing, "input_cache_read_per_million", "input_cache_read", rate),
                CacheWrite = Price(pricing, "input_cache_write_per_million", "input_cache_write", rate)
            }
            : null;
    }

    private static VeniceUnitPrice? Price(JsonElement pricing, string name, string perTokenName, decimal? rate)
    {
        if (Number(pricing, name) is { } perMillion)
        {
            return ProviderMoney.ToUsd(perMillion, rate) is { } usd ? new VeniceUnitPrice { Usd = usd } : null;
        }

        // Старый каталог называет рубли за токен; внутренний каталог всегда за миллион.
        return Number(pricing, perTokenName) is { } rubles &&
               ProviderMoney.ToUsd(rubles, rate) is >= 0m and <= decimal.MaxValue / 1_000_000m and var tokenUsd
            ? new VeniceUnitPrice { Usd = tokenUsd * 1_000_000m }
            : null;
    }

    private static bool? Supported(JsonElement capabilities, string name) =>
        Text(Property(capabilities, name), "status")?.ToLowerInvariant() switch
        {
            "supported" => true,
            "rejected" or "ignored" => false,
            _ => null
        };

    private static bool Contains(List<string>? values, string value) =>
        values?.Contains(value, StringComparer.OrdinalIgnoreCase) == true;

    private static List<string>? Strings(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        return value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!.Trim().ToLowerInvariant()).Where(item => item.Length > 0)]
            : null;
    }

    private static JsonElement Property(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value : default;

    private static string? Text(JsonElement parent, string name) =>
        Property(parent, name) is { ValueKind: JsonValueKind.String } value ? value.GetString()?.Trim() : null;

    private static bool? Boolean(JsonElement parent, string name) =>
        Property(parent, name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value ? value.GetBoolean() : null;

    private static long? Integer(JsonElement parent, string name) =>
        Property(parent, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number)
            ? number
            : null;

    private static int? PositiveInt(JsonElement parent, string name) =>
        Integer(parent, name) is > 0 and <= int.MaxValue and var number ? (int)number : null;

    private static decimal? Number(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }
}
