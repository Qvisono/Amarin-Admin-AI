using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amarin.Core;

public sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("messages")]
    public required List<ChatMessage> Messages { get; init; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolDefinition>? Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolChoice { get; init; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; } = 0.2;

    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Stream { get; init; }

    [JsonPropertyName("venice_parameters")]
    public VeniceParameters VeniceParameters { get; init; } = new();
}

public sealed class VeniceParameters
{
    [JsonPropertyName("include_venice_system_prompt")]
    public bool IncludeVeniceSystemPrompt { get; init; }

    [JsonPropertyName("enable_web_search")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnableWebSearch { get; init; }

    [JsonPropertyName("enable_web_citations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableWebCitations { get; init; }

    [JsonPropertyName("enable_x_search")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableXSearch { get; init; }

    [JsonPropertyName("disable_thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DisableThinking { get; init; }

    [JsonPropertyName("strip_thinking_response")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? StripThinkingResponse { get; init; }
}

public sealed class ChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Content { get; init; }

    /// <summary>Read-only mirror of the streaming delta field; never sent back to the API.</summary>
    [JsonPropertyName("reasoning_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ReasoningContent { get; init; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
}

public sealed class ToolCall
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public required FunctionCall Function { get; init; }
}

public sealed class FunctionCall
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    [JsonConverter(typeof(ToolArgumentsJsonConverter))]
    public required string Arguments { get; init; }
}

public sealed class ToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public required FunctionDefinition Function { get; init; }
}

public sealed class FunctionDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("parameters")]
    public required JsonElement Parameters { get; init; }
}

public sealed class ChatCompletionResponse
{
    [JsonPropertyName("choices")]
    public List<ChatChoice> Choices { get; init; } = [];

    [JsonPropertyName("cost")]
    public VeniceCostResponse? Cost { get; init; }

    [JsonPropertyName("error")]
    public VeniceError? Error { get; init; }
}

public sealed class ChatChoice
{
    [JsonPropertyName("message")]
    public required ChatMessage Message { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

public sealed class VeniceError
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

public sealed class ChatCompletionChunk
{
    [JsonPropertyName("choices")]
    public List<ChatChunkChoice> Choices { get; init; } = [];

    [JsonPropertyName("cost")]
    public VeniceCostResponse? Cost { get; init; }

    [JsonPropertyName("error")]
    public VeniceError? Error { get; init; }
}

public sealed class ChatChunkChoice
{
    [JsonPropertyName("delta")]
    public ChatMessageDelta? Delta { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

public sealed class ChatMessageDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    public JsonElement? Content { get; init; }

    /// <summary>
    /// Reasoning models (grok-4-x) stream their chain of thought here and only then start
    /// filling <see cref="Content"/>. Venice ignores disable_thinking/strip_thinking_response
    /// for them, so the field arrives whether we ask for it or not.
    /// </summary>
    [JsonPropertyName("reasoning_content")]
    public JsonElement? ReasoningContent { get; init; }

    [JsonPropertyName("tool_calls")]
    public List<ToolCallDelta>? ToolCalls { get; init; }
}

public sealed class ToolCallDelta
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("function")]
    public FunctionCallDelta? Function { get; init; }
}

public sealed class FunctionCallDelta
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("arguments")]
    [JsonConverter(typeof(ToolArgumentsJsonConverter))]
    public string? Arguments { get; init; }
}

public sealed class StreamedChatCompletion
{
    public string Text { get; init; } = "";

    /// <summary>
    /// Chain of thought collected from <c>reasoning_content</c>. Never shown as the answer
    /// unless the model produced nothing else — see <c>ChatEngine.StreamWithRetryAsync</c>.
    /// </summary>
    public string ReasoningText { get; init; } = "";

    /// <summary>
    /// Chain of thought the model wrote into <c>content</c> in <c>&lt;think&gt;</c>-style tags
    /// rather than on the reasoning channel. Already removed from <see cref="Text"/>.
    /// </summary>
    public string InlineReasoning { get; init; } = "";

    /// <summary>Time from the first chunk to the first word of the answer.</summary>
    public TimeSpan ThinkingElapsed { get; init; }

    public List<ToolCall> ToolCalls { get; init; } = [];

    public string? FinishReason { get; init; }

    public VeniceCost Cost { get; init; } = VeniceCost.Zero;

    public string Model { get; init; } = "";
}

public sealed class VeniceModelsListResponse
{
    [JsonPropertyName("data")]
    public List<VeniceModelInfo> Data { get; init; } = [];

    [JsonPropertyName("object")]
    public string? Object { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

public sealed class VeniceModelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("object")]
    public string? Object { get; init; }

    [JsonPropertyName("owned_by")]
    public string? OwnedBy { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("created")]
    public long? Created { get; init; }

    [JsonPropertyName("context_length")]
    public int? ContextLength { get; init; }

    [JsonPropertyName("model_spec")]
    public VeniceModelSpec? ModelSpec { get; init; }
}

public sealed class VeniceModelSpec
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("availableContextTokens")]
    public int? AvailableContextTokens { get; init; }

    [JsonPropertyName("offline")]
    public bool Offline { get; init; }

    [JsonPropertyName("beta")]
    public bool Beta { get; init; }

    [JsonPropertyName("traits")]
    public List<string>? Traits { get; init; }

    [JsonPropertyName("capabilities")]
    public VeniceModelCapabilities? Capabilities { get; init; }
}

public sealed class VeniceModelCapabilities
{
    [JsonPropertyName("optimizedForCode")]
    public bool OptimizedForCode { get; init; }

    [JsonPropertyName("supportsFunctionCalling")]
    public bool SupportsFunctionCalling { get; init; }

    [JsonPropertyName("supportsReasoning")]
    public bool SupportsReasoning { get; init; }

    [JsonPropertyName("supportsVision")]
    public bool SupportsVision { get; init; }

    [JsonPropertyName("quantization")]
    public string? Quantization { get; init; }
}
/// <summary>
/// Request for Venice's image endpoint. Separate from chat completions: it is a different API
/// shape, and the models that serve it are not in the text catalogue.
/// </summary>
public sealed class ImageGenerateRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [JsonPropertyName("negative_prompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NegativePrompt { get; init; }

    // Pixel size and aspect ratio are alternatives, not companions: the diffusion models take
    // width/height, while the Gemini-backed nano-banana line is driven by aspect_ratio plus a
    // resolution tier and rejects pixel dimensions. Both are nullable so only one goes on the wire.
    [JsonPropertyName("width")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Height { get; init; }

    [JsonPropertyName("aspect_ratio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AspectRatio { get; init; }

    [JsonPropertyName("resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Resolution { get; init; }

    [JsonPropertyName("format")]
    public string Format { get; init; } = "png";

    /// <summary>Base64 in the JSON body rather than raw bytes, so it can go straight into a message.</summary>
    [JsonPropertyName("return_binary")]
    public bool ReturnBinary { get; init; }

    [JsonPropertyName("safe_mode")]
    public bool SafeMode { get; init; }

    [JsonPropertyName("hide_watermark")]
    public bool HideWatermark { get; init; } = true;
}

public sealed class ImageGenerateResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("images")]
    public List<string> Images { get; init; } = [];

    /// <summary>
    /// Same shape as on a chat completion. Modelled so an image is billed in the message header
    /// like everything else; if the endpoint omits it, the caller falls back to the balance delta.
    /// </summary>
    [JsonPropertyName("cost")]
    public VeniceCostResponse? Cost { get; init; }
}
