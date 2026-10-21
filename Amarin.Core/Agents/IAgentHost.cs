using Amarin.Tools;

namespace Amarin.Core;

internal interface IAgentHost
{
    /// <param name="notes">
    /// Пометки модели чата для маршрутизатора уровня. Null или пусто — обычное дело: сказать
    /// сверх задания бывает нечего.
    /// </param>
    Task<ToolResult> RunAsync(string prompt, string? notes, CancellationToken cancellationToken);

    /// <summary>Сообщение чата агенту этого ответа: агент продолжает переписку и отвечает отчётом.</summary>
    Task<ToolResult> ContinueAsync(string agentId, string message, CancellationToken cancellationToken);

    /// <summary>Задачу отказавшего агента берёт модель уровнем выше.</summary>
    Task<ToolResult> EscalateAsync(string agentId, CancellationToken cancellationToken);
}
