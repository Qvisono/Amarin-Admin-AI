using Amarin.Tools;

namespace Amarin.Core;

internal interface IAgentHost
{
    /// <param name="notes">
    /// Пометки модели чата для маршрутизатора уровня. Null или пусто — обычное дело: сказать
    /// сверх задания бывает нечего.
    /// </param>
    Task<ToolResult> RunAsync(string prompt, string? notes, CancellationToken cancellationToken);
}
