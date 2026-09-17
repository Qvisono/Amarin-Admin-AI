namespace Amarin.Core;

internal sealed class AgentRunContext
{
    public required ToolCallRecord Call { get; init; }

    public required ChatDisplayMessage Assistant { get; init; }

    public required IChatTurnObserver Observer { get; init; }

    /// <summary>
    /// Чат, чей ход вызвал инструмент. Отсюда его узнаёт очередь подтверждений: с несколькими
    /// одновременными ходами вопрос обязан знать, из какого разговора он пришёл.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Уровень агента, названный человеком словом (<c>/agent-fast</c> и соседние команды).
    /// </summary>
    /// <remarks>
    /// Не через аргументы инструмента: их пишет модель, и ключ, которым можно продиктовать
    /// уровень, вернул бы ей выбор исполнителя, ради отъёма которого всё и делалось. Пусто —
    /// обычный вызов, и уровень решает <see cref="AgentTierRouter"/>.
    /// </remarks>
    public string? ForcedTier { get; init; }
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

    /// <summary>
    /// Записывает проверку SynGuard на сообщение, а не на строку инструмента.
    /// </summary>
    /// <remarks>
    /// На сообщение — потому что защита работает поверх всей работы хода, а не внутри одного
    /// вызова: агент может запустить десяток раундов, и десять строк «Guard» в разбивке были бы
    /// шумом вместо ответа на вопрос «сколько стоила защита».
    /// </remarks>
    public static void ChargeGuard(VeniceCost cost)
    {
        if (CurrentContext.Value is not { } context)
        {
            return;
        }

        lock (ChargeGate)
        {
            context.Assistant.GuardCost = (context.Assistant.GuardCost ?? VeniceCost.Zero).Add(cost);
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
