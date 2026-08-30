namespace Amarin.Core;

internal sealed class AgentSlotLimiter
{
    public const int MaxAgents = 4;

    private int _active;

    public int Active => Volatile.Read(ref _active);

    public bool TryEnter()
    {
        while (true)
        {
            var current = Volatile.Read(ref _active);
            if (current >= MaxAgents)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _active, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    public void Exit()
    {
        Interlocked.Decrement(ref _active);
    }
}
