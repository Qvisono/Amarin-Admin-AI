using System.Text.Json.Serialization;

namespace Amarin.Core;

// POST /api/alpha/decisions: выбор уровня, бинарные решения и расход.
internal sealed class OpenRouterDecisionResponse
{
    [JsonPropertyName("answers")]
    public Dictionary<string, OpenRouterDecisionAnswer>? Answers { get; init; }

    [JsonPropertyName("usage")]
    public OpenRouterDecisionUsage? Usage { get; init; }

    [JsonPropertyName("error")]
    public VeniceError? Error { get; init; }
}

internal sealed class OpenRouterDecisionAnswer
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("choice")]
    public string? Choice { get; init; }

    [JsonPropertyName("noul")]
    public double? Noul { get; init; }

    // Ровно половина — неопределённость, а не разрешение.
    public bool? BooleanValue => Type == "noul" && Noul is >= 0 and <= 1
        ? Noul > 0.5 : null;
}

internal sealed class OpenRouterDecisionUsage
{
    [JsonPropertyName("input_tokens")]
    public int? InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public int? OutputTokens { get; init; }

    [JsonPropertyName("cost")]
    public decimal? Cost { get; init; }
}
