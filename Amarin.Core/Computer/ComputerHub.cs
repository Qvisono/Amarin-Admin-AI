namespace Amarin.Core;

/// <summary>
/// Экран и ввод этого ПК вместе с плашкой — для агента. Собирается в корне композиции, а плашку
/// ставит окно, когда оно готово её показывать.
/// </summary>
/// <remarks>
/// Без плашки управления нет вовсе (<see cref="Access"/> отдаёт null): у человека не было бы ни
/// «Стоп», ни отсчёта перед началом без него, ни вопроса о нажатии, а окно программы не ушло бы
/// с дороги. Так же и в тестах, где окна нет, — агент просто не получает <c>computer</c>.
/// </remarks>
internal sealed class ComputerHub(IComputer port)
{
    public IComputer Port { get; } = port;

    /// <summary>Плашка и подсветка. Ставит окно; null — его ещё нет.</summary>
    public IComputerStage? Stage { get; set; }

    public ComputerAccess? Access() => Stage is { } stage ? new ComputerAccess(Port, stage) : null;

    /// <summary>
    /// Разрешено ли агенту управление мышью: галочка человека и нет запрета организации (1.37.0).
    /// </summary>
    /// <remarks>
    /// Одно правило на все места, где решается доступ (выдача инструмента, правило маршрутизатору,
    /// промпт чата, расписание): прочитай одно из них галочку напрямую — и запрет политики обошли бы.
    /// </remarks>
    public static bool Allowed(AppSettings settings) =>
        settings.ComputerControl && !OrganizationPolicy.Current.DisableComputerControl;
}
