using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>Цель чата (C10): выбор машины в панели композера и вкладка «Компьютеры» в Automation.</summary>
    public partial class MainWindow
    {
        private void RefreshTargetPicker()
        {
            // Та же точка «открыли другой чат» — и для отметки «только чтение» (D9).
            UpdateReadOnlyChip();
            UpdateProfileChip();
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

        /// <summary>«Управлять…» в выборе цели: настройки сразу на вкладке «Компьютеры».</summary>
        private void OpenMachinesSettings()
        {
            OpenSettingsPage(NavAutomation);
            AutomationPage.ShowMachinesTab();
        }

        /// <summary>Список машин изменился — выбор в чате перечитывает его.</summary>
        private void OnMachinesChanged() => RefreshTargetPicker();
    }
}
