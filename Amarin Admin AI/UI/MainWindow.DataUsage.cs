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

        /// <summary>Отступ подстроки: вложения — часть чатов, а не соседняя категория.</summary>
        private static readonly Thickness SubRowIndent = new(14, 0, 0, 7);

        private static readonly Thickness RowIndent = new(0, 0, 0, 7);

        private CancellationTokenSource? _usageScan;

        /// <summary>
        /// Пересчитывает разбивку. Обход диска уходит в <see cref="Task.Run(Action)"/>: на большой
        /// истории он занимает заметное время, а считается это на странице настроек, где рядом
        /// продолжают идти ответы.
        /// </summary>
        private async Task RefreshDataUsageAsync()
        {
            if (_services is null)
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

            UsageSummaryText.Text = Loc.Get("S.Data.Usage.Counting");
            UsageRefreshButton.IsEnabled = false;

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
                    UsageRefreshButton.IsEnabled = true;
                }

                scan.Dispose();
            }
        }

        private void ShowDataUsage(UsageReport report)
        {
            if (report.TotalBytes <= 0)
            {
                UsageSummaryText.Text = Loc.Get("S.Data.Usage.Empty");
                UsageList.ItemsSource = null;
                UsageAppText.Text = "";
                return;
            }

            UsageSummaryText.Text = AttachmentTypes.FormatSize(report.TotalBytes);

            var rows = new List<UsageRow>();
            foreach (var entry in report.Entries)
            {
                rows.Add(BuildUsageRow(
                    Loc.Get(entry.LabelKey),
                    entry.Bytes,
                    Loc.Format("S.Data.Usage.Files", entry.Files),
                    report.TotalBytes,
                    RowIndent));

                // Вложения — не отдельная категория, а из чего состоят чаты: на диске они лежат
                // внутри тех же файлов. Отсюда и подстрока со сдвигом, а не своё слагаемое суммы.
                if (entry.LabelKey == DataUsage.ChatsKey && report.AttachmentBytes > 0)
                {
                    rows.Add(BuildUsageRow(
                        Loc.Get(DataUsage.AttachmentsKey),
                        report.AttachmentBytes,
                        "",
                        report.TotalBytes,
                        SubRowIndent));
                }
            }

            UsageList.ItemsSource = rows;
            UsageAppText.Text = report.AppBytes > 0
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

        /// <summary>
        /// Человек перешёл на страницу «Файлы приложения». Считаем здесь, а не при открытии
        /// настроек: страница запоминается между открытиями, так что вернувшийся на неё увидит
        /// свежий счёт, а остальные за него не платят.
        /// </summary>
        private void NavData_Checked(object sender, RoutedEventArgs e) =>
            Detached.Run(RefreshDataUsageAsync(), "refresh_data_usage");
    }
}
