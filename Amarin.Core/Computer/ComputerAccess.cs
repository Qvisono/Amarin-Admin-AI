namespace Amarin.Core;

/// <summary>
/// Чем агенту управлять компьютером: экран и ввод этого ПК и плашка с подсветкой.
/// </summary>
/// <param name="Stage">Плашка и подсветка; null — их нет (тесты), и «Показать» отказывает.</param>
/// <param name="Lease">Кто держит мышь; null — общий на процесс.</param>
internal sealed record ComputerAccess(IComputer Port, IComputerStage? Stage, ComputerLease? Lease = null);
