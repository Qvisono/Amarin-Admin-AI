using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Хранение старых чатов и очистка места (F3).
    /// </summary>
    /// <remarks>
    /// Правило хранения прогоняется на таймере резервных копий: через две минуты после старта и
    /// дальше не чаще раза в сутки. Открытый чат и чаты с идущим ходом не трогаются никогда —
    /// удалить переписку из-под отвечающей модели значило бы потерять ответ.
    /// </remarks>
    public partial class MainWindow
    {
        private DateTime _retentionCheckedAt = DateTime.MinValue;
        private bool _dataCareWired;

        private void WireDataCare()
        {
            if (_dataCareWired)
            {
                return;
            }

            _dataCareWired = true;
            DataPage.DataCarePanel.Confirm = (title, text, yes) =>
                ShowNoticeAsync(title, text, yes, Loc.Get("S.Common.Cancel"), NoticeTone.Danger);
            DataPage.DataCarePanel.OpenChat = id =>
            {
                SettingsOverlay.Visibility = System.Windows.Visibility.Collapsed;
                OpenChat(id);
            };
            DataPage.DataCarePanel.BusyChats = BusyChatIds;
            DataPage.DataCarePanel.Cleaned = () => Detached.Run(RefreshDataUsageAsync(), "refresh_data_usage");
            DataPage.DataCarePanel.Changed += RefreshDataLinks;
            DataPage.BackupPanel.Changed += RefreshDataLinks;
        }

        /// <summary>Открытый чат и чаты, где идёт ход.</summary>
        private IReadOnlySet<string> BusyChatIds()
        {
            var busy = new HashSet<string>(Turns.RunningChatIds, StringComparer.Ordinal) { _session.Id };
            return busy;
        }

        private void ApplyRetentionIfDue()
        {
            if (_services is not { } services || DateTime.Now - _retentionCheckedAt < TimeSpan.FromDays(1))
            {
                return;
            }

            _retentionCheckedAt = DateTime.Now;
            var retention = services.Settings.Retention;
            var picked = DataCleanup.PickForRetention(
                services.ChatStore.List(),
                services.Organizer.Snapshot(),
                retention,
                DateTime.Now,
                BusyChatIds());
            if (picked.Count == 0)
            {
                return;
            }

            if (retention.Mode == RetentionMode.Archive)
            {
                services.Organizer.SetArchived(picked, archived: true);
            }
            else
            {
                foreach (var id in picked)
                {
                    services.ChatStore.Delete(id);
                }
            }

            RefreshChatList();
            ShowTransientNotice(_session.Id, Loc.Format(
                retention.Mode == RetentionMode.Archive ? "S.Care.Archived" : "S.Care.Deleted",
                picked.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }
}
