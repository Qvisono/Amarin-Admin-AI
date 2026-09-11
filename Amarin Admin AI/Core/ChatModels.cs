using System.Text.Json.Serialization;
using Amarin.Tools;

namespace Amarin.Core;

public enum AssistantStatus
{
    Streaming,
    Complete,
    Cancelled,
    Error
}

public enum ToolCallStatus
{
    Pending,
    Running,
    Done,
    Failed
}

public enum AgentRunStatus
{
    Running,
    Complete,
    Cancelled,
    Failed
}

public sealed class ChatSession
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "Новый чат";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string SelectedModelId { get; set; } = "";

    /// <summary>When true, the chat does not ask the model to think. Default preserves old behaviour.</summary>
    public bool DisableThinking { get; set; } = true;

    public string? ReasoningEffort { get; set; }

    [JsonIgnore]
    public ReasoningChoice Reasoning => new(DisableThinking, ReasoningEffort);

    public List<ChatDisplayMessage> Messages { get; set; } = [];

    public List<ChatMessage> ApiMessages { get; set; } = [];
}

public sealed class ChatDisplayMessage
{
    public string Role { get; set; } = "user";

    public string Id { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public string Text { get; set; } = "";

    /// <summary>
    /// Images the user attached to this message, stored inline as base64 so they survive a
    /// restart and travel with an exported or shared chat. Empty for assistant messages.
    /// </summary>
    public List<ImageAttachment> Images { get; set; } = [];

    /// <summary>
    /// Документы, прикреплённые к сообщению: PDF, таблицы, исходники. Хранятся так же, как
    /// картинки, — base64 внутри чата. У старых переписок поля в JSON нет, и список выходит
    /// пустым: чат сериализуется рефлексией, миграция не нужна.
    /// </summary>
    public List<FileAttachment> Files { get; set; } = [];

    public string? RequestedModelId { get; set; }

    public string? ResolvedModelId { get; set; }

    public TimeSpan Duration { get; set; }

    /// <summary>
    /// How long the model spent before the first word of the answer. Shown beside the duration
    /// when it is worth mentioning; zero for models that start writing immediately.
    /// </summary>
    public TimeSpan ThinkingDuration { get; set; }

    public VeniceCost? Cost { get; set; }

    /// <summary>
    /// What the conversation with the model itself cost, with tools and nested agents taken
    /// out. <see cref="Cost"/> is the sum of this, every tool call's own price and every nested
    /// agent's — which is exactly the breakdown shown when hovering the price.
    /// </summary>
    public VeniceCost? ModelCost { get; set; }

    public AssistantStatus Status { get; set; }

    public List<ToolRound> ToolRounds { get; set; } = [];
}

public sealed class ToolRound
{
    public string InfoLine { get; set; } = "";

    public List<ToolCallRecord> Calls { get; set; } = [];
}

public sealed class ToolCallRecord
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string ArgumentsJson { get; set; } = "";

    public string ResultPreview { get; set; } = "";

    public bool Success { get; set; }

    public ToolCallStatus Status { get; set; }

    /// <summary>
    /// Pictures the tool produced — a generated image, a screenshot. Stored inline as base64 so
    /// they survive a restart and travel with an exported chat, exactly like user attachments.
    /// </summary>
    public List<ImageAttachment> Images { get; set; } = [];

    /// <summary>
    /// What this one call added to the turn's bill. Display only — it is already inside the
    /// message total, so summing it again would double-count. Set for the tools that actually
    /// cost money (drawing a picture, scraping a page); null everywhere else.
    /// </summary>
    public VeniceCost? Cost { get; set; }

    public AgentRunRecord? NestedAgent { get; set; }
}

public sealed class AgentRunRecord
{
    public int SlotIndex { get; set; }

    public string ModelId { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public AgentRunStatus Status { get; set; }

    public List<ToolRound> ToolRounds { get; set; } = [];

    public string ReportText { get; set; } = "";

    public VeniceCost? Cost { get; set; }
}

public sealed class ChatIndex
{
    public List<ChatIndexEntry> Items { get; set; } = [];
}

public sealed class ChatIndexEntry
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Kept at the top of the sidebar, out of the by-date groups. Lives on the index rather
    /// than the session so pinning never counts as an edit to the conversation itself.
    /// </summary>
    public bool IsPinned { get; set; }
}
