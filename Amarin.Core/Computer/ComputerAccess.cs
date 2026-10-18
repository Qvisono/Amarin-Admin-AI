namespace Amarin.Core;

/// <summary>
/// Чем агенту управлять компьютером: экран и ввод этого ПК и плашка с подсветкой.
/// </summary>
/// <param name="Stage">Плашка и подсветка; null — их нет (тесты), и «Показать» отказывает.</param>
/// <param name="Lease">Кто держит мышь; null — общий на процесс.</param>
internal sealed record ComputerAccess(IComputer Port, IComputerStage? Stage, ComputerLease? Lease = null);

/// <summary>Сколько потратили операторы одного прогона агента — для его счёта.</summary>
/// <remarks>
/// Оператор платит своим клиентом, а счёт ходу отдаёт агент (<c>NestedAgent.Cost</c>): без этой
/// суммы деньги оператора не попали бы ни в ход, ни в разбивку под сообщением.
/// </remarks>
internal sealed class ComputerSpend
{
    private readonly Lock _gate = new();
    private VeniceCost _total = VeniceCost.Zero;

    public VeniceCost Total
    {
        get
        {
            lock (_gate)
            {
                return _total;
            }
        }
    }

    public void Add(VeniceCost cost)
    {
        lock (_gate)
        {
            _total = _total.Add(cost);
        }
    }
}
