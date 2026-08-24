using System.Text.Json.Serialization;

namespace Amarin.Core;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ScrapeUrlRequest))]
[JsonSerializable(typeof(string))]
internal partial class VeniceJsonContext : JsonSerializerContext;

internal sealed class ScrapeUrlRequest
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }
}
