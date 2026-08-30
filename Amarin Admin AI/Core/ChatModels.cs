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

    public List<ChatDisplayMessage> Messages { get; set; } = [];

    public List<ChatMessage> ApiMessages { get; set; } = [];
}

public sealed class ChatDisplayMessage
{
    public string Role { get; set; } = "user";

    public string Id { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public string Text { get; set; } = "";

    public string? RequestedModelId { get; set; }

    public string? ResolvedModelId { get; set; }

    public TimeSpan Duration { get; set; }

    public VeniceCost? Cost { get; set; }

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
}
