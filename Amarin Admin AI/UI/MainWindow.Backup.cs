using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Автоматические резервные копии (F1): проверка раз в час и копия, когда пора.
    /// </summary>
    /// <remarks>
    /// Первая проверка — через две минуты после старта, а не сразу: запуск и так читает с диска
    /// всё, и архив всего профиля поверх него замедлил бы первые минуты работы. Сама копия пишется
    /// на рабочем потоке; итог ложится в настройки уже на потоке интерфейса.
    /// </remarks>
    public partial class MainWindow
    {
        private readonly DispatcherTimer _backupTimer = new() { Interval = TimeSpan.FromMinutes(2) };
        private bool _backupRunning;

        private void StartBackups()
        {
            _backupTimer.Tick += (_, _) =>
            {
                _backupTimer.Interval = TimeSpan.FromHours(1);
                if (Backups.IsDue(_services?.Settings.Backup, DateTime.Now))
                {
                    Detached.Run(RunBackupAsync(manual: false), "backup_scheduled");
                }

                // Хранение чатов (F3) — на том же таймере: при старте и дальше раз в сутки.
                ApplyRetentionIfDue();
            };
            _backupTimer.Start();
            Closed += (_, _) => _backupTimer.Stop();
        }

        /// <param name="manual">«Сделать сейчас»: копия делается и при выключенном расписании.</param>
        internal async Task RunBackupAsync(bool manual)
        {
            if (_services is not { } services || _backupRunning)
            {
                return;
            }

            var backup = services.Settings.Backup ??= new BackupSettings();
            if (!manual && !Backups.IsDue(backup, DateTime.Now))
            {
                return;
            }

            _backupRunning = true;
            BackupPanel.ShowStatus(backup, running: true);
            var root = AppPaths.Root;
            var profileId = ActiveProfileId;
            var folder = Backups.FolderOf(backup);
            var keep = backup.Keep;
            var now = DateTime.Now;
            try
            {
                var result = await Task.Run(() => Backups.Run(root, profileId, folder, keep, now, CancellationToken.None))
                    .ConfigureAwait(true);
                backup.LastAt = now;
                backup.LastFile = result.Path;
                backup.LastBytes = result.Bytes;
                backup.LastError = null;
                backup.LastErrorAt = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                backup.LastError = ex.Message;
                backup.LastErrorAt = now;
                NotifyStatus(Loc.Format("S.Backup.FailedNotice", ex.Message));
            }
            finally
            {
                _backupRunning = false;
                services.SettingsStore.Save(services.Settings);
                BackupPanel.ShowStatus(backup, running: false);
            }
        }
    }
}
