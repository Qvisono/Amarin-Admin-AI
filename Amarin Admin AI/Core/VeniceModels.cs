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