using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>Цель чата (C10): выбор машины в панели композера и страница «Подключения».</summary>
    public partial class MainWindow
    {
        private void RefreshTargetPicker()
        {
            if (_services is null)
            {
                return;
            }

            ChatTargetPicker.Show(_services.Machines.Load(), _session.TargetMachineId);
        }

        /// <summary>
        /// Смена цели. Посреди хода — нельзя: половина его команд ушла бы на одну машину, половина
        /// на другую, а модель считала бы, что всё там же.
        /// </summary>
        private void OnTargetPicked(string? machineId)
        {
            if (_services is null)
            {
                return;
            }

            if (IsBusy(_session.Id))
            {
                Detached.Run(
                    ShowNoticeAsync(Loc.Get("S.Remote.BusyTitle"), Loc.Get("S.Remote.BusyText"), Loc.Get("S.Common.Close"), null),
                    "target_busy");
                RefreshTargetPicker();
                return;
            }

            _session.TargetMachineId = machineId;
            if (_session.Messages.Count > 0)
            {
                PersistCurrent();
            }

            RefreshTargetPicker();
        }

        private void NavConnections_Checked(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            ConnectionsPage.Attach(_services);
            ConnectionsPage.Load();
        }

        /// <summary>Список машин изменился — выбор в чате перечитывает его.</summary>
        private void OnMachinesChanged() => RefreshTargetPicker();
    }
}
