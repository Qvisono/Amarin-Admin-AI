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

    /// <summary>
    /// Bills whatever the running tool just spent to that tool's own row. The scope is pushed
    /// around every tool call and flows through the awaits inside it, so a client deep in the
    /// call stack can attribute a charge without anything being threaded through by hand —
    /// which matters because tool calls in a round run in parallel.
    /// </summary>
    public static void Charge(VeniceCost cost)
    {
        if (CurrentContext.Value is not { } context)
        {
            return;
        }

        lock (ChargeGate)
        {
            context.Call.Cost = (context.Call.Cost ?? VeniceCost.Zero).Add(cost);
        }
    }

    private static readonly Lock ChargeGate = new();

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
