using System.Diagnostics;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>Сообщение о повреждённых файлах данных, найденных при запуске.</summary>
    public partial class MainWindow
    {
        /// <summary>
        /// Показывает, что настройки, профили или ключи были повреждены и чем их заменили.
        /// </summary>
        /// <remarks>
        /// Файлы читаются до окна (<see cref="GuardedJsonFile"/>), а показать некому — поэтому
        /// находки копятся в <see cref="DataFileIncidents"/> и выходят сюда после первого кадра.
        /// Молчать нельзя: до 1.28.0 повреждённые настройки тихо становились заводскими.
        /// </remarks>
        internal async Task ShowDataFileIncidentsAsync()
        {
            var incidents = DataFileIncidents.Drain();
            if (incidents.Count == 0)
            {
                return;
            }

            var reveal = await ShowNoticeAsync(
                Loc.Get("S.DataSafety.Title"),
                DataFileIncidentText.Describe(incidents),
                Loc.Get("S.DataSafety.Show"),
                Loc.Get("S.Common.Close"),
                NoticeTone.Warning);

            var kept = incidents.Select(incident => incident.BrokenCopy).FirstOrDefault(path => path.Length > 0);
            if (reveal && kept is not null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{kept}\"") { UseShellExecute = true });
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Проводник не открылся — путь уже назван в тексте сообщения.
                }
            }
        }
    }
}
