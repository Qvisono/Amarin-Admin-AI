using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>Скачанная сборка, которая ждёт закрытия программы, чтобы встать на её место.</summary>
    internal sealed record StagedUpdate(UpdatePlan Plan, string File, Version Version);

    /// <summary>
    /// Плашка обновлений на странице Data Controls и автообновление целиком: проверка при каждом
    /// запуске и дальше по расписанию, фоновая загрузка найденной версии и подмена файла при
    /// закрытии программы — уже после того, как окно исчезло.
    /// </summary>
    /// <remarks>
    /// Всё это делается, только пока включена галка «Автообновление»; снятая означает, что
    /// программа не ходит в сеть сама и ничего не ставит — остаётся кнопка на странице.
    /// Скачанный файл сверяется по размеру и SHA-256 ещё до того, как его кто-то тронет
    /// (<see cref="UpdateInstaller.DownloadAsync"/>), а подмена сохраняет путь и имя exe —
    /// иначе слетели бы ярлыки и флаг «Запускать от имени администратора».
    /// </remarks>
    public partial class MainWindow
    {
        /// <summary>Отказ UAC: человек нажал «Нет». Обычный ответ, а не сбой.</summary>
        private const int ErrorCancelled = 1223;

        /// <summary>Состояние обновлений одним словом — то, что показывает пилюля в плашке.</summary>
        private enum UpdatePhase
        {
            UpToDate,
            Checking,
            Available,
            Downloading,
            Ready,
            Failed
        }

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

        /// <summary>Фоновая загрузка автообновления; <c>null</c> — сейчас ничего не качается.</summary>
        private CancellationTokenSource? _autoDownload;

        private StagedUpdate? _staged;

        /// <summary>
        /// Скачанное обновление, ждущее выхода.
        /// </summary>
        /// <remarks>
        /// Открыто наружу ради теста: проверить, что закрытие откладывается ровно один раз,
        /// можно только подсунув сюда готовую сборку.
        /// </remarks>
        internal StagedUpdate? Staged
        {
            get => _staged;
            set => _staged = value;
        }

        /// <summary>
        /// Такт, который сверяет, не пора ли проверить обновления.
        /// </summary>
        /// <remarks>
        /// Короткий такт, а не один тик на весь интервал: <see cref="DispatcherTimer"/> не
        /// досчитывает время сна и гибернации, и единственный пятичасовой тик после пробуждения
        /// сдвинулся бы ровно на столько, сколько машина спала.
        /// </remarks>
        private DispatcherTimer? _updateHeartbeat;

        /// <summary>Когда автопроверке можно идти в сеть снова. UTC; в памяти, не на диске.</summary>
        private DateTime _nextAutoCheckUtc;

        /// <summary>Выход уже начат — второй раз его начинать нельзя.</summary>
        private bool _exiting;

        /// <summary>Идущая проверка обновлений — чтобы выход мог её дождаться.</summary>
        private Task? _updateCheckTask;

        /// <summary>Идущая фоновая загрузка — чтобы выход мог её дождаться.</summary>
        private Task? _autoDownloadTask;

        /// <summary>Идущая установка по кнопке «Обновить».</summary>
        private Task? _installTask;

        /// <summary>Окно спрятано, а процесс доводит обновление, прежде чем завершиться.</summary>
        private bool _hiddenForExit;

        /// <summary>Файл уже подменяется — вернуть окно нельзя, можно только перезапуститься.</summary>
        private bool _swapStarted;

        /// <summary>Во время фоновой подмены программу запустили снова — после неё поднять новую версию.</summary>
        private bool _relaunchAfterExit;

        /// <summary>
        /// Номер попытки выхода. Возвращённое повторным запуском окно начинает новую попытку, и
        /// прежняя, дождавшись своей загрузки, обязана тихо отступить — иначе файл подменили бы
        /// дважды.
        /// </summary>
        private int _exitGeneration;

        private static Version CurrentVersion =>
            Version.TryParse(RuntimeContext.AppVersion, out var version) ? version : new Version(1, 0, 0);

        /// <summary>
        /// Приводит плашку обновлений в порядок при каждом открытии настроек.
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

            UpdateVersionText.Text = "v" + RuntimeContext.AppVersion;
            ShowLastCheck();

            if (_staged is not null)
            {
                ShowStagedUpdate();
                return;
            }

            if (_latestRelease is { } found && found.Version > UpdateChecker.Normalize(CurrentVersion))
            {
                ShowFoundRelease(found);
                return;
            }

            ShowUpdateStatus(Loc.Format("S.Updates.Installed", RuntimeContext.AppVersion), accent: false);
            ShowUpdatePhase(UpdatePhase.UpToDate);
            OpenReleaseButton.Visibility = Visibility.Collapsed;
            UpdateNowButton.Visibility = Visibility.Collapsed;
        }

        private void AutoUpdateToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            var on = AutoUpdateToggle.IsChecked == true;
            _services.Settings.AutoCheckUpdates = on;
            _services.SettingsStore.Save(_services.Settings);

            if (on)
            {
                ScheduleAutoUpdateCheck();
                return;
            }

            // Снятая галка значит «ничего не делай сам»: и начатую загрузку бросаем, и уже
            // скачанное забываем, иначе оно всё равно встало бы при закрытии.
            _autoDownload?.Cancel();
            _staged = null;
            _updateHeartbeat?.Stop();
        }

        private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => StartUpdateCheck(manual: true);

        private void StartUpdateCheck(bool manual)
        {
            if (_updateCheckRunning)
            {
                return;
            }

            _updateCheckTask = CheckUpdatesAsync(manual);
            Detached.Run(_updateCheckTask, "check_updates");
        }

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
            // Во время любой загрузки — хоть ручной, хоть фоновой — та же кнопка её отменяет:
            // под ней видна полоса, и другого способа остановить её у человека нет.
            if (_updateDownload is { } running)
            {
                running.Cancel();
                return;
            }

            if (_autoDownload is { } background)
            {
                background.Cancel();
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
                ShowUpdatePhase(UpdatePhase.Failed);
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
                _installTask = InstallUpdateAsync(plan);
                Detached.Run(_installTask, "install_update");
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
            if (_services is null || _updateDownload is not null || _autoDownload is not null)
            {
                return;
            }

            // Автообновление могло принести этот самый файл в фоне. Качать его второй раз —
            // семьдесят восемь мегабайт впустую и лишняя минута ожидания.
            var ready = _staged is { } staged && staged.Version == _latestRelease?.Version
                ? staged.File
                : null;
            var version = _latestRelease?.Version;

            using var cancellation = new CancellationTokenSource();
            _updateDownload = cancellation;
            UpdateNowButton.Content = Loc.Get("S.Common.Cancel");
            CheckUpdatesButton.IsEnabled = false;

            try
            {
                var file = ready;
                if (file is null)
                {
                    ShowUpdatePhase(UpdatePhase.Downloading);
                    ShowProgress(0);
                    ShowUpdateStatus(Loc.Format("S.Updates.Downloading", 0), accent: false);

                    var progress = new Progress<double>(share =>
                    {
                        ShowProgress(share);
                        ShowUpdateStatus(Loc.Format("S.Updates.Downloading", $"{share * 100:0}"), accent: false);
                    });

                    var (result, downloaded) = await UpdateInstaller.DownloadAsync(
                        plan,
                        _services.DownloadHttp,
                        progress,
                        cancellation.Token);

                    if (!result.Ok || downloaded is null)
                    {
                        ShowUpdateStatus(Loc.Format("S.Updates.Failed", result.Error), accent: false);
                        ShowUpdatePhase(UpdatePhase.Failed);
                        OpenReleaseButton.Visibility = Visibility.Visible;
                        return;
                    }

                    file = downloaded;
                }

                // Пока качали, человек закрыл программу: ставить и перезапускать уже нельзя —
                // он просил закрыть. Скачанное откладывается, и его поставит сам выход.
                if (_exiting)
                {
                    if (version is not null)
                    {
                        _staged = new StagedUpdate(plan, file, version);
                    }

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
                    ShowUpdatePhase(UpdatePhase.Failed);
                    OpenReleaseButton.Visibility = Visibility.Visible;
                    return;
                }

                // Файл уже подменён — откладывать его на выход больше нечего.
                _staged = null;
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
                UpdateNowButton.Content = Loc.Get(_staged is null ? "S.Updates.Update" : "S.Updates.Install");
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

            // Через общий выход, а не Shutdown напрямую: подменять файл второй раз уже незачем,
            // и _exiting закрывает ворота в Closing. Отметка о подмене — чтобы выход не принял
            // работающую ещё старую версию за недоведённое обновление и не качал его заново.
            _swapStarted = true;
            RequestExit();
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

        /// <summary>Заводит автопроверку: сразу и дальше по такту.</summary>
        /// <remarks>
        /// Сразу — при каждом запуске, а не по сроку от прошлой проверки. До 1.26.0 здесь
        /// считалось пять часов от последней, и человек, открывавший программу раз в день,
        /// узнавал о новой версии через запуск; обновление при закрытии поэтому часто просто
        /// не успевало найтись. Один запрос к GitHub на запуск укладывается в его лимит с
        /// большим запасом.
        /// </remarks>
        private void ScheduleAutoUpdateCheck()
        {
            if (_services is null)
            {
                return;
            }

            _nextAutoCheckUtc = DateTime.MinValue;

            _updateHeartbeat ??= new DispatcherTimer(
                UpdateSchedule.Heartbeat,
                DispatcherPriority.Background,
                (_, _) => MaybeAutoCheck(),
                Dispatcher);
            _updateHeartbeat.Start();

            MaybeAutoCheck();
        }

        /// <summary>Останавливает такт. Зовётся при закрытии окна.</summary>
        internal void StopUpdateHeartbeat() => _updateHeartbeat?.Stop();

        private void MaybeAutoCheck()
        {
            if (_services is null || !_services.Settings.AutoCheckUpdates || _exiting)
            {
                return;
            }

            if (DateTime.UtcNow < _nextAutoCheckUtc)
            {
                return;
            }

            StartUpdateCheck(manual: false);
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
                ShowUpdatePhase(UpdatePhase.Checking);
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var result = await UpdateChecker.CheckAsync(CurrentVersion, timeout.Token);

                var now = DateTime.UtcNow;
                _services.Settings.LastUpdateCheckUtc = now;
                _services.SettingsStore.Save(_services.Settings);

                // Неудачную проверку повторяем заметно раньше удачной: обрыв связи не повод
                // молчать пять часов, когда сеть вернулась через минуту.
                _nextAutoCheckUtc = UpdateSchedule.NextAfter(now, result.Ok);
                ShowLastCheck();

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
            if (_services is null)
            {
                return;
            }

            if (!result.Ok || result.Latest is null)
            {
                ShowUpdatePhase(UpdatePhase.Failed);
                if (manual)
                {
                    ShowUpdateStatus(result.Error ?? Loc.Get("S.Updates.CheckFailed"), accent: false);
                }

                return;
            }

            if (!result.UpdateAvailable)
            {
                ShowUpdatePhase(UpdatePhase.UpToDate);
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

            if (UpdateSchedule.ShouldAutoDownload(
                    _services.Settings.AutoCheckUpdates,
                    result.Latest,
                    _staged?.Version))
            {
                _autoDownloadTask = AutoDownloadAsync(result.Latest);
                Detached.Run(_autoDownloadTask, "auto_download_update");
            }
        }

        /// <summary>
        /// Молча скачивает найденную сборку и откладывает её до закрытия программы.
        /// </summary>
        /// <remarks>
        /// Качаем сразу, а ставим при выходе: подмена обрывает работу, а загрузка — нет, и к
        /// моменту, когда человек закроет окно, файл уже проверен и лежит рядом. Отказ здесь —
        /// обычный ответ: следующий такт попробует снова.
        /// </remarks>
        private async Task AutoDownloadAsync(ReleaseInfo release)
        {
            if (_services is null || _updateDownload is not null || _autoDownload is not null)
            {
                return;
            }

            if (!UpdateInstaller.TryPlan(release, Environment.ProcessPath, out var plan, out var error))
            {
                ShowUpdateStatus(error, accent: false);
                ShowUpdatePhase(UpdatePhase.Failed);
                OpenReleaseButton.Visibility = Visibility.Visible;
                return;
            }

            using var cancellation = new CancellationTokenSource();
            _autoDownload = cancellation;
            ShowUpdatePhase(UpdatePhase.Downloading);
            ShowProgress(0);

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
                    ShowUpdatePhase(UpdatePhase.Failed);
                    OpenReleaseButton.Visibility = Visibility.Visible;
                    return;
                }

                _staged = new StagedUpdate(plan, file, release.Version);
                ShowStagedUpdate();
            }
            catch (OperationCanceledException)
            {
                // Отменяют эту загрузку только выходом из программы или снятой галкой —
                // в обоих случаях сообщать уже некому.
            }
            finally
            {
                _autoDownload = null;
                UpdateProgress.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>Показывает найденный релиз. Общее для проверки и для открытия настроек.</summary>
        private void ShowFoundRelease(ReleaseInfo release)
        {
            _releasePageUrl ??= release.PageUrl;
            OpenReleaseButton.Visibility = Visibility.Visible;
            UpdateNowButton.Visibility = release.WindowsBuild is null
                ? Visibility.Collapsed
                : Visibility.Visible;
            ShowUpdatePhase(UpdatePhase.Available);
            ShowUpdateStatus(
                Loc.Format("S.Updates.Available", release.Version, RuntimeContext.AppVersion),
                accent: true);

            ShowSidebarVersionBadge("S.Updates.SidebarBadge");
        }

        /// <summary>Показывает уже скачанную сборку, которая ждёт закрытия программы.</summary>
        private void ShowStagedUpdate()
        {
            if (_staged is not { } staged)
            {
                return;
            }

            ShowUpdatePhase(UpdatePhase.Ready);
            ShowUpdateStatus(
                Loc.Format(
                    staged.Plan.NeedsElevation ? "S.Updates.ReadyAdmin" : "S.Updates.Ready",
                    staged.Version),
                accent: true);

            UpdateNowButton.Content = Loc.Get("S.Updates.Install");
            UpdateNowButton.Visibility = Visibility.Visible;
            OpenReleaseButton.Visibility = Visibility.Visible;
            ShowSidebarVersionBadge("S.Updates.SidebarReady");
        }

        /// <summary>
        /// Метка у номера версии в боковой колонке настроек: единственное место, где о новой
        /// версии видно, не открывая эту страницу.
        /// </summary>
        private void ShowSidebarVersionBadge(string key)
        {
            SettingsVersionText.Text = Loc.Format(key, RuntimeContext.AppVersion);
            SettingsVersionText.SetResourceReference(TextBlock.ForegroundProperty, "Accent.Fill");
        }

        private void ShowLastCheck() =>
            UpdateLastCheckText.Text = UpdateSchedule.DescribeLastCheck(
                _services?.Settings.LastUpdateCheckUtc,
                DateTime.UtcNow);

        /// <summary>
        /// Красит пилюлю состояния.
        /// </summary>
        /// <remarks>
        /// Кисти через <c>SetResourceReference</c>, а не литералом: захардкоженная кисть
        /// перестала бы менять тему вместе со всем остальным.
        /// «Последняя версия» берёт обычный хром и обводку акцентом, а не <c>Status.Success</c>:
        /// Success и SuccessSoft — соседние зелёные для текста, не пара «заливка + подпись».
        /// На большинстве палитр надпись пропадала (~1.3:1), и зелёный ещё и не следовал акценту
        /// темы. Обводку тоже ставит код: иначе акцент остался бы на пилюле «есть обновление».
        /// </remarks>
        private void ShowUpdatePhase(UpdatePhase phase)
        {
            var (key, background, foreground, border) = phase switch
            {
                UpdatePhase.Checking => ("S.Updates.Pill.Checking", "Bg.Raised", "Text.Dim", "Border.Subtle"),
                UpdatePhase.Available => ("S.Updates.Pill.Available", "Accent.Fill", "Text.OnAccent", "Accent.Fill"),
                UpdatePhase.Downloading => ("S.Updates.Pill.Downloading", "Bg.Raised", "Text.Dim", "Border.Subtle"),
                UpdatePhase.Ready => ("S.Updates.Pill.Ready", "Accent.Fill", "Text.OnAccent", "Accent.Fill"),
                UpdatePhase.Failed => ("S.Updates.Pill.Failed", "Status.WarningSurface", "Status.Warning", "Status.WarningBorder"),
                _ => ("S.Updates.Pill.UpToDate", "Bg.Raised", "Text.Secondary", "Accent.Fill")
            };

            UpdateStatePillText.Text = Loc.Get(key);
            UpdateStatePill.SetResourceReference(Border.BackgroundProperty, background);
            UpdateStatePill.SetResourceReference(Border.BorderBrushProperty, border);
            UpdateStatePillText.SetResourceReference(TextBlock.ForegroundProperty, foreground);
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

        /// <summary>Скачанное обновление ждёт выхода.</summary>
        internal bool HasStagedUpdate => _staged is not null;

        /// <summary>
        /// Единственная точка выхода из программы.
        /// </summary>
        /// <remarks>
        /// Кнопка закрытия зовёт её, а не <c>Application.Shutdown</c>, и это не придирка: при
        /// начатом Shutdown WPF гасит диспетчер в любом случае, и <c>e.Cancel</c> в
        /// <c>Closing</c> процесс уже не удержит. Отложить выход ради подмены файла можно,
        /// только не начиная Shutdown.
        /// </remarks>
        internal void RequestExit()
        {
            if (_exiting)
            {
                return;
            }

            _exiting = true;
            Detached.Run(FinishExitAsync(), "exit");
        }

        /// <summary>
        /// Надо ли отложить закрытие окна ради обновления.
        /// </summary>
        /// <remarks>
        /// Отдельно от <see cref="TryDeferCloseForUpdate"/>, потому что тому нечем ответить, не
        /// начав выход: проверить решение, не погасив при этом всё приложение, можно только здесь.
        /// </remarks>
        internal bool ShouldDeferClose => !_exiting && (HasStagedUpdate || UpdatePendingForExit);

        /// <summary>
        /// Обновление ещё не скачано, но его стоит довести после закрытия: идёт проверка или
        /// загрузка, или найденная версия ждёт своей очереди.
        /// </summary>
        /// <remarks>
        /// Только при включённом автообновлении: снятая галка значит «ничего не делай сам», и
        /// процесс, оставшийся жить после закрытия окна, был бы именно этим.
        /// </remarks>
        internal bool UpdatePendingForExit =>
            _services is { Settings.AutoCheckUpdates: true } &&
            (_updateCheckRunning ||
             _autoDownload is not null ||
             _updateDownload is not null ||
             (_latestRelease is { WindowsBuild: not null } found &&
              found.Version > UpdateChecker.Normalize(CurrentVersion)));

        /// <summary>
        /// Откладывает закрытие окна, если есть что поставить.
        /// </summary>
        /// <returns><c>true</c> — закрытие нужно отменить, выход с обновлением пошёл.</returns>
        internal bool TryDeferCloseForUpdate()
        {
            if (!ShouldDeferClose)
            {
                return false;
            }

            RequestExit();
            return true;
        }

        /// <summary>
        /// Завершает программу, доводя обновление уже без окна.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Окно исчезает сразу: человек просил закрыть программу, и ждать загрузку или подмену
        /// перед экраном он не должен. Дальше процесс без окна дожидается проверки, докачивает
        /// найденную версию (сверка по размеру и SHA-256 — та же, что и всегда), подменяет файл
        /// и выходит. Новая версия сама не запускается — её поднимет следующий запуск.
        /// </para>
        /// <para>
        /// Потолок на всё — <see cref="UpdateSchedule.ExitLimit"/>: зависшая сеть не должна
        /// оставлять невидимый процесс навсегда. Отказ на любом шаге выходу не мешает — следующий
        /// запуск найдёт версию снова. Повторный запуск во время этой работы возвращает окно
        /// (<see cref="ReviveFromBackgroundExit"/>), а если подмена уже шла — поднимает новую
        /// версию после неё.
        /// </para>
        /// </remarks>
        private async Task FinishExitAsync()
        {
            // Уступаем очередь прежде всего остального: зовут отсюда из самого Closing, и
            // прятанье окна с завершением приложения обязаны случиться после того, как обработчик
            // вернётся и его отмена закрытия вступит в силу.
            await Dispatcher.Yield(DispatcherPriority.Background);

            var generation = ++_exitGeneration;
            _updateHeartbeat?.Stop();

            // После ручной установки файл уже новый — доводить нечего.
            var finishUpdate = !_swapStarted && (_staged is not null || UpdatePendingForExit);
            if (finishUpdate)
            {
                HideForBackgroundExit();
            }

            if (finishUpdate && _staged is null)
            {
                await BringUpdateToStageAsync();

                // Пока ждали, программу открыли снова — этот выход отменён.
                if (generation != _exitGeneration || !_exiting)
                {
                    return;
                }
            }

            // Незаконченная загрузка уже не пригодится: всё, что успело, лежит в _staged.
            _autoDownload?.Cancel();
            _updateDownload?.Cancel();

            if (finishUpdate && _staged is { } staged)
            {
                _swapStarted = true;
                var swap = staged.Plan.NeedsElevation
                    ? await SwapWithElevationAsync(staged.Plan, staged.File)
                    : UpdateInstaller.Swap(staged.File, staged.Plan.ExePath);

                if (!swap.Ok)
                {
                    PerfLog.Write("update_on_exit failed " + swap.Error);
                }

                _staged = null;

                if (_relaunchAfterExit)
                {
                    StartSuccessor(staged.Plan.ExePath);
                }
            }

            Application.Current?.Shutdown();
        }

        /// <summary>Ждёт проверку и загрузку, пока не наберётся готовая сборка или не выйдет срок.</summary>
        private async Task BringUpdateToStageAsync()
        {
            using var limit = new CancellationTokenSource(UpdateSchedule.ExitLimit);
            try
            {
                if (_updateCheckTask is { IsCompleted: false } check)
                {
                    await check.WaitAsync(limit.Token);
                }

                // Загрузка по кнопке, увидев выход, сама откладывает скачанное в _staged.
                if (_installTask is { IsCompleted: false } install)
                {
                    await install.WaitAsync(limit.Token);
                }

                if (_staged is null && _autoDownloadTask is { IsCompleted: false } download)
                {
                    await download.WaitAsync(limit.Token);
                }

                // Версия найдена, а загрузка не шла вовсе или сорвалась — пробуем ещё раз, теперь
                // уже без окна.
                if (_staged is null &&
                    _autoDownload is null &&
                    _latestRelease is { } release &&
                    release.Version > UpdateChecker.Normalize(CurrentVersion) &&
                    UpdateSchedule.ShouldAutoDownload(autoUpdate: true, release, staged: null))
                {
                    _autoDownloadTask = AutoDownloadAsync(release);
                    await _autoDownloadTask.WaitAsync(limit.Token);
                }
            }
            catch (OperationCanceledException)
            {
                PerfLog.Write("update_on_exit timed out");
            }
        }

        /// <summary>
        /// Прячет окно на время фоновой работы: всё сохранено, ходы остановлены, попапы закрыты.
        /// </summary>
        /// <remarks>
        /// Сохранение — здесь, а не только в <c>Closing</c>: крестик зовёт <see cref="RequestExit"/>
        /// напрямую, и до <c>Closing</c> дело доходит лишь в самом конце, после загрузки.
        /// </remarks>
        private void HideForBackgroundExit()
        {
            if (_hiddenForExit)
            {
                return;
            }

            _hiddenForExit = true;
            SaveWindowGeometry();
            FlushPendingPersists();
            _services?.ChatStore.Flush();
            _services?.Ledger.Flush();
            CancelAllTurns();
            PopupManager.CloseAll();

            foreach (var other in Application.Current?.Windows.OfType<Window>().ToList() ?? [])
            {
                if (!ReferenceEquals(other, this))
                {
                    other.Close();
                }
            }

            Hide();
        }

        /// <summary>
        /// Программу запустили снова, пока она доводила обновление без окна.
        /// </summary>
        /// <returns>
        /// <c>true</c> — окно возвращено, выход отменён, загрузка продолжается обычной фоновой.
        /// <c>false</c> — файл уже подменяется: вернуть прежнюю версию нельзя, и после подмены
        /// поднимется новая.
        /// </returns>
        private bool ReviveFromBackgroundExit()
        {
            if (!_hiddenForExit)
            {
                return true;
            }

            if (_swapStarted)
            {
                _relaunchAfterExit = true;
                return false;
            }

            _exitGeneration++;
            _exiting = false;
            _hiddenForExit = false;
            Show();
            _updateHeartbeat?.Start();
            return true;
        }

        /// <summary>
        /// Запускает подменённый exe после выхода этого процесса.
        /// </summary>
        /// <remarks>
        /// <c>--await-exit</c> по той же причине, что и у <see cref="Restart"/>: замок единственного
        /// экземпляра держится до конца этого процесса.
        /// </remarks>
        private static void StartSuccessor(string exePath)
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
                PerfLog.Write("update_on_exit relaunch failed " + ex.Message);
            }
        }

        /// <summary>
        /// Windows завершает сеанс: ждать загрузку и окно UAC некогда.
        /// </summary>
        /// <remarks>
        /// Ставится только уже скачанное и только без прав администратора — это два переименования,
        /// доли секунды. Остальное доведёт следующий запуск.
        /// </remarks>
        private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
        {
            _autoDownload?.Cancel();
            _updateDownload?.Cancel();

            if (_swapStarted || _staged is not { Plan.NeedsElevation: false } staged)
            {
                return;
            }

            _swapStarted = true;
            var swap = UpdateInstaller.Swap(staged.File, staged.Plan.ExePath);
            if (!swap.Ok)
            {
                PerfLog.Write("update_on_session_end failed " + swap.Error);
            }

            _staged = null;
        }
    }
}
