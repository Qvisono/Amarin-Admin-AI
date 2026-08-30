using Amarin.Tools;

namespace Amarin.Core;

internal interface IAgentHost
{
    Task<ToolResult> RunAsync(string prompt, string complexity, CancellationToken cancellationToken);
}
