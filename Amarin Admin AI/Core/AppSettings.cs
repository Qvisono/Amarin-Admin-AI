namespace Amarin.Core;

public enum ApprovalMode
{
    Normal,
    AlwaysApprove
}

public enum AppTheme
{
    System,
    Light,
    Dark
}

public sealed class AppSettings
{
    public bool AutoScroll { get; set; } = true;

    /// <summary>Colour scheme. <see cref="AppTheme.System"/> follows the Windows app theme.</summary>
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>Uniform UI zoom, percent. Allowed: 80, 90, 100, 110, 125, 150, 175, 200, 225, 250.</summary>
    public int UiScalePercent { get; set; } = 100;

    public ApprovalMode ApprovalMode { get; set; } = ApprovalMode.Normal;

    /// <summary>Show the bottom-right toast when a turn finishes and the window is not focused.</summary>
    public bool NotifyOnResponseComplete { get; set; } = true;

    /// <summary>Play a short system sound with that toast. Ignored when the toast is off.</summary>
    public bool NotifySound { get; set; } = true;

    /// <summary>
    /// Show the "поделиться" and "экспорт" buttons under messages. Both produce unencrypted
    /// payloads that carry the whole conversation, so the feature can be switched off entirely.
    /// </summary>
    public bool ChatSharingEnabled { get; set; } = true;

    /// <summary>
    /// Hosts <c>download_file</c> may download from, subdomains included.
    /// <c>null</c> means "not seeded yet" — the store fills it from the shipped defaults on first load.
    /// An empty list is a deliberate choice and blocks every download.
    /// </summary>
    public List<string>? DownloadAllowedDomains { get; set; }

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

    public static AppSettings CreateDefault() => new()
    {
        DownloadAllowedDomains = [.. new DownloadOptions().AllowedDomains]
    };
}
