using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Amarin.Core;
using Path = System.IO.Path;

namespace Amarin.UI
{
    /// <summary>
    /// Плашка обновлений на странице Data Controls и выход с обновлением.
    /// </summary>
    /// <remarks>
    /// Решения здесь не принимаются: их принимает автомат (<see cref="UpdateMachine"/>), а
    /// исполняет — <see cref="UpdateController"/> и <see cref="UpdateExit"/> в Core. Окно
    /// переносит <see cref="UpdateView"/> на контролы, передаёт нажатия и даёт автомату то, что
    /// есть только у окна: настройки, ходы, сохранение чатов, прятанье и показ.
    /// </remarks>
    public partial class MainWindow
    {
        private UpdateController? _updates;
        private UpdateExit? _exit;
        private IUpdateFiles? _updateFiles;

        /// <summary>Вопрос «Обновить до версии…», открытый сейчас.</summary>
        private UpdateOffer? _pendingOffer;

        /// <summary>Обновления этого окна. Заводятся при первом обращении — после разметки.</summary>
        internal UpdateController Updates => _updates ??= CreateUpdates();

        /// <summary>Выход с обновлением.</summary>
        internal UpdateExit Exit => _exit ??= new UpdateExit(Updates, UpdateFiles, new WindowExitHost(this));

        private IUpdateFiles UpdateFiles => _updateFiles ??= new InstallerUpdateFiles(() => _services?.DownloadHttp, Environment.ProcessPath);

        private static ReleaseVersion CurrentRelease => RuntimeContext.AppRelease;

        /// <summary>Последний найденный релиз. Открыто наружу ради тестов плашки.</summary>
        internal ReleaseInfo? LatestRelease
        {
            get => Updates.State.Latest;
            set => Updates.Seed(state => state with { Latest = value });
        }

        /// <summary>Скачанное обновление, ждущее выхода. Открыто наружу ради тестов выхода.</summary>
        internal StagedUpdate? Staged
        {
            get => Updates.State.Staged;
            set => Updates.Seed(state => state with { Staged = value });
        }

        private UpdateController CreateUpdates() =>
            CreateUpdates(new GitHubUpdateSource(), UpdateFiles, new WindowUpdateApp(this));

        private UpdateController CreateUpdates(IUpdateSource source, IUpdateFiles files, IUpdateApp app)
        {
            // Итоги сетевых шагов возвращаются очередью диспетчера: внутри одного приоритета
            // она строгая, и итог не обгонит то, что встало в очередь раньше.
            var controller = new UpdateController(
                source,
                files,
                app,
                CurrentRelease,
                action => Dispatcher.InvokeAsync(action));
            controller.Changed += RenderUpdates;
            return controller;
        }

        /// <summary>
        /// Обновления этого окна на подставных GitHub, файлах и программе — для тестов, которые
        /// проходят плашку и её кнопки целиком, не трогая сети и не перезапуская процесс.
        /// </summary>
        internal void UseUpdatePorts(IUpdateSource source, IUpdateFiles files, IUpdateApp app)
        {
            _updates?.Dispose();
            _exit = null;
            _updateFiles = files;
            _updates = CreateUpdates(source, files, app);
            RenderUpdates();
        }

        /// <summary>
        /// Приводит плашку обновлений в порядок при каждом открытии настроек и смене языка.
        /// </summary>
        /// <remarks>
        /// Найденный релиз пересказывается заново, а не забывается. Раньше здесь безусловно
        /// гасли обе кнопки: автопроверка при запуске находила новую версию и показывала
        /// «Обновить», человек шёл в настройки — и открытие страницы стирало находку.
        /// </remarks>
        internal void LoadUpdatesUi()
        {
            if (_services is not null)
            {
                AutoUpdateToggle.IsChecked = _services.Settings.AutoCheckUpdates;
            }

            UpdateVersionText.Text = "v" + RuntimeContext.AppVersion;
            ShowLastCheck();
            LoadReleaseExtrasUi();

            // Страница нарисована заново — «Загрузка отменена» и прочие ответы на прошлые
            // нажатия уже не к месту.
            Updates.DismissNotices();
            RenderUpdates();
        }

        /// <summary>Рисует плашку по тому, что сейчас известно. Одно место на все случаи.</summary>
        private void RenderUpdates()
        {
            var view = UpdateView.From(Updates.State, RuntimeContext.AppVersion);

            ShowUpdatePhase(view.Phase);
            ShowUpdateStatus(view.Status, view.StatusAccent);

            // Через словарь, а не обратно в DynamicResource: локальное значение уже перекрыло
            // ссылку из разметки, и вернуть её нечем — иначе после первой же загрузки кнопка
            // навсегда осталась бы на языке, который стоял в тот момент.
            UpdateNowButton.Content = view.ActionLabel;
            UpdateNowButton.Visibility = view.Action == UpdateAction.None ? Visibility.Collapsed : Visibility.Visible;
            UpdateCancelDownloadButton.Visibility = view.CanCancelDownload ? Visibility.Visible : Visibility.Collapsed;
            OpenReleaseButton.Visibility = view.ShowOpenRelease ? Visibility.Visible : Visibility.Collapsed;
            CheckUpdatesButton.IsEnabled = view.CanCheck;

            if (view.Progress is { } share)
            {
                ShowProgress(share);
            }
            else
            {
                UpdateProgress.Visibility = Visibility.Collapsed;
            }

            SettingsVersionText.Text = view.SidebarText;
            SettingsVersionText.SetResourceReference(TextBlock.ForegroundProperty, view.SidebarAccent ? "Accent.Fill" : "Text.Secondary");
            RenderUpdateBadge(view);
        }

        // Значки в точках квадрата 18×18 с центром 9,9 — как кольцо загрузки вокруг них.
        private const string BadgeArrowGlyph = "M9,5 L9,12.5 M5.8,9.3 L9,12.5 L12.2,9.3";
        private const string BadgeReadyGlyph = "M5.5,9.5 L8,12 L12.5,6.5";
        private const string BadgeFailedGlyph = "M9,4.5 L9,10.2 M9,13.1 L9,13.3";

        /// <summary>
        /// Значок обновления в шапке и его попап — из того же вида, что и плашка в настройках:
        /// одно состояние, два места, и разойтись им нечем.
        /// </summary>
        private void RenderUpdateBadge(UpdateView view)
        {
            var ring = view.Badge is UpdateBadge.Downloading or UpdateBadge.Installing;
            UpdateBadgeTrack.Visibility = ring ? Visibility.Visible : Visibility.Collapsed;
            UpdateBadgeArc.Visibility = ring ? Visibility.Visible : Visibility.Collapsed;
            if (view.Badge == UpdateBadge.Hidden)
            {
                UpdateBadgeButton.IsChecked = false;
                UpdateBadgeButton.Visibility = Visibility.Collapsed;
                return;
            }

            UpdateBadgeButton.Visibility = Visibility.Visible;
            if (ring)
            {
                UpdateBadgeArc.Data = BadgeArc(view.Badge == UpdateBadge.Installing ? 1 : view.Progress ?? 0);
            }

            UpdateBadgeIcon.Data = Glyphs.Get(view.Badge switch
            {
                UpdateBadge.Ready => BadgeReadyGlyph,
                UpdateBadge.Failed => BadgeFailedGlyph,
                _ => BadgeArrowGlyph
            });
            UpdateBadgeIcon.SetResourceReference(Shape.StrokeProperty, view.Badge == UpdateBadge.Failed ? "Status.Warning" : "Accent.Fill");
            UpdateBadgeButton.ToolTip = view.SidebarText;

            UpdateBadgeVersion.Text = view.SidebarText;
            UpdateBadgeStatus.Text = view.Status;
            if (view.Progress is { } share)
            {
                var done = Math.Clamp(share, 0, 1);
                UpdateBadgeProgressDone.Width = new GridLength(done, GridUnitType.Star);
                UpdateBadgeProgressLeft.Width = new GridLength(1 - done, GridUnitType.Star);
                UpdateBadgeProgress.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateBadgeProgress.Visibility = Visibility.Collapsed;
            }

            UpdateBadgeActionButton.Content = view.ActionLabel;
            UpdateBadgeActionButton.Visibility = view.Action == UpdateAction.None ? Visibility.Collapsed : Visibility.Visible;
            UpdateBadgeCancelButton.Visibility = view.CanCancelDownload ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Дуга кольца загрузки: от верха по часовой стрелке на долю скачанного. Радиус 8 при
        /// толщине 2 — кольцо целиком в квадрате 18×18.
        /// </summary>
        internal static Geometry BadgeArc(double share)
        {
            const double center = 9;
            const double radius = 8;
            var part = Math.Clamp(share, 0, 1);
            if (part >= 0.999)
            {
                return new EllipseGeometry(new Point(center, center), radius, radius);
            }

            var angle = part * 2 * Math.PI;
            var start = new Point(center, center - radius);
            var end = new Point(center + (radius * Math.Sin(angle)), center - (radius * Math.Cos(angle)));
            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, part > 0.5, SweepDirection.Clockwise, isStroked: true));
            var arc = new PathGeometry([figure]);
            arc.Freeze();
            return arc;
        }

        /// <summary>
        /// Главная кнопка попапа — та же, что на плашке. Попап закрывается: вопрос «Обновить до
        /// версии…» встаёт на его место, а не под ним.
        /// </summary>
        private void UpdateBadgeActionButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateBadgeButton.IsChecked = false;
            UpdateNowButton_Click(sender, e);
        }

        /// <summary>
        /// Фоновую загрузку — до следующего запуска (<see cref="UpdateState.Postponed"/>). Одна
        /// кнопка на два места: попап значка в шапке и карточка в настройках.
        /// </summary>
        private void UpdateBadgeCancelButton_Click(object sender, RoutedEventArgs e) => Updates.Cancel();

        /// <summary>Настройки на странице данных — карточка обновлений там.</summary>
        private void UpdateBadgeDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateBadgeButton.IsChecked = false;
            OpenSettingsPage(NavData);
            Dispatcher.BeginInvoke(() => UpdateVersionText.BringIntoView(), DispatcherPriority.Loaded);
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
            Updates.SetAutoUpdate(on);
        }

        private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => Updates.CheckNow();

        private void OpenReleaseButton_Click(object sender, RoutedEventArgs e)
        {
            var url = Updates.State.Latest?.PageUrl ?? UpdateChecker.ReleasesPageUrl;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                Updates.ShowNotice(UpdateNoticeKind.BrowserFailed, ex.Message);
            }
        }

        /// <summary>
        /// Главная кнопка плашки. Что она делает, решает вид: «Отмена» отменяет загрузку, которую
        /// ждёт человек; «Обновить» во время фоновой загрузки присоединяется к ней.
        /// </summary>
        private void UpdateNowButton_Click(object sender, RoutedEventArgs e)
        {
            var action = UpdateView.From(Updates.State, RuntimeContext.AppVersion).Action;
            if (action == UpdateAction.Cancel)
            {
                Updates.Cancel();
                return;
            }

            if (action != UpdateAction.None && Updates.Prepare() is { } offer)
            {
                ShowUpdateConfirm(offer);
            }
        }

        private void ShowUpdateConfirm(UpdateOffer offer)
        {
            var release = offer.Release;
            var plan = offer.Plan;
            _pendingOffer = offer;
            UpdateConfirmTitle.Text = Loc.Format("S.Updates.ConfirmTitle", release.Release);
            UpdateConfirmText.Text = Loc.Format(
                "S.Updates.ConfirmText",
                RuntimeContext.AppVersion,
                release.Release,
                DownloadSize(plan.Asset.Size));
            ShowConfirmNotes(release.Notes);
            UpdateConfirmFile.Text = Path.GetFileName(plan.ExePath);
            UpdateConfirmFolder.Text = plan.Folder;
            UpdateConfirmNote.Text = plan.NeedsElevation
                ? Loc.Get("S.Updates.KeepsFileName") + " " + Loc.Get("S.Updates.NeedsAdmin")
                : Loc.Get("S.Updates.KeepsFileName");
            UpdateConfirmApplyButton.Content = plan.NeedsElevation
                ? Loc.Get("S.Updates.UpdateAsAdmin")
                : Loc.Get("S.Update.Confirm");

            // Без суммы обычной кнопки нет: ставить непроверенное — отдельное решение, со своим
            // вторым вопросом.
            UpdateConfirmNoChecksum.Visibility = plan.Verified ? Visibility.Collapsed : Visibility.Visible;
            UpdateConfirmApplyButton.Visibility = plan.Verified ? Visibility.Visible : Visibility.Collapsed;
            UpdateConfirmUnverifiedButton.Visibility = plan.Verified ? Visibility.Collapsed : Visibility.Visible;
            UpdateConfirmOverlay.Visibility = Visibility.Visible;
            ChatBlocked = true;
        }

        private void UpdateConfirmCancelButton_Click(object sender, RoutedEventArgs e) => CloseUpdateConfirm();

        private void UpdateConfirmUnverifiedButton_Click(object sender, RoutedEventArgs e)
        {
            var offer = _pendingOffer;
            CloseUpdateConfirm();
            if (offer is not null)
            {
                Detached.Run(ConfirmUnverifiedInstallAsync(offer), "install_unverified");
            }
        }

        /// <summary>Второй вопрос перед сборкой, которую нечем сверить.</summary>
        private async Task ConfirmUnverifiedInstallAsync(UpdateOffer offer)
        {
            var confirmed = await ShowNoticeAsync(
                Loc.Get("S.Updates.UnverifiedTitle"),
                Loc.Get("S.Updates.UnverifiedText"),
                Loc.Get("S.Updates.InstallUnverified"),
                Loc.Get("S.Common.Cancel"),
                NoticeTone.Danger);
            if (confirmed)
            {
                Updates.Confirm(offer, allowUnverified: true);
            }
        }

        private void UpdateConfirmApplyButton_Click(object sender, RoutedEventArgs e)
        {
            var offer = _pendingOffer;
            CloseUpdateConfirm();
            if (offer is not null)
            {
                Updates.Confirm(offer);
            }
        }

        private void CloseUpdateConfirm()
        {
            _pendingOffer = null;
            UpdateConfirmOverlay.Visibility = Visibility.Collapsed;
            ChatBlocked = ConfirmationOverlay.Visibility == Visibility.Visible;
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

        /// <summary>Заводит автопроверку: сразу и дальше по такту (<see cref="UpdateController.Start"/>).</summary>
        private void ScheduleAutoUpdateCheck() => Updates.Start();

        /// <summary>Останавливает такт. Зовётся при закрытии окна.</summary>
        internal void StopUpdateHeartbeat() => _updates?.Stop();

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
                UpdatePhase.Found => ("S.Updates.Pill.Available", "Accent.Fill", "Text.OnAccent", "Accent.Fill"),
                UpdatePhase.Downloading => ("S.Updates.Pill.Downloading", "Bg.Raised", "Text.Dim", "Border.Subtle"),
                UpdatePhase.Downloaded => ("S.Updates.Pill.Ready", "Accent.Fill", "Text.OnAccent", "Accent.Fill"),
                UpdatePhase.Installing => ("S.Updates.Pill.Installing", "Bg.Raised", "Text.Dim", "Border.Subtle"),
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
        internal bool HasStagedUpdate => Updates.State.Staged is not null;

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
            if (Exit.Exiting)
            {
                return;
            }

            // Чужое окно (в программе его не бывает — только в тестах, рядом с общим) закрывается
            // само по себе: гасить из-за него всё приложение нельзя.
            if (!OwnsApplication)
            {
                Close();
                return;
            }

            // Значок в трее уходит вместе с программой: пока выход доводит обновление без окна,
            // щелчок по нему вёл бы в никуда. Второй запуск вернёт и окно, и значок.
            _tray?.Dispose();
            _tray = null;
            Detached.Run(Exit.BeginAsync(), "exit");
        }

        /// <summary>
        /// Выход этого окна — выход программы: оно главное окно приложения или приложения нет.
        /// </summary>
        /// <remarks>
        /// В программе окно одно, и так было всегда. Но оконные тесты поднимают свои окна рядом с
        /// общим, и закрытие такого окна при включённом автообновлении откладывалось ради проверки
        /// версии, а через несколько секунд выход звал <c>Application.Shutdown</c> — общий поток
        /// интерфейса гас посреди чужого теста, и три сотни следующих падали одинаковым «A task
        /// was canceled».
        /// </remarks>
        private bool OwnsApplication =>
            Application.Current is not { } application ||
            application.MainWindow is null ||
            ReferenceEquals(application.MainWindow, this);

        /// <summary>Надо ли отложить закрытие окна ради обновления (см. <see cref="UpdateExit.ShouldDeferClose"/>).</summary>
        internal bool ShouldDeferClose => Exit.ShouldDeferClose;

        /// <summary>Обновление стоит довести после закрытия (см. <see cref="UpdateExit.PendingForExit"/>).</summary>
        internal bool UpdatePendingForExit => Exit.PendingForExit;

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
        /// Прячет окно на время фоновой работы: всё сохранено, ходы остановлены, попапы закрыты.
        /// </summary>
        /// <remarks>
        /// Сохранение — здесь, а не только в <c>Closing</c>: крестик зовёт <see cref="RequestExit"/>
        /// напрямую, и до <c>Closing</c> дело доходит лишь в самом конце, после загрузки.
        /// </remarks>
        private void HideForBackgroundExit()
        {
            SaveWindowGeometry();
            FlushPendingPersists();
            FlushDraft();
            _services?.ChatStore.Flush();
            _services?.Ledger.Flush();
            AppSettingsStore.FlushAll();
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
        /// Программу запустили снова, пока она доводила обновление без окна (см. <see cref="UpdateExit.Revive"/>).
        /// </summary>
        private bool ReviveFromBackgroundExit() => Exit.Revive();

        /// <summary>Windows завершает сеанс: подменить уже скачанное, пока сеанс ждёт ответа.</summary>
        private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
        {
            // До выхода процесса дело может не дойти: Windows гасит его, как только ответили.
            AppSettingsStore.FlushAll();
            Exit.OnSessionEnding();
        }

        /// <summary>Настройки, ходы, сохранение и перезапуск — то, что автомату даёт окно.</summary>
        private sealed class WindowUpdateApp(MainWindow window) : IUpdateApp
        {
            public bool AutoUpdate => window._services?.Settings.AutoCheckUpdates == true;

            public bool Beta => window._services?.Settings.BetaChannel == true;

            public ReleaseVersion? Declined => ReleaseVersion.Parse(window._services?.Settings.DeclinedUpdate);

            public bool TurnsRunning => window.AnyTurnRunning;

            public void RememberCheck(DateTime utc)
            {
                if (window._services is { } services)
                {
                    services.Settings.LastUpdateCheckUtc = utc;
                    services.SettingsStore.Save(services.Settings);
                }

                window.ShowLastCheck();
            }

            public void BeforeSwap() => window.PersistCurrent();

            /// <remarks>
            /// Через общий выход, а не Shutdown напрямую: подменять файл второй раз уже незачем
            /// (автомат знает, что подмена была), а выход закрывает ворота в <c>Closing</c>.
            /// </remarks>
            public string? RestartInto(string exePath)
            {
                if (AppRelaunch.StartSuccessor(exePath) is { } error)
                {
                    return error;
                }

                window.RequestExit();
                return null;
            }
        }

        /// <summary>Окно вокруг выхода с обновлением.</summary>
        private sealed class WindowExitHost(MainWindow window) : IExitHost
        {
            public bool OwnsApplication => window.OwnsApplication;

            public bool UpdateForbidden => window._wipeRestart || window._elevationRestart;

            public async Task YieldAsync() => await Dispatcher.Yield(DispatcherPriority.Background);

            public void HideForBackgroundExit() => window.HideForBackgroundExit();

            public void ShowAgain()
            {
                window.Show();
                if (window._services is { } services)
                {
                    window.ApplyTray(services.Settings.Windows);
                }
            }

            public void StartSuccessor(string exePath)
            {
                if (AppRelaunch.StartSuccessor(exePath) is { } error)
                {
                    PerfLog.Write("update_on_exit relaunch failed " + error);
                }
            }

            public void Shutdown() => Application.Current?.Shutdown();
        }
    }
}
