using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Обновление на странице Data &amp; Info: проверка, загрузка и подмена файла одной кнопкой.
    /// Ничего из этого не происходит само — загрузку начинает человек, подтвердив её в отдельном
    /// окне, а автопроверка только спрашивает GitHub о номере последней версии.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Отказ UAC: человек нажал «Нет». Обычный ответ, а не сбой.</summary>
        private const int ErrorCancelled = 1223;

        private string? _releasePageUrl;
        private bool _updateCheckRunning;
        private ReleaseInfo? _latestRelease;

        /// <summary>
        /// Последний найденный релиз.
        /// </summary>
        /// <remarks>
        /// Открыто наружу ради теста: убедиться, что открытие настроек больше не стирает
        /// находку, можно только подсунув её и открыв страницу.
        /// </remarks>
        internal ReleaseInfo? LatestRelease
        {
            get => _latestRelease;
            set => _latestRelease = value;
        }
        private UpdatePlan? _pendingUpdate;
        private CancellationTokenSource? _updateDownload;

        private static Version CurrentVersion =>
            Version.TryParse(RuntimeContext.AppVersion, out var version) ? version : new Version(1, 0, 0);

        /// <summary>
        /// Приводит строку обновлений в порядок при каждом открытии настроек.
        /// </summary>
        /// <remarks>
        /// Найденный релиз пересказывается заново, а не забывается. Раньше здесь безусловно
        /// гасли обе кнопки: автопроверка при запуске находила новую версию и показывала
        /// «Обновить», человек шёл в настройки — и открытие страницы стирало находку. Обновиться
        /// можно было, только нажав «Проверить» ещё раз, уже внутри открытой страницы.
        /// </remarks>
        internal void LoadUpdatesUi()
        {
            if (_services is not null)
            {
                AutoUpdateToggle.IsChecked = _services.Settings.AutoCheckUpdates;
            }

            if (_latestRelease is { } found && found.Version > UpdateChecker.Normalize(CurrentVersion))
            {
                ShowFoundRelease(found);
                return;
            }

            ShowUpdateStatus(Loc.Format("S.Updates.Installed", RuntimeContext.AppVersion), accent: false);
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
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                ShowUpdateStatus(Loc.Format("S.Updates.BrowserFailed", ex.Message), accent: false);
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
                ShowUpdateStatus(Loc.Get("S.Updates.WaitForTurns"), accent: false);
                return;
            }

            if (!UpdateInstaller.TryPlan(_latestRelease, Environment.ProcessPath, out var plan, out var error))
            {
                ShowUpdateStatus(error, accent: false);
                OpenReleaseButton.Visibility = Visibility.Visible;
                return;
            }

            _pendingUpdate = plan;
            UpdateConfirmTitle.Text = Loc.Format("S.Updates.ConfirmTitle", _latestRelease.Version);
            UpdateConfirmText.Text = Loc.Format(
                "S.Updates.ConfirmText",
                RuntimeContext.AppVersion,
                _latestRelease.Version,
                DownloadSize(plan.Asset.Size));
            UpdateConfirmFile.Text = Path.GetFileName(plan.ExePath);
            UpdateConfirmFolder.Text = plan.Folder;
            UpdateConfirmNote.Text = plan.NeedsElevation
                ? Loc.Get("S.Updates.KeepsFileName") + " " + Loc.Get("S.Updates.NeedsAdmin")
                : Loc.Get("S.Updates.KeepsFileName");
            UpdateConfirmApplyButton.Content = plan.NeedsElevation
                ? Loc.Get("S.Updates.UpdateAsAdmin")
                : Loc.Get("S.Update.Confirm");
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
            UpdateNowButton.Content = Loc.Get("S.Common.Cancel");
            CheckUpdatesButton.IsEnabled = false;
            ShowProgress(0);
            ShowUpdateStatus(Loc.Format("S.Updates.Downloading", 0), accent: false);

            try
            {
                var progress = new Progress<double>(share =>
                {
                    ShowProgress(share);
                    ShowUpdateStatus(Loc.Format("S.Updates.Downloading", $"{share * 100:0}"), accent: false);
                });

                var (result, file) = await UpdateInstaller.DownloadAsync(
                    plan,
                    _services.DownloadHttp,
                    progress,
                    cancellation.Token);

                if (!result.Ok || file is null)
                {
                    ShowUpdateStatus(Loc.Format("S.Updates.Failed", result.Error), accent: false);
                    OpenReleaseButton.Visibility = Visibility.Visible;
                    return;
                }

                ShowUpdateStatus(Loc.Get("S.Updates.Installing"), accent: false);

                // Чат сохраняем до подмены: дальше процесс уже завершается.
                PersistCurrent();

                var swap = plan.NeedsElevation
                    ? await SwapWithElevationAsync(plan, file)
                    : UpdateInstaller.Swap(file, plan.ExePath);

                if (!swap.Ok)
                {
                    ShowUpdateStatus(Loc.Format("S.Updates.Failed", swap.Error), accent: false);
                    OpenReleaseButton.Visibility = Visibility.Visible;
                    return;
                }

                Restart(plan.ExePath);
            }
            catch (OperationCanceledException)
            {
                ShowUpdateStatus(Loc.Get("S.Updates.DownloadCancelled"), accent: false);
            }
            finally
            {
                _updateDownload = null;

                // Через словарь, а не обратно в DynamicResource: локальное значение уже перекрыло
                // ссылку из разметки, и вернуть её нечем — иначе после первой же загрузки кнопка
                // навсегда осталась бы на языке, который стоял в тот момент.
                UpdateNowButton.Content = Loc.Get("S.Updates.Update");
                CheckUpdatesButton.IsEnabled = true;
                UpdateProgress.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Просит права только на подмену файла и ждёт, пока она закончится.
        /// </summary>
        /// <remarks>
        /// Через UAC поднимается отдельный короткий запуск той же программы: он переставляет файл
        /// и выходит. Новую версию запускает потом этот, обычный процесс — иначе программа после
        /// обновления осталась бы работать с правами администратора, о которых человек не просил.
        /// Он для этого и жив: переименовать работающий exe Windows позволяет.
        /// </remarks>
        private static async Task<UpdateStepResult> SwapWithElevationAsync(UpdatePlan plan, string file)
        {
            var start = new ProcessStartInfo
            {
                FileName = plan.ExePath,
                UseShellExecute = true,
                Verb = "runas"
            };
            start.ArgumentList.Add("--apply-update");
            start.ArgumentList.Add(file);

            try
            {
                using var elevated = Process.Start(start);
                if (elevated is null)
                {
                    return UpdateStepResult.Failed(Loc.Get("S.Updates.ElevationFailed"));
                }

                await elevated.WaitForExitAsync();
                return elevated.ExitCode == 0
                    ? UpdateStepResult.Success
                    : UpdateStepResult.Failed(Loc.Get("S.Updates.ElevationFailed"));
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return UpdateStepResult.Failed(Loc.Get("S.Updates.ElevationRefused"));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                return UpdateStepResult.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Запускает новую версию и закрывается.
        /// </summary>
        /// <remarks>
        /// <c>--await-exit</c> обязателен. Замок единственного экземпляра держится до конца
        /// процесса, а закрыться раньше, чем запустить преемника, этот процесс не может: без
        /// ожидания новый видит живого владельца, отдаёт ему запрос и выходит — оба процесса
        /// исчезают, и человек остаётся без окна.
        /// </remarks>
        private void Restart(string exePath)
        {
            var start = new ProcessStartInfo { FileName = exePath, UseShellExecute = true };
            start.ArgumentList.Add("--await-exit");
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            try
            {
                Process.Start(start);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                ShowUpdateStatus(
                    Loc.Format("S.Updates.RestartFailed", ex.Message),
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

        private static string DownloadSize(long bytes) =>
            bytes <= 0 ? Loc.Get("S.Updates.UnknownSize") : AttachmentTypes.FormatSize(bytes);

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
                ShowUpdateStatus(Loc.Get("S.Updates.Checking"), accent: false);
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
                    ShowUpdateStatus(result.Error ?? Loc.Get("S.Updates.CheckFailed"), accent: false);
                }

                return;
            }

            if (!result.UpdateAvailable)
            {
                if (manual)
                {
                    ShowUpdateStatus(
                        Loc.Format("S.Updates.UpToDate", RuntimeContext.AppVersion),
                        accent: false);
                }

                return;
            }

            _latestRelease = result.Latest;
            _releasePageUrl = result.Latest.PageUrl;
            ShowFoundRelease(result.Latest);
        }

        /// <summary>Показывает найденный релиз. Общее для проверки и для открытия настроек.</summary>
        private void ShowFoundRelease(ReleaseInfo release)
        {
            _releasePageUrl ??= release.PageUrl;
            OpenReleaseButton.Visibility = Visibility.Visible;
            UpdateNowButton.Visibility = release.WindowsBuild is null
                ? Visibility.Collapsed
                : Visibility.Visible;
            ShowUpdateStatus(
                Loc.Format("S.Updates.Available", release.Version, RuntimeContext.AppVersion),
                accent: true);

            // Метка у номера версии в боковой колонке настроек: единственное место, где
            // о новой версии видно, не открывая эту страницу.
            SettingsVersionText.Text = Loc.Format("S.Updates.SidebarBadge", RuntimeContext.AppVersion);
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
