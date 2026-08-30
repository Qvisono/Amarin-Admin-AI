namespace Amarin.Core;

internal sealed class AgentRunContext
{
    public required ToolCallRecord Call { get; init; }

    public required ChatDisplayMessage Assistant { get; init; }

    public required IChatTurnObserver Observer { get; init; }
}

internal static class AgentRunScope
{
    private static readonly AsyncLocal<AgentRunContext?> CurrentContext = new();

    public static AgentRunContext? Current => CurrentContext.Value;

    public static IDisposable Push(AgentRunContext context)
    {
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new Popper(() => CurrentContext.Value = previous);
    }

    private sealed class Popper(Action restore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                restore();
            }
        }
    }
}
