namespace Amarin.Core;

public enum ApprovalMode
{
    Normal,
    AlwaysApprove
}

public sealed class AppSettings
{
    public bool AutoScroll { get; set; } = true;

    /// <summary>Uniform UI zoom, percent. Allowed: 80, 90, 100, 110, 125, 150.</summary>
    public int UiScalePercent { get; set; } = 100;

    public ApprovalMode ApprovalMode { get; set; } = ApprovalMode.Normal;

    /// <summary>Empty means use Venice:Model from the shipped appsettings.json.</summary>
    public string ChatModelId { get; set; } = "";

    public string LiteModelId { get; set; } = "qwen-3-7-plus";

    public string HeavyModelId { get; set; } = "claude-sonnet-5";

    public string RouterModelId { get; set; } = "qwen-3-7-plus";

    public string TitleModelId { get; set; } = "qwen-3-7-plus";

    public string AgentLiteModelId { get; set; } = "grok-4-3";

    public string AgentHeavyModelId { get; set; } = "grok-4-3";

    /// <summary>Optional personality. Empty means the chat companion uses only the tech prompt.</summary>
    public string MainPrompt { get; set; } = "";

    public string TechAiPrompt { get; set; } = "";

    public string TechAgentPrompt { get; set; } = "";

    public SessionMode SessionMode { get; set; } = SessionMode.Continuous;

    public static AppSettings CreateDefault() => new();
}
