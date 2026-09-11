namespace Amarin.Core;

/// <summary>Помнит ли агент предыдущие запросы или начинает каждый с чистого листа.</summary>
public enum SessionMode
{
    Continuous,
    Isolated
}
