using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Проверка обновлений на странице Data &amp; Info: кнопка и автопроверка при запуске.
    /// Скачиванием и установкой приложение не занимается — найденный релиз открывается
    /// в браузере, решение остаётся за пользователем.
    /// </summary>
    public partial class MainWindow
    {
        private string? _releasePageUrl;
        private bool _updateCheckRunning;
        private ReleaseInfo? _latestRelease;
        private UpdatePlan? _pendingUpdate;
        private CancellationTokenSource? _updateDownload;

        private static Version CurrentVersion =>
            Version.TryParse(RuntimeContext.AppVersion, out var version) ? version : new Version(1, 0, 0);

        private void LoadUpdatesUi()
        {
            if (_services is null)
            {
                return;
            }

            AutoUpdateToggle.IsChecked = _services.Settings.AutoCheckUpdates;
            ShowUpdateStatus($"Установлена версия {RuntimeContext.AppVersionDisplay}", accent: false);
            OpenReleaseButton.Visibility = Visibility.Collapsed;
            UpdateNowButton.Visibility = Visibility.Collapsed;
        }

        private void AutoUpdateToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            _services.Settings.AutoCheckUpdates = AutoUpdateToggle.IsChecked == true;
            _services.SettingsStore.Save(_services.Settings);
        }

        private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => _ = CheckUpdatesAsync(manual: true);

        private void OpenReleaseButton_Click(object sender, RoutedEventArgs e)
        {
            var url = _releasePageUrl ?? UpdateChecker.ReleasesPageUrl;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                ShowUpdateStatus("Не удалось открыть браузер: " + ex.Message, accent: false);
            }
        }

        private void UpdateNowButton_Click(object sender, RoutedEventArgs e)
        {
            // Во время загрузки та же кнопка её отменяет.
            if (_updateDownload is { } running)
            {
                running.Cancel();
                return;
            }

            if (_services is null || _latestRelease is null)
            {
                return;
            }

            // Тоже про всю программу: после установки она перезапустится и оборвёт все ходы.
            if (AnyTurnRunning)
            {
                ShowUpdateStatus("Дождитесь окончания ответов — программа перезапустится.", accent: false);
                return;
            }

            if (!UpdateInstaller.TryPlan(_latestRelease, Environment.ProcessPath, out var plan, out var error))
            {
                ShowUpdateStatus(error, accent: false);
                OpenReleaseButton.Visibility = Visibility.Visible;
                return;
            }

            _pendingUpdate = plan;
            UpdateConfirmTitle.Text = $"Обновить до версии {_latestRelease.Version}";
            UpdateConfirmText.Text =
                $"Установлена {RuntimeContext.AppVersionDisplay}, доступна {_latestRelease.Version}. " +
                $"Будет скачано {Megabytes(plan.Asset.Size)} и записано на место текущей программы.";
            UpdateConfirmFile.Text = plan.Asset.Name;
            UpdateConfirmFolder.Text = plan.Folder;
            UpdateConfirmOverlay.Visibility = Visibility.Visible;
            Chat.IsHitTestVisible = false;
        }

        private void UpdateConfirmCancelButton_Click(object sender, RoutedEventArgs e) => CloseUpdateConfirm();

        private void UpdateConfirmApplyButton_Click(object sender, RoutedEventArgs e)
        {
            var plan = _pendingUpdate;
            CloseUpdateConfirm();
            if (plan is not null)
            {
                _ = InstallUpdateAsync(plan);
            }
        }

        private void CloseUpdateConfirm()
        {
            UpdateConfirmOverlay.Visibility = Visibility.Collapsed;
            Chat.IsHitTestVisible = ConfirmationOverlay.Visibility != Visibility.Visible;
        }

        /// <summary>
        /// Скачивает сборку, сверяет её и подменяет ею себя. Отмена возможна до самой подмены;
        /// после неё остаётся только перезапуск — программа уже лежит на диске новой версией.
        /// </summary>
        private async Task InstallUpdateAsync(UpdatePlan plan)
        {
            if (_services is null || _updateDownload is not null)
            {
                return;
            }

            using var cancellation = new CancellationTokenSource();
            _updateDownload = cancellation;
            UpdateNowButton.Content = "Отменить";
            CheckUpdatesButton.IsEnabled = false;
            ShowProgress(0);
            ShowUpdateStatus("Скачивание 0 %", accent: false);

            try
            {
                var progress = new Progress<double>(share =>
                {
                    ShowProgress(share);
                    ShowUpdateStatus($"Скачивание {share * 100:0} %", accent: false);
                });

                var (result, file) = await UpdateInstaller.DownloadAsync(
                    plan,
                    _services.DownloadHttp,
                    progress,
                    cancellation.Token);

                if (!result.Ok || file is null)
                {
                    ShowUpdateStatus("Обновление не состоялось: " + result.Error, accent: false);
                    OpenReleaseButton.Visibility = Visibility.Visible;
                    return;
                }

                ShowUpdateStatus("Устанавливаем…", accent: false);

                // Чат сохраняем до подмены: дальше процесс уже завершается.
                PersistCurrent();

                var swap = UpdateInstaller.Swap(file, plan.ExePath);
                if (!swap.Ok)
                {
                    ShowUpdateStatus("Обновление не состоялось: " + swap.Error, accent: false);
                    OpenReleaseButton.Visibility = Visibility.Visible;
                    return;
                }

                Restart(plan.ExePath);
            }
            catch (OperationCanceledException)
            {
                ShowUpdateStatus("Загрузка отменена.", accent: false);
            }
            finally
            {
                _updateDownload = null;
                UpdateNowButton.Content = "Обновить";
                CheckUpdatesButton.IsEnabled = true;
                UpdateProgress.Visibility = Visibility.Collapsed;
            }
        }

        private void Restart(string exePath)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                ShowUpdateStatus(
                    "Новая версия установлена, но запустить её не вышло: " + ex.Message,
                    accent: false);
                return;
            }

            Application.Current?.Shutdown();
        }

        private void ShowProgress(double share)
        {
            var done = Math.Clamp(share, 0, 1);
            UpdateProgress.Visibility = Visibility.Visible;
            UpdateProgressDone.Width = new GridLength(done, GridUnitType.Star);
            UpdateProgressLeft.Width = new GridLength(1 - done, GridUnitType.Star);
        }

        private static string Megabytes(long bytes) =>
            bytes <= 0 ? "неизвестно сколько" : $"{bytes / 1024.0 / 1024.0:0.#} МБ";

        /// <summary>Автопроверка при запуске: молча, не чаще одного раза в интервал.</summary>
        private void ScheduleAutoUpdateCheck()
        {
            if (_services is null || !_services.Settings.AutoCheckUpdates)
            {
                return;
            }

            var last = _services.Settings.LastUpdateCheckUtc;
            if (last is not null && DateTime.UtcNow - last.Value < UpdateChecker.AutoCheckInterval)
            {
                return;
            }

            _ = CheckUpdatesAsync(manual: false);
        }

        private async Task CheckUpdatesAsync(bool manual)
        {
            if (_services is null || _updateCheckRunning)
            {
                return;
            }

            _updateCheckRunning = true;
            CheckUpdatesButton.IsEnabled = false;
            if (manual)
            {
                OpenReleaseButton.Visibility = Visibility.Collapsed;
                ShowUpdateStatus("Проверяем…", accent: false);
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var result = await UpdateChecker.CheckAsync(CurrentVersion, timeout.Token);

                _services.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
                _services.SettingsStore.Save(_services.Settings);

                ApplyUpdateResult(result, manual);
            }
            finally
            {
                _updateCheckRunning = false;
                CheckUpdatesButton.IsEnabled = true;
            }
        }

        private void ApplyUpdateResult(UpdateCheckResult result, bool manual)
        {
            if (!result.Ok || result.Latest is null)
            {
                if (manual)
                {
                    ShowUpdateStatus(result.Error ?? "Проверить не удалось", accent: false);
                }

                return;
            }

            if (!result.UpdateAvailable)
            {
                if (manual)
                {
                    ShowUpdateStatus(
                        $"Установлена последняя версия — {RuntimeContext.AppVersionDisplay}",
                        accent: false);
                }

                return;
            }

            _latestRelease = result.Latest;
            _releasePageUrl = result.Latest.PageUrl;
            OpenReleaseButton.Visibility = Visibility.Visible;
            UpdateNowButton.Visibility = result.Latest.WindowsBuild is null
                ? Visibility.Collapsed
                : Visibility.Visible;
            ShowUpdateStatus(
                $"Доступна версия {result.Latest.Version} — установлена {RuntimeContext.AppVersionDisplay}",
                accent: true);

            // Метка у номера версии в боковой колонке настроек: единственное место, где
            // о новой версии видно, не открывая эту страницу.
            SettingsVersionText.Text = $"v{RuntimeContext.AppVersionDisplay} · есть обновление";
            SettingsVersionText.SetResourceReference(TextBlock.ForegroundProperty, "Accent.Fill");
        }

        private void ShowUpdateStatus(string text, bool accent)
        {
            UpdateStatusText.Text = text;
            if (accent)
            {
                UpdateStatusText.SetResourceReference(TextBlock.ForegroundProperty, "Accent.Fill");
            }
            else
            {
                UpdateStatusText.ClearValue(TextBlock.ForegroundProperty);
            }
        }
    }
}
