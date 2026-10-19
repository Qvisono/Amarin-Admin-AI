using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Управление компьютером мышью (1.37.0): окно ставит плашку, без которой агент мышь не получает.
    /// </summary>
    /// <remarks>
    /// Только в настоящей программе (<see cref="IsRealApp"/>): оконные тесты поднимают это же окно,
    /// и плашка встала бы поверх того, чем занят человек, а агент в тесте получил бы его мышь.
    /// </remarks>
    public partial class MainWindow
    {
        private ComputerStage? _computerStage;

        private void AttachComputerStage(AppServices? previous, AppServices services)
        {
            if (previous?.Computer is { } old)
            {
                old.Stage = null;
            }

            _computerStage?.Detach();
            _computerStage = null;
            if (!IsRealApp || services.Computer is not { } hub)
            {
                return;
            }

            _computerStage = new ComputerStage(this, services.Confirmations);
            hub.Stage = _computerStage;
        }

        private void DetachComputerStage()
        {
            if (_services?.Computer is { } hub)
            {
                hub.Stage = null;
            }

            _computerStage?.Detach();
            _computerStage = null;
        }
    }
}
