using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Перезапуск от имени администратора (C9) — по щелчку на значке «без прав администратора».
    /// </summary>
    /// <remarks>
    /// <para>
    /// Преемник запускается с <c>--await-exit</c>, как после обновления и стирания: замок
    /// единственного экземпляра держится до конца этого процесса, и без ожидания повышенный
    /// процесс отдал бы запрос живому владельцу и вышел — человек остался бы без окна.
    /// </para>
    /// <para>
    /// Набранный текст едет файлом (<c>--prompt-file</c>, его преемник удаляет сразу после
    /// чтения), а не аргументом: в командной строке его видел бы любой процесс на машине.
    /// </para>
    /// </remarks>
    public partial class MainWindow
    {
        /// <summary>Win32 ERROR_CANCELLED: человек нажал «Нет» в окне UAC.</summary>
        internal const int UacCancelled = 1223;

        private bool _elevationRestart;

        private async void Warn_Click(object sender, RoutedEventArgs e) => await RestartElevatedAsync();

        private async Task RestartElevatedAsync()
        {
            if (_services is null || Exit.Exiting || Environment.ProcessPath is not { Length: > 0 } exe)
            {
                return;
            }

            var running = Turns.Count;
            var text = Loc.Get("S.Elevation.Text");
            if (running > 0)
            {
                text += "\n\n" + Loc.Format("S.Elevation.StopsTurns", running);
            }

            var go = await ShowNoticeAsync(
                Loc.Get("S.Elevation.Title"),
                text,
                Loc.Get("S.Elevation.Restart"),
                Loc.Get("S.Common.Cancel"),
                running > 0 ? NoticeTone.Warning : NoticeTone.Info);
            if (!go)
            {
                return;
            }

            PersistCurrent();
            var promptFile = WriteDraftFile(MessageTextBox.Text);
            var chatId = _session.Messages.Count > 0 ? _session.Id : null;

            var start = new ProcessStartInfo { FileName = exe, UseShellExecute = true, Verb = "runas" };
            foreach (var argument in ElevationArguments(Environment.ProcessId, chatId, promptFile))
            {
                start.ArgumentList.Add(argument);
            }

            try
            {
                using var process = Process.Start(start);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == UacCancelled)
            {
                // Отказ в окне UAC — тоже ответ: остаёмся как были, без сообщений.
                DeleteQuietly(promptFile);
                return;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                DeleteQuietly(promptFile);
                await ShowNoticeAsync(
                    Loc.Get("S.Elevation.Title"),
                    Loc.Get("S.Elevation.Failed"),
                    Loc.Get("S.Common.Close"),
                    secondary: null);
                return;
            }

            CancelAllTurns();
            _elevationRestart = true;
            RequestExit();
        }

        /// <summary>Аргументы преемника: дождаться этого процесса, открыть чат, вернуть набранное.</summary>
        internal static IReadOnlyList<string> ElevationArguments(int pid, string? chatId, string? promptFile)
        {
            var arguments = new List<string> { "--await-exit", pid.ToString(CultureInfo.InvariantCulture) };
            if (StartupArgs.IsChatId(chatId))
            {
                arguments.Add("--open-chat");
                arguments.Add(chatId!);
            }

            if (!string.IsNullOrEmpty(promptFile))
            {
                arguments.Add("--prompt-file");
                arguments.Add(promptFile);
            }

            return arguments;
        }

        /// <summary>Набранный текст — во временный файл для преемника. Null — нечего или не вышло.</summary>
        internal static string? WriteDraftFile(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                var path = Path.Combine(Path.GetTempPath(), "amarin-draft-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(path, text);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void DeleteQuietly(string? path)
        {
            if (path is null)
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Временная папка: не удалилось сейчас — уберёт система.
            }
        }
    }
}
