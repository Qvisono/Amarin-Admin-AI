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

    /// <summary>
    /// Asks for the token count on a streamed answer. Only ever set alongside
    /// <see cref="Stream"/>: a non-streaming request reports usage on its own, and some models
    /// reject the field outright when there is no stream to attach it to.
    /// </summary>
    [JsonPropertyName("stream_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StreamOptions? StreamOptions { get; init; }

    [JsonPropertyName("reasoning_effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningEffort { get; init; }

    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReasoningConfig? Reasoning { get; init; }

    [JsonPropertyName("venice_parameters")]
    public VeniceParameters VeniceParameters { get; init; } = new();

    /// <summary>
    /// Caller's intent. Never serialized — <see cref="VeniceClient"/> clamps it per target
    /// model (including fallback) before the body goes on the wire.
    /// </summary>
    [JsonIgnore]
    public ReasoningChoice? ReasoningChoice { get; init; }
}

public sealed class StreamOptions
{
    [JsonPropertyName("include_usage")]
    public bool IncludeUsage { get; init; } = true;
}

/// <summary>
/// How many tokens the request actually cost. <see cref="PromptTokens"/> is the whole context the
/// model read — system prompt, history, tool results — which is exactly what the context ring
/// shows; counting it locally can only ever be a guess.
/// </summary>
public sealed class VeniceUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; init; }

    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; init; }
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

/// <summary>
/// Nested <c>reasoning</c> object on chat completions. Used to flip Venice's
/// <c>enabled: false</c> switch; effort itself goes on the top-level field.
/// </summary>
public sealed class ReasoningConfig
{
    [JsonPropertyName("enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; init; }

    [JsonPropertyName("effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Effort { get; init; }
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

    [JsonPropertyName("usage")]
    public VeniceUsage? Usage { get; init; }

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

    /// <summary>
    /// Arrives once, on a trailing chunk whose <see cref="Choices"/> is empty. Anything that reads
    /// it after picking a choice out of the list will never see it.
    /// </summary>
    [JsonPropertyName("usage")]
    public VeniceUsage? Usage { get; init; }

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

    /// <summary>
    /// Context the model read for this request, as counted by Venice. Zero when the API stayed
    /// quiet about it — the caller then falls back to an estimate rather than showing nothing.
    /// </summary>
    public int PromptTokens { get; init; }

    public int TotalTokens { get; init; }

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

    [JsonPropertyName("supportsReasoningEffort")]
    public bool SupportsReasoningEffort { get; init; }

    /// <summary>
    /// When false, <c>/chat/completions</c> rejects function tools together with a non-none
    /// <c>reasoning_effort</c>. Null means the catalogue did not say — we fall back to known families.
    /// </summary>
    [JsonPropertyName("supportsReasoningEffortWithTools")]
    public bool? SupportsReasoningEffortWithTools { get; init; }

    [JsonPropertyName("reasoningEffortOptions")]
    public List<string>? ReasoningEffortOptions { get; init; }

    [JsonPropertyName("defaultReasoningEffort")]
    public string? DefaultReasoningEffort { get; init; }

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
