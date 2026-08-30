using Amarin.Tools;

namespace Amarin.Core;

public sealed class AgentRunResult
{
    public string? AssistantText { get; init; }

    public VeniceCost Cost { get; init; } = VeniceCost.Zero;

    public bool Cancelled { get; init; }

    public string? Error { get; init; }
}

public interface IAgentUi
{
    void Warn(string message);

    void Error(string message);

    void Info(string message);

    void AssistantMessage(string text);

    void ToolCall(string name, string argumentsJson);

    void ToolResult(string name, ToolResult result);

    Task<T> RunBusyAsync<T>(string message, Func<Task<T>> work, CancellationToken cancellationToken = default);

    Task<bool> ConfirmDangerousActionAsync(
        DangerousActionInfo info,
        CancellationToken cancellationToken = default);
}
