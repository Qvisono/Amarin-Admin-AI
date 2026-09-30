using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// «Сообщить об ошибке» и «Открыть папку журналов» со страницы Info (H5). То же, что в окне
    /// аварии, но без аварии: в отчёт идёт последняя запись журнала сбоев, если она есть.
    /// </summary>
    public partial class MainWindow
    {
        internal async Task ReportBugAsync()
        {
            // Журнал читается на рабочем потоке: он бывает в сотни килобайт.
            var report = await Task.Run(() =>
            {
                try
                {
                    var path = CrashLog.Default.FilePath;
                    return File.Exists(path) ? BugReport.LastCrash(File.ReadAllText(path)) : null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            });

            var clean = report is null ? null : BugReport.SanitizeHere(report);
            var link = BugReport.Link(
                clean is null ? "" : BugReport.TitleFrom(clean),
                BugReport.Body(RuntimeContext.AppVersion, clean));

            if (link.ClipboardBody is { } body)
            {
                try
                {
                    Clipboard.SetText(body);
                }
                catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
                {
                    await ShowNoticeAsync(Loc.Get("S.BugReport.Title"), Loc.Get("S.Crash.ReportNoClipboard"), Loc.Get("S.Common.Close"), null);
                    return;
                }
            }

            OpenExternalLink(link.Url);
            if (link.ClipboardBody is not null)
            {
                await ShowNoticeAsync(Loc.Get("S.BugReport.Title"), Loc.Get("S.Crash.ReportPaste"), Loc.Get("S.Common.Close"), null, NoticeTone.Info);
            }
        }

        internal void OpenLogsFolder()
        {
            var folder = CrashLog.DefaultDirectory();
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Detached.Run(
                    ShowNoticeAsync(Loc.Get("S.Links.OpenFailed"), Loc.Format("S.Crash.LogPath", folder), Loc.Get("S.Common.Close"), null),
                    "open_logs_failed");
            }
        }
    }
}
