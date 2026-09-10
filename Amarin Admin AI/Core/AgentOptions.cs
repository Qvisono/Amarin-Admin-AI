namespace Amarin.Core;

public sealed class AgentOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = "https://api.venice.ai/api/v1";
    public string Model { get; set; } = "grok-4-6";
    public int MaxToolRounds { get; init; } = 30;

    /// <summary>Agent-only. The shared launch <see cref="AgentOptions"/> must stay off so chat cannot leak here.</summary>
    public bool DisableThinking { get; init; } = true;

    public string? ReasoningEffort { get; init; }

    public ReasoningChoice Reasoning => new(DisableThinking, ReasoningEffort);

    public string WebSearch { get; init; } = "off";

    public bool EnableWebCitations { get; init; } = true;

    /// <summary>Native xAI search for SearchWebAsync on Grok models. Agent chat always disables it.</summary>
    public bool? EnableXSearch { get; init; }

    public DownloadOptions Download { get; init; } = new();
}