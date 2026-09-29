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

    /// <summary>
    /// Вводная от человека, дописанная уже во время работы агента.
    /// </summary>
    /// <remarks>
    /// Не <see cref="Info"/>: тот пишет в строку состояния раунда, которую движок агента тут же
    /// переписывает своим «инструменты завершены», — сказанное человеком мелькнуло бы и пропало.
    /// Реализация по умолчанию оставлена ради тех, кому эта разница не нужна.
    /// </remarks>
    void UserNote(string text) => Info(text);

    /// <summary>Исход проверки SynGuard для раунда, который сейчас начнётся.</summary>
    /// <remarks>Реализация по умолчанию — для тех, кому раунды рисовать не надо.</remarks>
    void GuardChecked(SynGuardOutcome outcome)
    {
    }

    void ToolCall(string name, string argumentsJson);

    void ToolResult(string name, ToolResult result);

    Task<T> RunBusyAsync<T>(string message, Func<Task<T>> work, CancellationToken cancellationToken = default);

    Task<bool> ConfirmDangerousActionAsync(
        DangerousActionInfo info,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// То же, но с тем, кто разрешил: человек, режим «всё автоматически» или разрешение
    /// инструмента впрок. Нужно журналу аудита.
    /// </summary>
    /// <remarks>Реализация по умолчанию — для тех, у кого разрешать впрок нечем.</remarks>
    async Task<ConfirmationAnswer> ConfirmDetailedAsync(
        DangerousActionInfo info,
        CancellationToken cancellationToken = default) =>
        new(await ConfirmDangerousActionAsync(info, cancellationToken).ConfigureAwait(false), ApprovalSource.Human);
}
