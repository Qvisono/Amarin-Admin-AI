using System.Text.Json.Serialization;

namespace Amarin.Core;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ChatCompletionChunk))]
[JsonSerializable(typeof(VeniceModelsListResponse))]
[JsonSerializable(typeof(ScrapeUrlRequest))]
[JsonSerializable(typeof(ImageGenerateRequest))]
[JsonSerializable(typeof(ImageGenerateResponse))]
[JsonSerializable(typeof(VeniceUsagePage))]
[JsonSerializable(typeof(VeniceRateLimitsResponse))]
[JsonSerializable(typeof(OpenRouterModelsResponse))]
[JsonSerializable(typeof(OpenRouterBatchSubmit))]
[JsonSerializable(typeof(OpenRouterBatchObject))]
[JsonSerializable(typeof(OpenRouterKeyResponse))]
[JsonSerializable(typeof(OpenRouterCreditsResponse))]
[JsonSerializable(typeof(string))]
internal partial class VeniceJsonContext : JsonSerializerContext;

internal sealed class ScrapeUrlRequest
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }
}
