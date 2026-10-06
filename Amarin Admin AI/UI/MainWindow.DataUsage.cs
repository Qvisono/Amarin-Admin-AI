using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Разбивка «занято на диске» на странице Data Controls.
    /// </summary>
    /// <remarks>
    /// Страница «Файлы приложения» до сих пор умела только открыть папку — дальше человек считал
    /// сам. Здесь тот же вопрос отвечается на месте и по категориям, чтобы было видно, что именно
    /// разрослось: история переписки, снимки для отката или журналы сбоев.
    /// </remarks>
    public partial class MainWindow
    {
        /// <summary>Строка таблицы. Ширины — готовые <see cref="GridLength"/>: полоска доли
        /// рисуется двумя звёздными колонками, как полоса загрузки обновления.</summary>
        private sealed record UsageRow(
            string Label,
            string Size,
            string Files,
            GridLength Share,
            GridLength Rest,
            Thickness Indent);

        private static readonly Thickness RowIndent = new(0, 0, 0, 7);

        private CancellationTokenSource? _usageScan;

        /// <summary>
        /// Пересчитывает разбивку. Обход диска уходит в <see cref="Task.Run(Action)"/>: на большой
        /// истории он занимает заметное время, а считается это на странице настроек, где рядом
        /// продолжают идти ответы.
        /// </summary>
        private async Task RefreshDataUsageAsync()
        {
            // Разбивку некуда показать, пока страница «Данные» не построена: построенная, она посчитает её
            // при заходе в «Хранение и очистку».
            if (_services is null || BuiltPage<SettingsDataPage>() is null)
            {
                return;
            }

            // Предыдущий обход отменяем: страницу открывают и закрывают быстрее, чем он успевает.
            // Отменяем, но не освобождаем: его токеном ещё пользуется незавершённая задача, и
            // Dispose здесь дал бы ей ObjectDisposedException вместо обычной отмены. Освобождает
            // тот обход, которому источник принадлежит, — в своём finally.
            _usageScan?.Cancel();
            var scan = new CancellationTokenSource();
            _usageScan = scan;

            DataPage.UsageSummaryText.Text = Loc.Get("S.Data.Usage.Counting");
            DataPage.UsageRefreshButton.IsEnabled = false;

            var appRoot = AppPaths.Root;
            var localRoot = CrashLog.DefaultDirectory();
            var exePath = Environment.ProcessPath;

            try
            {
                var report = await Task.Run(
                    () => DataUsage.Measure(appRoot, localRoot, exePath, scan.Token),
                    scan.Token);

                if (scan.IsCancellationRequested || !ReferenceEquals(_usageScan, scan))
                {
                    return;
                }

                ShowDataUsage(report);
            }
            catch (OperationCanceledException)
            {
                // Обычный конец: страницу закрыли или нажали «пересчитать» ещё раз.
            }
            finally
            {
                if (ReferenceEquals(_usageScan, scan))
                {
                    _usageScan = null;
                    DataPage.UsageRefreshButton.IsEnabled = true;
                }

                scan.Dispose();
            }
        }

        private void ShowDataUsage(UsageReport report)
        {
            if (report.TotalBytes <= 0)
            {
                DataPage.UsageSummaryText.Text = Loc.Get("S.Data.Usage.Empty");
                DataPage.UsageList.ItemsSource = null;
                DataPage.UsageAppText.Text = "";
                return;
            }

            DataPage.UsageSummaryText.Text = AttachmentTypes.FormatSize(report.TotalBytes);

            var rows = new List<UsageRow>();
            foreach (var entry in report.Entries)
            {
                rows.Add(BuildUsageRow(
                    Loc.Get(entry.LabelKey),
                    entry.Bytes,
                    Loc.Format("S.Data.Usage.Files", entry.Files),
                    report.TotalBytes,
                    RowIndent));
            }

            DataPage.UsageList.ItemsSource = rows;
            DataPage.UsageAppText.Text = report.AppBytes > 0
                ? $"{Loc.Get(DataUsage.AppKey)} - {AttachmentTypes.FormatSize(report.AppBytes)}"
                : "";
        }

        private static UsageRow BuildUsageRow(
            string label, long bytes, string files, long total, Thickness indent)
        {
            var share = total <= 0 ? 0 : Math.Clamp((double)bytes / total, 0, 1);
            return new UsageRow(
                label,
                AttachmentTypes.FormatSize(bytes),
                files,
                new GridLength(share, GridUnitType.Star),
                new GridLength(1 - share, GridUnitType.Star),
                indent);
        }

        private void UsageRefreshButton_Click(object sender, RoutedEventArgs e) =>
            Detached.Run(RefreshDataUsageAsync(), "refresh_data_usage");

        /// <summary>Человек перешёл на страницу «Данные»: наполняем подстраницы и значения строк «›».</summary>
        private void NavData_Checked(object sender, RoutedEventArgs e)
        {
            if (_services is not null)
            {
                DataPage.BackupPanel.RunNow ??= () => RunBackupAsync(manual: true);
                DataPage.BackupPanel.Load(_services);
                DataPage.BackupPanel.ShowStatus(_services.Settings.Backup ?? new BackupSettings(), _backupRunning);
                WireDataCare();
                DataPage.DataCarePanel.Load(_services);
                RefreshDataLinks();
            }
        }

        /// <summary>Значения строк «Резервные копии ›» и «Хранение и очистка ›».</summary>
        private void RefreshDataLinks()
        {
            if (_services is null)
            {
                return;
            }

            DataPage.BackupLinkRow.Tag = BackupBlock.Summary(_services.Settings.Backup);
            DataPage.CareLinkRow.Tag = DataCareBlock.Summary(_services.Settings.Retention);
        }
    }
}
