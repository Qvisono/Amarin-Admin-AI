using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI
{
    public partial class MainWindow : Window, IChatTurnUi
    {
        private AppServices? _services;
        private ChatSession _session = new();
        private bool _sidebarCollapsed;

        /// <summary>
        /// Вьюшка ответа в открытом чате. Состояние самого хода живёт в <see cref="Turns"/>: у окна
        /// один экран, а ходов может идти несколько.
        /// </summary>
        private AssistantMessageView? _liveAssistant;
        private readonly DispatcherTimer _workingTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        // Разметку в живом ответе пересобираем по таймеру, а не на каждую дельту: полная
        // перестройка FlowDocument десятки раз в секунду съела бы UI-поток.
        private readonly DispatcherTimer _streamRender = new() { Interval = StreamRenderMin };

        /// <summary>Шаг живого рендера в лучшем случае — на коротком ответе он таким и остаётся.</summary>
        private static readonly TimeSpan StreamRenderMin = TimeSpan.FromMilliseconds(80);

        /// <summary>Потолок шага: реже человек уже читает текст рывками, а не по мере набора.</summary>
        private static readonly TimeSpan StreamRenderMax = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Во сколько раз пауза между перерисовками больше самой перерисовки. Тройка означает,
        /// что на живой ответ уходит не больше трети потока диспетчера, а остальное достаётся
        /// кадрам, прокрутке и вводу.
        /// </summary>
        private const double StreamRenderShare = 3.0;
        /// <summary>Место каждого сообщения в ленте, по его идентификатору.</summary>
        private readonly Dictionary<string, ChatMessageHost> _messageViews = [];

        /// <summary>
        /// Карточка моложе этого активацию окна не замечает: срок переживает шквал активаций вокруг
        /// её показа, но настоящий возврат щелчком её всё же закрывает.
        /// </summary>
        private static readonly TimeSpan ActivationDismissGrace = TimeSpan.FromMilliseconds(700);

        private NotificationToast? _toast;

        /// <summary>Карточка «ответ готов» на экране, если есть. Для тестов.</summary>
        internal NotificationToast? CurrentToast => _toast;
        private DateTime _workingStarted;
        private Image? _logoImage;
        private System.Windows.Shapes.Path? _expandIcon;
        private TextBlock? _newChatLabel;
        private System.Windows.Shapes.Path? _newChatPlus;
        private Image? _modelButtonLogo;
        private TextBlock? _modelButtonLabel;
        private System.Windows.Shapes.Path? _modelButtonLightning;
        private TextBlock? _modelButtonLetter;
        private bool _settingsUiLoading;

        /// <summary>Окно закрыто: отложенная работа, догнавшая его после закрытия, отступает.</summary>
        private bool _windowClosed;
        private bool _stickToBottom = true;
        private bool _autoScrolling;

        public MainWindow()
        {
            using (PerfLog.Measure("main_window_ctor"))
            {
                InitializeComponent();
            }

            Title = $"Amarin Admin AI v{RuntimeContext.AppVersion}";
            TitleText.Text = Title;

            PlanOverlay.Decided += OnPlanDecided;
            HealthOverlay.AskRequested += OnHealthAskRequested;
            WireFind();
            WireWorkReport();
            WireShortcuts();
            WireCommands();
            WireDrafts();
            WireCodeBlocks();
            WireDropZone();
            WireChatProfile();
            WireVoice();
            WireCostEstimate();
            WireWindowsIntegration();

            // Имя для диктора и рамка фокуса — всем кнопкам и полям, у которых их нет (I1).
            AccessibilityDefaults.Register();
            ChatTargetPicker.Picked += OnTargetPicked;
            ChatTargetPicker.ManageRequested += OpenMachinesSettings;
            HealthOverlay.CloseRequested += CloseHealth;

            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);

            WindowRenderDefaults.Apply(this);
            InitializeAppearance();

            // Выпадашки чата — под тем же присмотром, что и пикеры в настройках: одна открытая
            // за раз, клик мимо закрывает.
            PopupManager.Register(ModelPicker, ModelButton);
            PopupManager.Register(ActionsPopup, AttachButton);
            PopupManager.Register(ConfirmationAllowPopup, ConfirmationAllowToggle);
            PopupManager.Register(UpdateBadgePopup, UpdateBadgeButton);
            InitializeQuotes();

            WindowMaximizeFix.Attach(this);

            // Окно показывается раньше, чем нарисовано: до первого кадра — цвет фона темы, а не белое.
            WindowFirstPaint.Attach(this);

            // Позже WindowMaximizeFix: перехватчик, добавленный последним, слышит сообщение первым,
            // и лента успевает заморозиться до раскладки нового размера (см. MainWindow.Resize).
            WireTranscriptResize();

            // Второй запуск программы просит это окно показаться. Вешаем здесь, а не в Program:
            // приём сообщения — дело самого окна, и в тестах оно работает так же, как в бою.
            SingleInstance.Attach(this, ActivateFromSecondInstance);
            CursorGuard.Attach(this);
            StateChanged += (_, _) => ApplyWindowStateChrome();

            // Один обработчик на всю панель вместо подписки на каждой строке — см. ChatListPanel_Click.
            ChatListPanel.AddHandler(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
                new RoutedEventHandler(ChatListPanel_Click));

            // Зажатая кнопка в колонке чатов перетаскивает чат, а не листает список.
            SmoothScroll.SetIsEnabled(SideBarScrollViewer, true);
            InitializeChatDrag();
            SmoothScroll.SetIsEnabled(ChatScrollViewer, true);
            ChatScrollViewer.ScrollChanged += ChatScrollViewer_ScrollChanged;

            // Лупа над лентой. Порядок с SmoothScroll неважен: её обработчики сидят на окне, а не
            // на ленте, и туннель приводит их первыми в любом случае — см. ChatZoom.
            InitializeChatZoom();
            InitializeScrollJump();

            // Стрелки вверх и вниз на крайней строке поля ввода уводят каретку в начало или в
            // конец текста — как в Discord. См. TextCaretEdges.
            TextCaretEdges.Attach(MessageTextBox);

            // Тело вопроса о подтверждении: у него внутри свои прокрутки — блок кода и
            // подробности, — и SmoothScroll сам уступает им колесо, а на их краю забирает
            // обратно. Без него они бы держали колесо и не отдавали наружу.
            SmoothScroll.SetIsEnabled(ConfirmationBodyScroll, true);

            Warn.Visibility = RuntimeContext.IsAdministrator()
                ? Visibility.Collapsed
                : Visibility.Visible;

            _workingTimer.Tick += (_, _) => UpdateWorkingClock();
            _streamRender.Tick += (_, _) => FlushStreamText();

            Loaded += OnWindowLoaded;
            Activated += (_, _) => OnWindowActivated();
            ThemeManager.EffectiveThemeChanged += OnEffectiveThemeChanged;
            LanguageManager.LanguageChanged += RelocalizeUi;
            // На Closing, а не на Closed: размер снимается через хэндл окна, а к Closed окно
            // с ним уже расстаётся.
            Closing += (_, e) =>
            {
                // Закрыть в трей (G1), если так выбрано: окно прячется, программа работает дальше.
                if (ShouldCloseToTray())
                {
                    e.Cancel = true;
                    HideToTray();
                    return;
                }

                // Спрятанное ради обновления окно геометрию уже сохранило, а сейчас Windows
                // отдала бы его состоянием «скрыто» — и «развёрнуто» потерялось бы. То же у
                // спрятанного в трей: геометрию оно сохранило, прячась.
                if (!Exit.HiddenForExit && !_hiddenToTray)
                {
                    SaveWindowGeometry();
                }

                // Расшифрованная копия архива под паролем не должна пережить программу.
                ForgetImportPlainCopy();

                // Хранилище пишет в фоне, и без этого последний ответ мог не доехать до диска.
                FlushPendingPersists();
                FlushDraft();
                _services?.ChatStore.Flush();

                // Журнал трат пишется отложенно: без этого последние ответы сеанса до диска не дошли бы.
                _services?.Ledger.Flush();

                // Настройки оформления — тоже (SaveDeferred): последний тик ползунка.
                AppSettingsStore.FlushAll();

                // Сбросы выше идут первыми и повторяются на втором проходе — они безобидны, а
                // вот подмену файла делать до них нельзя. Отмена закрытия здесь работает только
                // потому, что до неё никто не звал Application.Shutdown: см. RequestExit.
                if (TryDeferCloseForUpdate())
                {
                    e.Cancel = true;
                }
            };
            // Выключение Windows не проходит через Closing с правом отмены — подмена скачанного
            // обновления делается здесь, пока сеанс ещё ждёт ответа.
            if (Application.Current is { } application)
            {
                application.SessionEnding += OnSessionEnding;
            }

            Closed += (_, _) =>
            {
                _windowClosed = true;
                if (Application.Current is { } current)
                {
                    current.SessionEnding -= OnSessionEnding;
                }

                StopUpdateHeartbeat();
                CancelAllTurns();
                _persistQueue?.Dispose();
                ThemeManager.EffectiveThemeChanged -= OnEffectiveThemeChanged;
                LanguageManager.LanguageChanged -= RelocalizeUi;
                DownloadAccessBroker.SetHandler(null);
            };
            PreviewTextInput += Window_PreviewTextInput;
            PreviewKeyDown += Window_PreviewKeyDown;
        }

        internal void AttachServices(AppServices services)
        {
            if (_services is not null)
            {
                _services.Confirmations.Changed -= OnConfirmationChanged;
                _services.PlanReviews.Changed -= OnPlanReviewChanged;
            }

            _services = services;
            _services.Confirmations.Changed += OnConfirmationChanged;
            _services.PlanReviews.Changed += OnPlanReviewChanged;
            WireAutoLock();
            WireSpendLimits();

            DownloadAccessBroker.SetHandler(RequestDownloadDomainAsync);
            StartSpendBackfill();
            ApplyUiScaleFromSettings();

            // Трей, глобальные сочетания и записи в реестре (G) — хэндл окна уже есть: его завёл масштаб.
            ApplyWindowsIntegration();
            UpdateMicAvailability();

            // До Show(), как и масштаб: углы — форма окна, и первым кадром она уже должна быть той,
            // что выбрана, а не заводской системной.
            WindowCornerStyle.Apply(this, _services.Settings.WindowCorners);

            // До Show(), а не из Loaded: фон раньше впервые красился вместе со страницами
            // настроек, то есть уже после того, как окно показалось, — и первым кадром человек
            // видел голую заливку, а картинку получал следом.
            ApplyAppearance(save: false);

            if (IsLoaded)
            {
                OnWindowLoaded(this, new RoutedEventArgs());
            }
        }

        /// <summary>
        /// Пускает разовый перенос старых цен в журнал трат.
        /// </summary>
        /// <remarks>
        /// На запуске, а не по заходу на страницу трат: до правки чат, удалённый раньше первого
        /// захода туда, уносил свои деньги с графика навсегда. Фоном и брошенной задачей — обход
        /// файлов чатов не должен задерживать окно, а его неудача не повод ничего показывать:
        /// страница трат попробует ещё раз.
        /// </remarks>
        private void StartSpendBackfill()
        {
            if (_services is { } services)
            {
                Detached.Run(Task.Run(services.BackfillSpendLedger), "spend_backfill");
            }
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            SidebarLogoButton.ApplyTemplate();
            NewChatButton.ApplyTemplate();
            ModelButton.ApplyTemplate();
            _logoImage = SidebarLogoButton.Template.FindName("LogoImage", SidebarLogoButton) as Image;
            _expandIcon = SidebarLogoButton.Template.FindName("ExpandIcon", SidebarLogoButton) as System.Windows.Shapes.Path;
            _newChatLabel = NewChatButton.Template.FindName("NewChatLabel", NewChatButton) as TextBlock;
            _newChatPlus = NewChatButton.Template.FindName("NewChatPlus", NewChatButton) as System.Windows.Shapes.Path;
            _modelButtonLogo = ModelButton.Template.FindName("ModelButtonLogo", ModelButton) as Image;
            _modelButtonLabel = ModelButton.Template.FindName("ModelButtonLabel", ModelButton) as TextBlock;
            _modelButtonLightning = ModelButton.Template.FindName("ModelButtonLightning", ModelButton) as System.Windows.Shapes.Path;
            _modelButtonLetter = ModelButton.Template.FindName("ModelButtonLetter", ModelButton) as TextBlock;
            UiScale.AttachCenteredBelowTooltip(Warn);
            ChatReasoningPicker.SetUsesTools(true);
            if (_services is null)
            {
                return;
            }

            StartNewSession(persist: false);

            // Ширина и свёрнутость панели — до первого кадра: иначе список мигнул бы заводской ширины.
            SetSidebarCollapsed(_services.Settings.SidebarCollapsed);
            RestoreDraft(_session);
            RefreshChatListFirstScreen();
            UpdateModelButton();

            // Главному окну нужен от настроек только аватар в углу — остальное живёт на
            // свёрнутых страницах.
            LoadAccountUi();

            // Настройки строятся за первым кадром: в простое, оболочка и по странице за порцию
            // (MainWindow.SettingsHost). На первом кадре их не видно, а их разметка была больше
            // половины разбора окна. Открыть раньше, чем прогрев догонит, можно — открытие
            // достроит нужное само.
            ScheduleSettingsPrewarm();

            // Прошлый сеанс мог не дописать перешифровку чатов (закрыли посреди) — доводим в фоне.
            Detached.Run(_services.ChatStore.EnsureFormat(), "chat_reformat");

            // Прошлое обновление оставило рядом прежний exe и папку загрузки — убираем.
            UpdateInstaller.CleanupLeftovers(Environment.ProcessPath);
            ScheduleWhatsNew();
            ScheduleAutoUpdateCheck();
            Detached.Run(LoadModelCatalogAsync(), "load_model_catalog");
            StartSchedule();
            StartBackups();
            StartTextIndexBuild();

            // Запрос из командной строки только ложится в поле, как и переданный уже открытому
            // окну; сам уходит лишь с явным --send (см. StartupArgs.Send).
            // Перезапуск от администратора (--open-chat) возвращает в тот чат, где человек был.
            if (_services.StartupChatId is { } startupChat)
            {
                OpenChat(startupChat);
            }

            PlaceIncomingPrompt(_services.StartupPrompt, _services.StartupSend);

            // Действие из списка переходов, Проводника или автозапуска (G6).
            if (_startInTray)
            {
                _startInTray = false;
                Dispatcher.BeginInvoke(FinishStartInTray, DispatcherPriority.Background);
            }
            else if (_services.StartupAction != StartupAction.None)
            {
                RunStartupAction(_services.StartupAction, null, _services.StartupAskPath);
            }

            // Этот запуск стёр данные по просьбе прежнего — сказать об этом, а о неудаче тем более.
            if (_services.StartupWipe is { } wiped)
            {
                ShowWipeReport(wiped);
            }

            // Повреждённые настройки, профили или ключи, найденные при чтении до окна.
            Dispatcher.BeginInvoke(
                new Action(() => Detached.Run(ShowDataFileIncidentsAsync(), "data_file_incidents")),
                DispatcherPriority.Background);
        }

        /// <remarks>
        /// Не <c>Application.Shutdown</c>: он гасит диспетчер независимо от того, отменил ли кто
        /// закрытие окна, и отложить выход ради подмены файла обновления стало бы нечем.
        /// </remarks>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (ShouldCloseToTray())
            {
                HideToTray();
                return;
            }

            RequestExit();
        }

        /// <remarks>
        /// Через <see cref="Application.MainWindow"/>, а не через <c>this</c>: те же три кнопки
        /// стоят и в шапке экрана блокировки, у которого своё окно.
        /// </remarks>
        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current?.MainWindow is { } window)
            {
                window.WindowState = WindowState.Minimized;
            }
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current?.MainWindow is { } window)
            {
                window.WindowState = window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }
        }

        /// <remarks>
        /// Приближённую ленту таскают по ней самой, и окно в этот момент двигать нельзя:
        /// <c>WM_NCLBUTTONDOWN</c> уводит мышь в модальный цикл системы, после которого WPF
        /// сообщений мыши больше не видит — панорамирование просто не началось бы.
        /// </remarks>
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_chatZoom?.SuppressesWindowDrag() == true)
            {
                return;
            }

            WindowMoveBehavior.HandleMouseLeftButtonDownForMove(this, e);
        }

        /// <summary>
        /// Разворот и сворачивание гасят открытые попапы: они висят отдельными окнами и остаются
        /// на прежнем месте экрана. Кромку для растягивания здесь трогать нечем — ею заведует
        /// <c>WindowChrome</c>, и у развёрнутого окна она отключается сама.
        /// </summary>
        private static void ApplyWindowStateChrome()
        {
            PopupManager.CloseAll();
        }

        private void SettingsCloseButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        private void OpenAppFilesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppPaths.EnsureCreated();
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppPaths.Root,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Inform(Loc.Get("S.Links.OpenFailed"), ex.Message);
            }
        }

        private void DeleteAllChatsButton_Click(object sender, RoutedEventArgs e) =>
            Detached.Run(DeleteAllChatsAsync(), "delete_all_chats");

        private async Task DeleteAllChatsAsync()
        {
            if (_services is null)
            {
                return;
            }

            var confirmed = await ShowNoticeAsync(
                Loc.Get("S.ChatList.DeleteAllTitle"),
                Loc.Get("S.ChatList.DeleteAllConfirm"),
                Loc.Get("S.Common.Delete"),
                Loc.Get("S.Common.Cancel"),
                NoticeTone.Danger);
            if (!confirmed || _services is null)
            {
                return;
            }

            CancelAllTurns();
            _services.ChatStore.DeleteAll();
            Turns.ForgetAllAttention();
            StartNewSession(persist: false);
            RefreshChatList();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // Лупу здесь не сбрасываем: настройки лежат поверх ленты, и из них возвращаются к
            // тому же чату — приближение, которое человек выставил сам, должно его дождаться.
            OpenSettings();
        }

        private void AutoScrollToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            _services.Settings.AutoScroll = GeneralPage.AutoScrollToggle.IsChecked == true;
            _services.SettingsStore.Save(_services.Settings);
        }

        private void CodeLineNumbersToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            _services.Settings.CodeLineNumbers = GeneralPage.CodeLineNumbersToggle.IsChecked == true;
            _services.SettingsStore.Save(_services.Settings);

            // Блоки кода уже построены — перестраиваем ленту, не трогая лупы.
            RebuildTranscript(resetZoom: false);
        }

        private void UiScaleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            var percent = ReadUiScaleCombo();
            _services.Settings.UiScalePercent = percent;
            _services.SettingsStore.Save(_services.Settings);
            UiScale.Apply(this, ScaledRoot, percent);
        }

        /// <remarks>
        /// Лента перерисовывается целиком: подсказка с датой ставится один раз при сборке пузыря,
        /// и без этого новый формат увидели бы только ответы, пришедшие после смены. Тот же путь,
        /// каким чат обновляется при смене языка.
        /// </remarks>
        private void DateFormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            _services.Settings.DateFormat = ReadDateFormatCombo();
            _services.SettingsStore.Save(_services.Settings);
            RebuildTranscript(resetZoom: false);
        }

        private void WindowCornersComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            _services.Settings.WindowCorners = ReadWindowCornersCombo();
            _services.SettingsStore.Save(_services.Settings);
            WindowCornerStyle.Apply(this, _services.Settings.WindowCorners);
        }

        private WindowCorners ReadWindowCornersCombo() =>
            AppearancePage.WindowCornersComboBox.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<WindowCorners>(Convert.ToString(item.Tag), out var value)
                ? value
                : WindowCorners.Small;

        private void SelectWindowCorners(WindowCorners corners)
        {
            for (var i = 0; i < AppearancePage.WindowCornersComboBox.Items.Count; i++)
            {
                if (AppearancePage.WindowCornersComboBox.Items[i] is ComboBoxItem item &&
                    Enum.TryParse<WindowCorners>(Convert.ToString(item.Tag), out var value) &&
                    value == corners)
                {
                    AppearancePage.WindowCornersComboBox.SelectedIndex = i;
                    return;
                }
            }

            AppearancePage.WindowCornersComboBox.SelectedIndex = 0;
        }

        private DateFormat ReadDateFormatCombo() =>
            GeneralPage.DateFormatComboBox.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<DateFormat>(Convert.ToString(item.Tag), out var value)
                ? value
                : DateFormat.DayMonthShort;

        private void SelectDateFormat(DateFormat format)
        {
            for (var i = 0; i < GeneralPage.DateFormatComboBox.Items.Count; i++)
            {
                if (GeneralPage.DateFormatComboBox.Items[i] is ComboBoxItem item &&
                    Enum.TryParse<DateFormat>(Convert.ToString(item.Tag), out var value) &&
                    value == format)
                {
                    GeneralPage.DateFormatComboBox.SelectedIndex = i;
                    return;
                }
            }

            GeneralPage.DateFormatComboBox.SelectedIndex = 0;
        }

        /// <summary>
        /// Перечитывает ImageSource, присвоенные из кода (значки действий, логотипы моделей): они
        /// держат объект прежней темы и за DynamicResource не следят.
        /// </summary>
        private void OnEffectiveThemeChanged()
        {
            if (!IsLoaded)
            {
                return;
            }

            UpdateModelButton();

            // Перечитываем картинки, а не собираем ленту заново: всё остальное в сообщениях
            // сидит на ресурсах и перекрашивается само, а перестройка стоила бы повторного
            // разбора всей переписки — это и был лаг при переключении темы.
            ThemeImages.Refresh(this);
        }

        // ───────────────────────── Уведомление о завершении ответа ─────────────────────────

        private void NotifyOnCompleteToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            _services.Settings.NotifyOnResponseComplete = GeneralPage.NotifyOnCompleteToggle.IsChecked == true;
            _services.SettingsStore.Save(_services.Settings);
            if (!_services.Settings.NotifyOnResponseComplete)
            {
                _toast?.Dismiss();
            }
        }

        private void NotifySoundToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            _services.Settings.NotifySound = GeneralPage.NotifySoundToggle.IsChecked == true;
            _services.SettingsStore.Save(_services.Settings);
        }

        private void MaybeShowCompletionToast(ChatDisplayMessage assistant)
        {
            if (_services?.Settings.NotifyOnResponseComplete != true ||
                assistant.Status == AssistantStatus.Error)
            {
                PerfLog.Write("toast skipped reason=disabled_or_error");
                return;
            }

            // Только когда человек смотрит не сюда. IsActive этого не проверяет: окно, закрытое
            // другой программой (самый частый случай), остаётся активным.
            if (IsForeground() && WindowState != WindowState.Minimized)
            {
                PerfLog.Write("toast skipped reason=window_in_foreground");
                return;
            }

            // Never on top of a modal.
            if (ConfirmationOverlay.Visibility == Visibility.Visible ||
                DomainOverlay.Visibility == Visibility.Visible)
            {
                PerfLog.Write("toast skipped reason=modal_open");
                return;
            }

            ShowCompletionToast(assistant);

            // Карточка гаснет сама, а кнопка на панели задач мигает до возвращения — пропущенная
            // карточка не значит пропущенный ответ. Срабатывает всякий раз, когда окно не впереди,
            // а не только свёрнутое: окно под другой программой — обычный случай.
            TaskbarFlash.Flash(this);

            if (_services.Settings.NotifySound)
            {
                try
                {
                    System.Media.SystemSounds.Asterisk.Play();
                }
                catch
                {
                    // No audio device — the visual cue alone is enough.
                }
            }
        }

        /// <summary>
        /// Человек вернулся в программу: гасим мигание на панели задач и убираем карточку — если
        /// она висела достаточно, чтобы возврат был осознанным.
        /// <para>
        /// Карточка показывается, только когда окно *не* впереди, и активация может прийти
        /// одновременно с ней по причинам, к человеку отношения не имеющим: фокус сдвинулся
        /// внутри программы, открылось модальное окно, окно восстановилось. Закрытие по ним и
        /// давало уведомление, которое мелькало на кадр и пропадало.
        /// </para>
        /// </summary>
        internal void OnWindowActivated()
        {
            TaskbarFlash.Stop(this);
            if (_toast is { } toast && toast.VisibleFor > ActivationDismissGrace)
            {
                toast.Dismiss();
            }
        }

        internal void ShowCompletionToast(ChatDisplayMessage assistant)
        {
            // Прежнюю карточку убираем сразу, без угасания, — чтобы они не наложились.
            var modelId = assistant.ResolvedModelId ?? assistant.RequestedModelId ?? "";
            var id = assistant.Id;
            Notify(
                modelId,
                ToastText(FirstLine(assistant.Text)),
                BuildToastMeta(modelId, assistant.Duration),
                () => OpenFromToast(id));
        }

        /// <summary>
        /// Сбой в чате, который сейчас не на экране. Карточкой, а не модалкой: модальное окно
        /// посреди набора в другом чате перехватило бы ввод из-за чужого сетевого сбоя.
        /// </summary>
        private void ShowBackgroundTurnError(RunningTurn turn, string message)
        {
            var sessionId = turn.SessionId;
            _trayError = true;
            UpdateTrayState();
            Notify(
                turn.Assistant?.ResolvedModelId ?? turn.Assistant?.RequestedModelId ?? "",
                ToastText(FirstLine(message)),
                IsLocked ? "" : Loc.Format("S.Turn.BackgroundFailed", DisplayTitle(turn.Session.Title)),
                () => OpenChat(sessionId),
                warning: true);
        }

        private IntPtr OwnHandle()
        {
            try
            {
                return new System.Windows.Interop.WindowInteropHelper(this).Handle;
            }
            catch (InvalidOperationException)
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>Смотрит ли человек сейчас именно на это окно.</summary>
        private bool IsForeground()
        {
            var own = OwnHandle();
            return own != IntPtr.Zero && GetForegroundWindow() == own;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private void OpenFromToast(string? messageId)
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            TaskbarFlash.Stop(this);
            Activate();
            ScrollToMessage(messageId);
        }

        internal static string FirstLine(string? text)
        {
            var line = (text ?? "")
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (string.IsNullOrWhiteSpace(line))
            {
                return Loc.Get("S.Message.NoText");
            }

            return line.Length <= 160 ? line : line[..160] + "…";
        }

        /// <summary>Подпись под текстом в карточке уведомления: модель и сколько шёл ответ.</summary>
        /// <remarks>
        /// Длительность берётся тем же <see cref="ChatFormat.Duration"/>, что и шапка ответа:
        /// «4s», «1m5s». Прежде здесь стояли русские «с» и «мин» литералами — на другом языке
        /// они такими и оставались, да и об одном и том же ходе карточка и чат говорили
        /// по-разному. Единицы намеренно не переводятся и от числа не отрываются.
        /// </remarks>
        internal static string BuildToastMeta(string modelId, TimeSpan duration)
        {
            var name = VeniceModelCatalog.GetDisplayName(modelId);
            if (duration <= TimeSpan.Zero)
            {
                return name;
            }

            var elapsed = ChatFormat.Duration(duration);
            return string.IsNullOrWhiteSpace(name) ? elapsed : $"{name} · {elapsed}";
        }

        private void ScrollToMessage(string? id)
        {
            if (string.IsNullOrEmpty(id) || !_messageViews.TryGetValue(id, out var host))
            {
                return;
            }

            // Сообщение может быть ещё не построено: без этого прокрутка привела бы к пустому
            // месту нужной высоты.
            MaterializeHost(host);

            // Чтобы автопрокрутка не утащила ленту обратно вниз.
            _stickToBottom = false;
            Dispatcher.BeginInvoke(host.BringIntoView, DispatcherPriority.Loaded);
        }

        // ───────────────────────── Белый список загрузок ─────────────────────────

        private List<string> AllowedDomains
        {
            get
            {
                if (_services is null)
                {
                    return [];
                }

                return _services.Settings.DownloadAllowedDomains ??= [];
            }
        }

        /// <summary>Список живёт на подстранице «Безопасность › Источники загрузки».</summary>
        private void RefreshAllowedDomainsUi() => BuiltPage<SettingsSecurityPage>()?.ShowDomains(AllowedDomains);

        private void SaveAllowedDomains()
        {
            if (_services is null)
            {
                return;
            }

            _services.SettingsStore.Save(_services.Settings);
            DownloadValidator.ConfigureAllowedDomains(_services.Settings.DownloadAllowedDomains);
            RefreshAllowedDomainsUi();
        }

        private void OpenDomainDialog(string host)
        {
            if (_services is null)
            {
                return;
            }

            DomainError.Visibility = Visibility.Collapsed;
            DomainInput.Text = host ?? "";
            DomainOverlay.Visibility = Visibility.Visible;
            ChatBlocked = true;
            Dispatcher.BeginInvoke(() =>
            {
                DomainInput.Focus();
                DomainInput.SelectAll();
            }, DispatcherPriority.Input);
        }

        /// <summary>
        /// Чат заслонён оверлеем: мышь не доходит ни до ленты, ни до композера.
        /// </summary>
        /// <remarks>
        /// Заслонка (<c>ChatShield</c>), а не <c>IsHitTestVisible</c> у <c>Chat</c>: то свойство
        /// наследуется, и каждое переключение обходило всё дерево ленты — на чате в 1200
        /// сообщений по 45 мс, и вопрос об опасном действии, пришедший посреди ответа,
        /// останавливал поток окна дважды.
        /// </remarks>
        internal bool ChatBlocked
        {
            get => ChatShield.Visibility == Visibility.Visible;
            set => ChatShield.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CloseDomainDialog()
        {
            DomainOverlay.Visibility = Visibility.Collapsed;
            ChatBlocked = ConfirmationOverlay.Visibility == Visibility.Visible;
        }

        private void DomainCancelButton_Click(object sender, RoutedEventArgs e) => CloseDomainDialog();

        private void DomainAddButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            if (!DomainList.TryAdd(AllowedDomains, DomainInput.Text, out var error))
            {
                DomainError.Text = error;
                DomainError.Visibility = Visibility.Visible;
                return;
            }

            AllowedDomains.Sort(StringComparer.OrdinalIgnoreCase);
            SaveAllowedDomains();
            CloseDomainDialog();
        }

        private void DomainInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                DomainAddButton_Click(sender, e);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseDomainDialog();
            }
        }

        private void DomainInput_TextChanged(object sender, TextChangedEventArgs e) =>
            DomainError.Visibility = Visibility.Collapsed;

        // ───────────────────────── Запрос разрешения на загрузку ─────────────────────────

        private sealed class DomainRequest
        {
            public required string Host { get; init; }

            public required TaskCompletionSource<bool> Completion { get; init; }
        }

        private readonly Queue<DomainRequest> _domainRequests = new();
        private DomainRequest? _shownDomainRequest;

        /// <summary>
        /// Инструмент загрузки упёрся в белый список. Показываем запрос и ждём ответа: «да» —
        /// домен уходит в настройки и загрузка продолжается сама, «нет» — инструмент вернёт отказ.
        /// Вызывается из фонового потока агента, поэтому всё, что трогает окно, идёт через Ui.
        /// </summary>
        private Task<bool> RequestDownloadDomainAsync(string host, CancellationToken cancellationToken)
        {
            if (DomainList.Normalize(host) is not { } normalized)
            {
                return Task.FromResult(false);
            }

            var request = new DomainRequest
            {
                Host = normalized,
                Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            };

            var registration = cancellationToken.Register(
                () => Ui(() => CompleteDomainRequest(request, allowed: false)));
            _ = request.Completion.Task.ContinueWith(
                _ => registration.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            Ui(() =>
            {
                _domainRequests.Enqueue(request);
                ShowNextDomainRequest();
            });

            return request.Completion.Task;
        }

        private void ShowNextDomainRequest()
        {
            if (_shownDomainRequest is not null)
            {
                return;
            }

            while (_domainRequests.TryDequeue(out var next))
            {
                // Отменённые запросы уже завершены — их незачем показывать.
                if (next.Completion.Task.IsCompleted)
                {
                    continue;
                }

                _shownDomainRequest = next;
                DownloadRequestText.Text = Loc.Get("S.Confirm.DownloadDesc");
                DownloadRequestHost.Text = next.Host;
                DownloadRequestOverlay.Visibility = Visibility.Visible;
                ChatBlocked = true;
                return;
            }

            DownloadRequestOverlay.Visibility = Visibility.Collapsed;
            ChatBlocked = ConfirmationOverlay.Visibility == Visibility.Visible;
        }

        private void CompleteDomainRequest(DomainRequest request, bool allowed)
        {
            request.Completion.TrySetResult(allowed);
            if (!ReferenceEquals(_shownDomainRequest, request))
            {
                return;
            }

            _shownDomainRequest = null;
            ShowNextDomainRequest();
        }

        private void DownloadRequestAllowButton_Click(object sender, RoutedEventArgs e)
        {
            if (_shownDomainRequest is not { } request || _services is null)
            {
                return;
            }

            if (!DownloadValidator.IsDomainAllowed(request.Host))
            {
                DomainList.TryAdd(AllowedDomains, request.Host, out _);
                AllowedDomains.Sort(StringComparer.OrdinalIgnoreCase);
                SaveAllowedDomains();
            }

            CompleteDomainRequest(request, allowed: true);
        }

        private void DownloadRequestDenyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_shownDomainRequest is { } request)
            {
                CompleteDomainRequest(request, allowed: false);
            }
        }

        private void NavSecurity_Checked(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            SecurityPage.Attach(_services);
            SecurityPage.Load(_services.Settings);
        }

        private void SettingsModelPicked(object? sender, ModelBinding binding)
        {
            if (_settingsUiLoading || _services is null || string.IsNullOrWhiteSpace(binding.ModelId))
            {
                return;
            }

            foreach (var (slot, field, reasoning) in SettingsSlots())
            {
                if (!ReferenceEquals(sender, field))
                {
                    continue;
                }

                // Модель и ключ пишутся вместе: порознь их забывают записать по одному, и слот
                // остаётся с ключом провайдера, к которому его модель больше не принадлежит.
                ModelSlots.WriteBinding(_services.Settings, slot, binding);
                reasoning.SetModel(binding.ModelId);

                if (slot == ModelSlot.SynGuard)
                {
                    ShowSynGuardModelName();
                }

                _services.SettingsStore.Save(_services.Settings);
                return;
            }
        }

        private void SettingsPickerAddKeyRequested(object? sender, EventArgs e) => OpenKeyDialog();

        /// <summary>
        /// Человек выбрал, через кого и каким ключом искать в интернете.
        /// </summary>
        /// <remarks>
        /// Модель здесь не спрашивается: её подбирает <see cref="ModelSlots.WebSearch"/> под
        /// выбранного провайдера. Пустой провайдер — «как у модели хода», прежнее поведение.
        /// </remarks>
        private void WebSearchTargetChanged(object? sender, WebSearchTarget target)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            _services.Settings.WebSearchProvider = target.Provider;
            _services.Settings.WebSearchKeyId = target.KeyId;
            _services.Settings.WebSearchEngine = target.Engine;
            _services.Settings.WebSearchEngineMode = target.Mode;
            _services.SettingsStore.Save(_services.Settings);
        }

        private void SettingsPickerProviderShown(object? sender, LlmProvider provider) =>
            Detached.Run(LoadProviderCatalogAsync(provider), "load_model_catalog");

        private void SettingsReasoningChanged(object? sender, ReasoningChoiceChangedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            var slot = SlotForReasoningPicker(sender);
            if (slot is null)
            {
                return;
            }

            slot.DisableThinking = e.DisableThinking;
            slot.ReasoningEffort = e.Effort;
            _services.SettingsStore.Save(_services.Settings);
        }

        private ReasoningSettings? SlotForReasoningPicker(object? sender)
        {
            if (_services is null)
            {
                return null;
            }

            var settings = _services.Settings;
            var models = BuiltPage<SettingsModelsPage>();
            var security = BuiltPage<SettingsSecurityPage>();
            if (ReferenceEquals(sender, models?.LiteReasoningPicker))
            {
                return settings.LiteReasoning ??= new ReasoningSettings();
            }

            if (ReferenceEquals(sender, models?.HeavyReasoningPicker))
            {
                return settings.HeavyReasoning ??= new ReasoningSettings();
            }

            if (ReferenceEquals(sender, models?.RouterReasoningPicker))
            {
                return settings.RouterReasoning ??= new ReasoningSettings();
            }

            if (ReferenceEquals(sender, models?.TitleReasoningPicker))
            {
                return settings.TitleReasoning ??= new ReasoningSettings();
            }

            if (ReferenceEquals(sender, models?.AgentFastReasoningPicker))
            {
                return settings.AgentFastReasoning ??= new ReasoningSettings();
            }

            if (ReferenceEquals(sender, models?.AgentLiteReasoningPicker))
            {
                return settings.AgentLiteReasoning ??= new ReasoningSettings();
            }

            if (ReferenceEquals(sender, models?.AgentHeavyReasoningPicker))
            {
                return settings.AgentHeavyReasoning ??= new ReasoningSettings();
            }

            if (ReferenceEquals(sender, security?.SynGuardReasoningPicker))
            {
                return settings.SynGuardReasoning ??= new ReasoningSettings();
            }

            if (ReferenceEquals(sender, models?.SummaryReasoningPicker))
            {
                return settings.SummaryReasoning ??= new ReasoningSettings();
            }

            return null;
        }

        private void SynGuardToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            // Ни предупреждения, ни подтверждения: выключить защиту — осознанный выбор человека,
            // и переспрашивать о нём — то же самое, что не давать её выключить.
            _services.Settings.SynGuardEnabled = SecurityPage.SynGuardToggle.IsChecked == true;
            _services.SettingsStore.Save(_services.Settings);
        }

        /// <summary>
        /// Подпись раскрывающегося пункта — голый идентификатор модели, а не человеческое имя:
        /// строка служебная, и в ней важно точно знать, что именно стоит в настройке.
        /// </summary>
        private void ShowSynGuardModelName()
        {
            if (BuiltPage<SettingsSecurityPage>() is { } security)
            {
                security.SynGuardModelLabel.Text = _services is null
                    ? SynGuard.FallbackModelId
                    : SynGuard.ResolveModel(_services.Settings);
            }
        }

        private void ChatModelPicker_ModelPicked(object sender, ModelBinding binding)
        {
            ModelButton.IsChecked = false;
            _session.SelectedModelId = binding.ModelId;
            _session.SelectedKeyId = binding.KeyId;
            if (_services is not null)
            {
                ModelSlots.WriteBinding(_services.Settings, ModelSlot.Chat, binding);
                _services.SettingsStore.Save(_services.Settings);
                // Модель чата хранится в сессии — сохраняем сейчас, а не ждём случайного
                // следующего сохранения.
                PersistCurrent();
            }

            UpdateModelButton();
        }

        /// <summary>
        /// Ключ сменили — плашку не закрываем: человек обычно тут же выбирает под него модель.
        /// </summary>
        private void ChatModelPicker_KeyPicked(object sender, ModelBinding binding)
        {
            _session.SelectedKeyId = binding.KeyId;
            if (_services is not null)
            {
                ModelSlots.WriteBinding(_services.Settings, ModelSlot.Chat, binding);
                _services.SettingsStore.Save(_services.Settings);
                PersistCurrent();
            }

            UpdateModelButton();
        }

        private void ChatModelPicker_AddKeyRequested(object sender, EventArgs e)
        {
            ModelButton.IsChecked = false;
            OpenKeyDialog();
        }

        private void ModelPicker_ProviderShown(object sender, LlmProvider provider) =>
            Detached.Run(LoadProviderCatalogAsync(provider), "load_model_catalog");

        private void ChatReasoningPicker_ChoiceChanged(object sender, ReasoningChoiceChangedEventArgs e)
        {
            if (VeniceModelCatalog.IsAuto(CurrentModelId()))
            {
                return;
            }

            _session.DisableThinking = e.DisableThinking;
            _session.ReasoningEffort = e.Effort;
            if (_services is null)
            {
                return;
            }

            var chat = _services.Settings.ChatReasoning ??= new ReasoningSettings();
            chat.DisableThinking = e.DisableThinking;
            chat.ReasoningEffort = e.Effort;
            _services.SettingsStore.Save(_services.Settings);
            PersistCurrent();
        }

        private void ModelPicker_Opened(object sender, EventArgs e)
        {
            PushKeysToPickers();
            ChatModelPicker.SetSelected(CurrentModelId(), CurrentKeyId());
            ChatModelPicker.ShowSelectedProvider();
            Detached.Run(LoadModelCatalogAsync(), "load_model_catalog");
        }

        /// <summary>
        /// Раздаёт плашкам список ключей: из него собирается правый столбец выбора.
        /// </summary>
        /// <remarks>
        /// Плашки о хранилище ключей не знают намеренно — они живут в разметке и обязаны
        /// собираться в тестах без профиля и без ключей вовсе.
        /// </remarks>
        /// <summary>
        /// Провайдеры, чей каталог уже разошёлся по плашкам и вылечил слоты.
        /// </summary>
        /// <remarks>
        /// Раздача тянет за собой лечение выбора, сохранение настроек и обновление девяти
        /// плашек, восьми полей размышления и кольца контекста. Делать это повторно на каждое
        /// нажатие в столбце провайдеров незачем — список от нажатия не меняется.
        /// </remarks>
        private readonly HashSet<LlmProvider> _appliedCatalogs = [];

        private void PushKeysToPickers()
        {
            if (_services is null)
            {
                return;
            }

            var keys = _services.KeyStore.List();
            ChatModelPicker.SetKeys(keys);
            foreach (var field in SettingsPickers())
            {
                field.SetKeys(keys);
            }

            BuiltPage<SettingsModelsPage>()?.WebSearchField.SetKeys(keys);
        }

        /// <summary>
        /// Подвозит каталоги всех провайдеров, у которых есть чем платить.
        /// </summary>
        /// <remarks>
        /// Всех, а не одного: слоты теперь стоят у разных провайдеров, и лечение выбора обязано
        /// сверяться с каталогом того провайдера, которому слот принадлежит. Провайдера без
        /// ключа не трогаем — запрос без ключа вернулся бы четырёхсоткой.
        /// </remarks>
        private async Task LoadModelCatalogAsync()
        {
            if (_services is null)
            {
                return;
            }

            foreach (var spec in ProviderSpec.All)
            {
                if (_services.Keys.HasKeyFor(spec.Provider))
                {
                    await LoadProviderCatalogAsync(spec.Provider);
                }
            }
        }

        private async Task LoadProviderCatalogAsync(LlmProvider provider)
        {
            if (_services is null || !_services.Keys.HasKeyFor(provider))
            {
                return;
            }

            if (_services.Models.CachedFor(provider) is { } cached)
            {
                // Раздан — значит слоты уже вылечены, а плашки уже знают этот список. Второй
                // проход по всему конвейеру на каждое нажатие провайдера и был тем подвисанием.
                if (_appliedCatalogs.Add(provider))
                {
                    ApplyCatalog(provider, cached, error: null);
                }

                return;
            }

            ChatModelPicker.ShowLoading(provider);
            foreach (var field in SettingsPickers())
            {
                field.ShowLoading(provider);
            }

            var models = await _services.Models.GetAgenticAsync(provider);
            _appliedCatalogs.Add(provider);
            ApplyCatalog(provider, models, models.Count == 0 ? _services.Models.ErrorFor(provider) : null);
        }

        private void ApplyCatalog(LlmProvider provider, IReadOnlyList<VeniceModelInfo> models, string? error)
        {
            HealUnusableModelSelections(provider, models);
            ChatModelPicker.SetCatalog(provider, models, error);
            ChatModelPicker.SetSelected(CurrentModelId(), CurrentKeyId());
            foreach (var field in SettingsPickers())
            {
                field.SetCatalog(provider, models, error);
            }

            // Поля страницы «Customize» заполняются при открытии настроек, а каталог доезжает
            // и потом — например, когда человек добавил ключ, не закрывая окна. Тогда в полях
            // осталось бы имя модели, которой в новом списке нет вовсе: выглядит это как
            // сброшенная настройка, хотя выбор цел.
            ShowSettingsModelSelections();

            foreach (var picker in SettingsReasoningPickers())
            {
                picker.SetCatalog(models);
            }

            ChatReasoningPicker.SetCatalog(models);
            UpdateReasoningPicker();
            BindSettingsReasoningPickers();
            RefreshContextRing();
        }

        /// <summary>
        /// Возвращает выбор моделей в каталог, если сохранённая модель из него пропала.
        /// </summary>
        /// <remarks>
        /// Убрать модель из списка мало: выбранная лежит в settings.json и в самом чате, и
        /// запросы продолжали уходить к ней. Именно так вела себя inkling — она отвечала
        /// четырёхсоткой на каждый ход, а сменить её было негде, потому что из списка она уже
        /// исчезла. Чиним при загрузке каталога, а не в каждом ходе: подмена на лету была бы
        /// незаметной, а здесь человек видит в поле новую модель.
        /// </remarks>
        private void HealUnusableModelSelections(
            LlmProvider provider,
            IReadOnlyList<VeniceModelInfo> models)
        {
            if (_services is null || models.Count == 0)
            {
                return;
            }

            var settings = _services.Settings;
            var changed = false;

            if (Belongs(_session.SelectedModelId, provider) &&
                !VeniceModelCatalog.IsSelectable(models, _session.SelectedModelId))
            {
                // Пустая строка — «бери из настроек», а не «нет модели».
                _session.SelectedModelId = "";
                _session.SelectedKeyId = null;
                PersistCurrent();
            }

            foreach (var (slot, read, write) in ModelSlots.All)
            {
                var current = read(settings) ?? "";

                // Сверяем слот только с каталогом его собственного провайдера: у слота,
                // стоящего у соседа, этой модели в списке нет и быть не должно — вылечив его
                // здесь, мы бы молча перетащили заголовки чатов к другому провайдеру.
                if (!Belongs(current, provider))
                {
                    continue;
                }

                if (VeniceModelCatalog.IsSelectable(models, current))
                {
                    // Модель на месте, а вот назначенный ключ мог исчезнуть вместе с удалённой
                    // строкой: возвращаем слот к ключу по умолчанию, а не к отказу в запросе.
                    if (ModelSlots.ReadKey(settings, slot) is { } keyId && !KeyExists(keyId))
                    {
                        ModelSlots.WriteKey(settings, slot, null);
                        changed = true;
                    }

                    continue;
                }

                // У Venice пустая строка в слоте чата означает «взять модель из
                // appsettings.json», и обнулить слот достаточно. У остальных провайдеров такой
                // модели нет — им слот заполняется явно, иначе каждый ход уходил бы к чужому
                // идентификатору.
                if (slot == ModelSlot.Chat && provider == LlmProvider.Venice)
                {
                    settings.ChatModelId = "";
                    ModelSlots.WriteKey(settings, slot, null);
                    changed = true;
                    continue;
                }

                var replacement = ModelSlotDefaults.Resolve(provider, slot, models);
                if (replacement.Length == 0)
                {
                    continue;
                }

                write(settings, replacement);
                ModelSlots.WriteKey(settings, slot, null);
                changed = true;
            }

            if (changed)
            {
                _services.SettingsStore.Save(settings);
            }
        }

        /// <summary>
        /// Стоит ли эта модель у названного провайдера. Пустой слот — ничей: его лечит тот
        /// провайдер, чьим ключом программа платит по умолчанию.
        /// </summary>
        private bool Belongs(string? modelId, LlmProvider provider)
        {
            if (string.IsNullOrWhiteSpace(modelId) || VeniceModelCatalog.IsAuto(modelId))
            {
                return _services is not null && _services.Keys.CurrentProvider == provider;
            }

            return ModelRef.Of(modelId) == provider;
        }

        private bool KeyExists(string keyId)
        {
            if (_services is null)
            {
                return true;
            }

            foreach (var key in _services.Keys.Keys)
            {
                if (key.Id.Equals(keyId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Показывает в полях настроек то, что сейчас выбрано. Читает настройки, а не хранит
        /// своё: выбор меняется и мимо этих полей — лечением каталога.
        /// </summary>
        private void ShowSettingsModelSelections()
        {
            if (_services is null)
            {
                return;
            }

            foreach (var (slot, field, _) in SettingsSlots())
            {
                var binding = ModelSlots.Binding(_services.Settings, slot);
                field.SetSelected(binding.ModelId, binding.KeyId);
            }

            BuiltPage<SettingsModelsPage>()?.WebSearchField.SetSelected(
                _services.Settings.WebSearchProvider,
                _services.Settings.WebSearchKeyId,
                _services.Settings.WebSearchEngine,
                _services.Settings.WebSearchEngineMode);

            ShowSynGuardModelName();
        }

        /// <summary>
        /// Служебные слоты вместе с их полем и выбором размышления.
        /// </summary>
        /// <remarks>
        /// Одной таблицей, а не цепочкой сравнений на каждый случай: слот, поле и размышление
        /// всегда ходят втроём, и разъехались бы они молча — при добавлении десятого слота.
        /// </remarks>
        private (ModelSlot Slot, ModelPickerField Field, ReasoningPicker Reasoning)[] SettingsSlots()
        {
            // Только построенные страницы: таблицу читают и запуск, и доехавший каталог, а
            // непостроенная страница, созданная позже, заполнится из настроек сама.
            var slots = new List<(ModelSlot Slot, ModelPickerField Field, ReasoningPicker Reasoning)>();
            if (BuiltPage<SettingsModelsPage>() is { } models)
            {
                slots.AddRange(
                [
                    (ModelSlot.Lite, models.LiteModelPicker, models.LiteReasoningPicker),
                    (ModelSlot.Heavy, models.HeavyModelPicker, models.HeavyReasoningPicker),
                    (ModelSlot.Router, models.RouterModelPicker, models.RouterReasoningPicker),
                    (ModelSlot.Title, models.TitleModelPicker, models.TitleReasoningPicker),
                    (ModelSlot.AgentFast, models.AgentFastModelPicker, models.AgentFastReasoningPicker),
                    (ModelSlot.AgentLite, models.AgentLiteModelPicker, models.AgentLiteReasoningPicker),
                    (ModelSlot.AgentHeavy, models.AgentHeavyModelPicker, models.AgentHeavyReasoningPicker),
                    (ModelSlot.Summary, models.SummaryModelPicker, models.SummaryReasoningPicker)
                ]);
            }

            if (BuiltPage<SettingsSecurityPage>() is { } security)
            {
                slots.Add((ModelSlot.SynGuard, security.SynGuardModelPicker, security.SynGuardReasoningPicker));
            }

            return [.. slots];
        }

        private ModelPickerField[] SettingsPickers() => [.. SettingsSlots().Select(slot => slot.Field)];

        private ReasoningPicker[] SettingsReasoningPickers() => [.. SettingsSlots().Select(slot => slot.Reasoning)];

        private void SaveMainPromptButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            _services.Settings.MainPrompt = PromptsPage.MainPromptTextBox.Text ?? "";
            _services.SettingsStore.Save(_services.Settings);
        }

        private void SaveTechAiPromptButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            _services.Settings.TechAiPrompt = PromptsPage.TechAiPromptTextBox.Text ?? "";
            _services.SettingsStore.Save(_services.Settings);
            RefreshPromptLinks();
        }

        private void SaveTechAgentPromptButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            _services.Settings.TechAgentPrompt = PromptsPage.TechAgentPromptTextBox.Text ?? "";
            _services.SettingsStore.Save(_services.Settings);
            RefreshPromptLinks();
        }

        /// <summary>
        /// Значение у строки «Технические промпты ›»: заводские или изменены. Нетронутая копия
        /// нынешнего заводского текста — тоже заводская: «Сохранить» кладёт текст целиком.
        /// </summary>
        private void RefreshPromptLinks()
        {
            if (_services is null || BuiltPage<SettingsPromptsPage>() is not { } prompts)
            {
                return;
            }

            var settings = _services.Settings;
            var changed = Differs(settings.TechAiPrompt, ChatEngine.DefaultTechPrompt) ||
                          Differs(settings.TechAgentPrompt, Agent.BaseSystemPrompt);
            prompts.TechPromptsLinkRow.Tag = Loc.Get(changed ? "S.Prompts.Tech.Changed" : "S.Prompts.Tech.Default");

            static bool Differs(string? saved, string factory) =>
                !string.IsNullOrWhiteSpace(saved) && !string.Equals(saved.Trim(), factory.Trim(), StringComparison.Ordinal);
        }

        /// <summary>
        /// Возвращает техническому промпту заводской текст — и сразу его сохраняет.
        /// </summary>
        /// <remarks>
        /// В настройки пишется пустая строка: это и есть штатный признак «заводской промпт»,
        /// по нему их подставляют <c>ChatEngine.BuildSystemPrompt</c> и <see cref="Agent"/>.
        /// Записав туда сам текст, мы заморозили бы сегодняшнюю редакцию промпта навсегда —
        /// правки в следующих версиях до такого человека уже не дошли бы.
        /// </remarks>
        private void ResetTechAiPromptButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            _services.Settings.TechAiPrompt = "";
            _services.SettingsStore.Save(_services.Settings);
            PromptsPage.TechAiPromptTextBox.Text = ChatEngine.DefaultTechPrompt;
            RefreshPromptLinks();
        }

        /// <inheritdoc cref="ResetTechAiPromptButton_Click"/>
        private void ResetTechAgentPromptButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            _services.Settings.TechAgentPrompt = "";
            _services.SettingsStore.Save(_services.Settings);
            PromptsPage.TechAgentPromptTextBox.Text = Agent.BaseSystemPrompt;
            RefreshPromptLinks();
        }

        /// <summary>
        /// Перечитывает настройки и применяет к окну то, что в них про окно; страницы настроек —
        /// перечитанными: видимую сразу, остальные — когда их покажут.
        /// </summary>
        /// <remarks>
        /// Сюда приходят открытие настроек, смена профиля и импорт архива: у другого профиля
        /// тема, оформление, углы и масштаб могут быть другими. Непостроенным страницам
        /// перечитывать нечего — построенные позже заполнятся из того же <see cref="AppSettings"/>.
        /// </remarks>
        private void LoadSettingsUi()
        {
            if (_services is null)
            {
                return;
            }

            using var timer = PerfLog.Measure("settings_open");

            _services.ReloadSettings();
            var settings = _services.Settings;
            ThemeManager.Apply(settings.Theme);
            ApplyAppearance(save: false);
            WindowCornerStyle.Apply(this, settings.WindowCorners);
            ApplyUiScaleFromSettings();

            // Имя и аватар в боковой колонке — у другого профиля свои.
            LoadAccountUi();

            if (_settingsView is null)
            {
                return;
            }

            foreach (var page in _settingsView.BuiltPages)
            {
                _staleSettingsPages.Add(page);
            }

            if (_settingsView.CurrentPage is { } current && _staleSettingsPages.Remove(current))
            {
                LoadSettingsPage(current);
            }

            // Обход диска — только когда открыта сама «Хранение и очистка». Считать при каждом
            // заходе в настройки значило читать все файлы чатов ради разбивки, которую человек
            // чаще всего и не смотрит. Вне флага загрузки: обход асинхронный, и держать на нём
            // _settingsUiLoading значило бы глушить обработчики остальных настроек.
            if (BuiltPage<SettingsDataPage>() is { DataCareSub.IsVisible: true })
            {
                Detached.Run(RefreshDataUsageAsync(), "refresh_data_usage");
            }
        }

        /// <summary>
        /// Порядок даты из настроек. До появления служб — заводской: окно успевает нарисовать
        /// ленту раньше, чем к нему прикрутят AppServices.
        /// </summary>
        private DateFormat ActiveDateFormat => _services?.Settings.DateFormat ?? DateFormat.DayMonthShort;

        private void ApplyUiScaleFromSettings()
        {
            if (_services is null)
            {
                return;
            }

            var percent = UiScale.Normalize(_services.Settings.UiScalePercent);
            if (_services.Settings.UiScalePercent != percent)
            {
                _services.Settings.UiScalePercent = percent;
                _services.SettingsStore.Save(_services.Settings);
            }

            UiScale.Apply(this, ScaledRoot, percent);
        }

        private void SelectUiScale(int percent)
        {
            percent = UiScale.Normalize(percent);
            for (var i = 0; i < AppearancePage.UiScaleComboBox.Items.Count; i++)
            {
                if (AppearancePage.UiScaleComboBox.Items[i] is ComboBoxItem item &&
                    int.TryParse(Convert.ToString(item.Tag), out var value) &&
                    value == percent)
                {
                    AppearancePage.UiScaleComboBox.SelectedIndex = i;
                    return;
                }
            }

            AppearancePage.UiScaleComboBox.SelectedIndex = 2;
        }

        private int ReadUiScaleCombo()
        {
            if (AppearancePage.UiScaleComboBox.SelectedItem is ComboBoxItem item &&
                int.TryParse(Convert.ToString(item.Tag), out var percent))
            {
                return UiScale.Normalize(percent);
            }

            return 100;
        }

        private void CollapseSidebarButton_Click(object sender, RoutedEventArgs e) => SetSidebarCollapsed(true);

        /// <summary>
        /// Свёрнутую колонку логотип разворачивает, раскрытой — заводит новый чат.
        /// </summary>
        /// <remarks>
        /// В раскрытой колонке кнопка прежде не делала ничего: на логотип нажимают по привычке
        /// веб-клиентов, где он ведёт «на главную», а главная у чата — пустой разговор.
        /// </remarks>
        private void SidebarLogoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_sidebarCollapsed)
            {
                SetSidebarCollapsed(false);
                return;
            }

            StartNewChatFromUi();
        }

        private void SidebarLogoButton_MouseEnter(object sender, MouseEventArgs e) => UpdateLogoGlyph(hover: true);

        private void SidebarLogoButton_MouseLeave(object sender, MouseEventArgs e) => UpdateLogoGlyph(hover: false);

        private void NewChatButton_Click(object sender, RoutedEventArgs e) => StartNewChatFromUi();

        /// <summary>Заводит новый чат: кнопкой в колонке или горячей клавишей.</summary>
        internal void StartNewChatFromUi()
        {
            if (_services is null)
            {
                return;
            }

            PersistCurrent();
            StashDraft();
            StartNewSession(persist: false);
            RestoreDraft(_session);
            RenderSession();
            RefreshChatList();
            MessageTextBox.Focus();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Вставленный код «Поделиться» — не запрос поиска: открываем переписку из него.
            if (ChatShareCodec.LooksLikeShareCode(SearchBox.Text))
            {
                var shared = ChatShareCodec.TryDecode(SearchBox.Text);
                SearchBox.Clear();
                if (shared is null)
                {
                    Inform(Loc.Get("S.Share.OpenFailedTitle"), Loc.Get("S.Share.CodeBroken"));
                    return;
                }

                OpenSharedSession(shared);
                return;
            }

            // Запрос изменился — прошлый ответ модели относился к другому вопросу.
            SyncSearchModeRow();
            ResetContentSearch();
            RefreshChatList();
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            // После щелчка фокус остаётся на кнопке: каретке там хуже, чем в поле, куда человек
            // сейчас снова будет печатать, а при сжатом поле ввода фокус на панели кнопок ещё и
            // держал бы полоску развёрнутой весь ход.
            FocusMessageInput();
            Detached.Run(SendAsync(), "send");
        }

        private void FocusMessageInput()
        {
            if (MessageTextBox is null)
            {
                return;
            }

            MessageTextBox.Focus();
            MessageTextBox.CaretIndex = MessageTextBox.Text?.Length ?? 0;

            // Высота поля ограничена, и длинный текст в нём прокручивается. Каретку в конец
            // ставим мы сами, а сама по себе она в кадр не приезжает — без этого человек видел бы
            // начало чужого промпта и не понимал, куда он печатает.
            MessageTextBox.ScrollToEnd();
        }

        private bool ShouldKeepKeyboardFocus()
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
                Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                return true;
            }

            // До любого чтения элемента с фокусом и без него: журнал берёт фокус при открытии, но
            // до первого щелчка бывает миг, когда фокуса нет ни у кого, и ранний выход ниже отдал
            // бы эти нажатия полю ввода — вместе с Escape, который должен закрыть журнал.
            if (JournalOverlay.Visibility == Visibility.Visible || HealthOverlay.Visibility == Visibility.Visible ||
                IsNoticeOpen)
            {
                return true;
            }

            if (Keyboard.FocusedElement is not DependencyObject focused)
            {
                return false;
            }

            if (ReferenceEquals(focused, MessageTextBox))
            {
                return true;
            }

            if (TakesTypedText(focused))
            {
                return true;
            }

            if (SettingsOverlay.Visibility == Visibility.Visible && IsInside(SettingsOverlay, focused))
            {
                return true;
            }

            // Пока открыт просмотр, стрелки и Escape — его; иначе окно отдало бы их полю ввода.
            if (ImageViewerOverlay.Visibility == Visibility.Visible)
            {
                return true;
            }

            if (ConfirmationOverlay.Visibility == Visibility.Visible &&
                IsInside(ConfirmationOverlay, focused))
            {
                return true;
            }

            return false;
        }

        /// <summary>Элемент сам принимает набранный текст — забирать его в поле сообщения нельзя.</summary>
        /// <remarks>
        /// Поле пароля — не <see cref="TextBox"/>, и до 1.28.0 его здесь не было: набранный в нём
        /// пароль (архив данных, экран блокировки) окно перекладывало бы в поле сообщения, то
        /// есть в чат с моделью.
        /// </remarks>
        internal static bool TakesTypedText(DependencyObject focused) => focused switch
        {
            TextBox { IsReadOnly: false } => true,
            System.Windows.Controls.RichTextBox { IsReadOnly: false } => true,
            PasswordBox => true,
            _ => false
        };

        private static bool IsInside(DependencyObject root, DependencyObject node)
        {
            for (DependencyObject? current = node; current is not null;)
            {
                if (ReferenceEquals(current, root))
                {
                    return true;
                }

                current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
            }

            return false;
        }

        private void InsertIntoMessageBox(string text)
        {
            var box = MessageTextBox;
            var start = box.SelectionStart;
            var length = box.SelectionLength;
            var current = box.Text ?? "";
            if (length > 0)
            {
                current = current.Remove(start, length);
            }

            box.Text = current.Insert(start, text);
            box.CaretIndex = start + text.Length;
            box.ScrollToEnd();
        }

        private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            NoteInput();
            if (KeepKeyboardOnLock(e))
            {
                return;
            }

            if (ShouldKeepKeyboardFocus() || string.IsNullOrEmpty(e.Text) || e.Text == "\b")
            {
                return;
            }

            FocusMessageInput();
            InsertIntoMessageBox(e.Text);
            e.Handled = true;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            NoteInput();
            if (KeepKeyboardOnLock(e))
            {
                return;
            }

            if (e.Key == Key.Escape && _chatDrag?.Cancel() == true)
            {
                e.Handled = true;
                return;
            }

            // Уведомление модально: Escape отвечает «нет», а горячие клавиши и лупа ждут —
            // «новый чат» под открытым вопросом увёл бы из-под него тот чат, о котором спрашивают.
            if (IsNoticeOpen)
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    CloseNotice(false);
                }

                return;
            }

            // Esc в настройках сперва возвращает с подстраницы «›» на её страницу.
            if (e.Key == Key.Escape && SettingsOverlay.IsVisible && SettingsDrill.TryBackIn(SettingsOverlay))
            {
                e.Handled = true;
                return;
            }

            // Раньше проверки фокуса: приблизить ленту можно и не уходя из поля ввода, и выйти
            // из этого вида человек попросит оттуда же.
            if (e.Key == Key.Escape && _chatZoom?.TryHandleEscape() == true)
            {
                e.Handled = true;
                return;
            }

            // Esc во время записи голоса (D14) отменяет запись — раньше всего остального.
            if (e.Key == Key.Escape && IsRecording && CancelRecording())
            {
                e.Handled = true;
                return;
            }

            // Esc останавливает ответ (D8) — после лупы: пока лента приближена, Esc снимает её.
            if (TryStopByEscape(e))
            {
                e.Handled = true;
                return;
            }

            // Тоже раньше проверки фокуса, и по той же причине: ShouldKeepKeyboardFocus
            // уступает событие полю ввода при любом Ctrl или Alt, а сочетание без модификатора
            // назначить нельзя — ниже этой строки ни одно из них не дожило бы.
            if (TryRunHotkey(e))
            {
                e.Handled = true;
                return;
            }

            if (ShouldKeepKeyboardFocus())
            {
                return;
            }

            if (e.Key == Key.Back)
            {
                FocusMessageInput();
                var box = MessageTextBox;
                if (box.SelectionLength > 0)
                {
                    InsertIntoMessageBox("");
                }
                else if (box.CaretIndex > 0)
                {
                    var index = box.CaretIndex;
                    box.Text = box.Text.Remove(index - 1, 1);
                    box.CaretIndex = index - 1;
                }

                e.Handled = true;
                return;
            }

            if (e.Key == Key.Space)
            {
                FocusMessageInput();
                InsertIntoMessageBox(" ");
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                FocusMessageInput();
                e.Handled = true;
                Detached.Run(SendAsync(), "send");
            }
        }

        private void MessageTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Раньше отправки: пока открыта подсказка «@», Enter выбирает цитату, а не шлёт
            // недописанное сообщение.
            if (TryHandleQuoteSuggestKey(e) || TryHandleCommandSuggestKey(e))
            {
                e.Handled = true;
                return;
            }

            if (TryRecallLastSent(e))
            {
                e.Handled = true;
                return;
            }

            // Ctrl+V прикладывает картинку, если она в буфере; иначе вставку делает само поле,
            // и текст вставляется как обычно.
            if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (TryPasteAttachmentFromClipboard())
                {
                    e.Handled = true;
                }

                return;
            }

            if (e.Key != Key.Enter)
            {
                return;
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                return;
            }

            e.Handled = true;
            Detached.Run(SendAsync(), "send");
        }

        /// <summary>
        /// Отправка из поля ввода. Что делать — решает <see cref="SendPlanner.ForComposer"/>; окно
        /// показывает отказ или запускает ход.
        /// </summary>
        internal async Task SendAsync()
        {
            if (_services is null)
            {
                return;
            }

            var services = _services;
            var draft = new OutgoingDraft(MessageTextBox.Text, _pendingImages.Count, _pendingFiles.Count, _pendingQuotes.Count);
            var targetGone = _session.TargetMachineId is { } targetId && services.Machines.Find(targetId) is null;
            var plan = SendPlanner.ForComposer(draft, HasUsableKey(), IsBusy(_session.Id), targetGone, Turns.HasRoom);

            // Если ход кончился между проверкой и вызовом, QueueFollowUp так и скажет, и
            // сообщение уйдёт обычным, а не пропадёт.
            if (plan.Verdict == SendVerdict.FollowUp && !QueueFollowUp(plan.Text, plan.Command is not null))
            {
                plan = SendPlanner.ForComposer(draft, HasUsableKey(), busy: false, targetGone, Turns.HasRoom);
            }

            switch (plan.Verdict)
            {
                case SendVerdict.Nothing:
                case SendVerdict.FollowUp:
                    return;

                case SendVerdict.NeedsText:
                    FocusMessageInput();
                    return;

                case SendVerdict.LocalCommand when plan.Local is { } local:
                    MessageTextBox.Clear();
                    CloseCommandSuggest();
                    RunLocalCommand(local);
                    return;

                case SendVerdict.NoKey:
                    ShowNoKeyNotice();
                    return;

                case SendVerdict.AgentNoAttachments:
                    ShowAgentRefusal("S.Turn.AgentNoAttachments");
                    return;

                case SendVerdict.AgentNoQuotes:
                    ShowAgentRefusal("S.Turn.AgentNoQuotes");
                    return;

                case SendVerdict.LimitReached:
                    ShowTurnLimitNotice();
                    return;

                // Человек думает, что команды уходят на ту машину. Текст остаётся в поле.
                case SendVerdict.TargetGone:
                    await ShowNoticeAsync(Loc.Get("S.Remote.GoneTitle"), Loc.Get("S.Remote.GoneText"), Loc.Get("S.Common.Close"), null);
                    return;
            }

            var text = plan.Text;
            MessageTextBox.Clear();
            var images = _pendingImages.Count == 0 ? null : _pendingImages.ToArray();
            var files = _pendingFiles.Count == 0 ? null : _pendingFiles.ToArray();
            var quotes = _pendingQuotes.Count == 0 ? null : _pendingQuotes.ToArray();
            ClearPendingAttachments();
            ForgetDraft(_session);

            var session = _session;
            if (plan is { Verdict: SendVerdict.Agent, Command: { } agent })
            {
                await RunTurnAsync(session, TurnKind.AgentCommand, (chat, observer, token) =>
                    services.Chat.RunAgentCommandAsync(
                        chat, text, agent.Argument, agent.Complexity, observer, token));
                return;
            }

            await RunTurnAsync(session, TurnKind.Send, (chat, observer, token) =>
                services.Chat.RunTurnAsync(chat, text, images, files, quotes, observer, token));
        }

        private void StartNewSession(bool persist)
        {
            _stickToBottom = true;
            _session = _services?.ChatStore.CreateNew(CurrentModelId()) ?? new ChatSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = ChatTitle.Default,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            RefreshTargetPicker();
            ApplyChatReasoningDefaults();
            if (persist)
            {
                PersistCurrent();
            }

            RenderSession();
            UpdateModelButton();
            FocusMessageInput();
        }

        private void ApplyChatReasoningDefaults()
        {
            var reasoning = _services?.Settings.ChatReasoning ?? new ReasoningSettings();
            _session.DisableThinking = reasoning.DisableThinking;
            _session.ReasoningEffort = reasoning.ReasoningEffort;
        }

        private void LoadSession(ChatSession session)
        {
            _stickToBottom = true;
            ResetFindForChat();
            _session = session;
            RefreshTargetPicker();
            // Ссылки на картинки в прежних ответах разрешаются, только пока картинки
            // зарегистрированы, а реестр перезапуск не переживает.
            ChatImageRegistry.RestoreAll(session);
            RenderSession();
            UpdateModelButton();
            FocusMessageInput();
        }

        /// <summary>
        /// Показывает открытый чат. Строится только видимая часть переписки — остальное
        /// достраивается в фоне и по прокрутке, см. <c>MainWindow.Messages.cs</c>.
        /// </summary>
        private void RenderSession() => RebuildTranscript(resetZoom: true);

        /// <summary>
        /// То же, что <see cref="RenderSession"/>, но с выбором: сбрасывать ли лупу.
        /// </summary>
        /// <param name="resetZoom">
        /// Вернуть ленте обычный вид. Так и есть у <see cref="RenderSession"/>: жест относится
        /// к тому, что человек читал, и открывать соседний чат приближённым он не просил.
        /// Перерисовки того же самого чата — смена формата даты, смена языка — передают
        /// <c>false</c>: содержимое перед глазами то же, и терять приближение не за что.
        /// </param>
        /// <remarks>
        /// Отдельным именем, а не необязательным параметром у <see cref="RenderSession"/>:
        /// тесты зовут ту через отражение, а <c>MethodBase.Invoke</c> значений по умолчанию не
        /// подставляет.
        /// </remarks>
        private void RebuildTranscript(bool resetZoom)
        {
            using var timer = PerfLog.Measure("chat_render");

            if (resetZoom)
            {
                ResetChatZoom();
            }

            _variantGate.IsOpen = !IsBusy(_session.Id);
            BuildMessageHosts();

            if (FindTurn(_session.Id) is { } live)
            {
                ResumeLiveRendering(live);
            }

            // Подпись под композером принадлежит чату: при переходе показывается его, а не
            // та, что осталась от разговора, из которого ушли.
            UpdateAttachmentWarning();

            // Удаление, «Повторить» и смена языка тоже идут сюда: подпись источника у
            // прикреплённой цитаты («из последнего ответа», «удалён») могла стать неправдой.
            RefreshQuoteRows();
            MaybeAutoscroll();
            ScheduleBackgroundFill();
        }

        /// <summary>
        /// Возобновляет показ ответа, который шёл, пока смотрели другой чат.
        /// </summary>
        /// <remarks>
        /// Тело перерисовываем сразу, не дожидаясь тика: у хода, который дописал текст и молча
        /// крутит инструменты, дельт больше не будет вовсе — пузырь остался бы пустым. Часы
        /// считаем от начала хода, иначе они обнулялись бы при каждом переключении чата.
        /// </remarks>
        private void ResumeLiveRendering(RunningTurn turn)
        {
            if (_liveAssistant is null || turn.Finished)
            {
                return;
            }

            _workingStarted = turn.StartedAt;
            turn.RenderedText = turn.Assistant?.Text ?? turn.PendingText;

            // Вьюшку только что построил RenderSession — из того же сообщения и, значит, из
            // того же текста. Второй разбор разметки подряд не менял на экране ничего.
            if (!string.IsNullOrEmpty(turn.RenderedText) &&
                !string.Equals(_liveAssistant.BodyText, turn.RenderedText, StringComparison.Ordinal))
            {
                _liveAssistant.SetBody(turn.RenderedText, streaming: true);
            }

            if (turn.Assistant is not null && !ReferenceEquals(turn.Assistant, _liveAssistant.ToolsSource))
            {
                _liveAssistant.UpdateTools(turn.Assistant);
            }

            _liveAssistant.ShowWorking(DateTime.Now - turn.StartedAt);
            _liveAssistant.ShowCancelOnly();
            _workingTimer.Start();
            StartStreamRendering();
        }

        /// <summary>Уходим с чата, который ещё отвечает: гасим только показ, сам ход продолжается.</summary>
        private void StopVisibleRendering()
        {
            _workingTimer.Stop();
            _streamRender.Stop();
            _liveAssistant = null;
        }

        /// <param name="session">
        /// Чат, которому принадлежат кнопки. Захватывается по значению: ряд действий переживает
        /// перерисовку, и «стоп», собранный для чата А, иначе остановил бы открытый чат Б.
        /// </param>
        private MessageActions CreateMessageActions(ChatSession session) => new()
        {
            Copy = CopyMessage,
            CommitEdit = CommitUserEdit,
            Delete = DeleteAssistant,
            Regenerate = RegenerateAssistant,
            SwitchVariant = SwitchVariant,
            VariantGate = _variantGate,
            Continue = ResumeAssistant,
            CanContinue = message => CanResume(session, message),
            Cancel = _ => CancelTurn(session.Id),
            Share = ShareMessage,
            Export = ExportMessage,
            SharingEnabled = SharingEnabled,
            AddDownloadDomain = OpenDomainDialog,
            Transcript = () => session.Messages,
            ShowQuoteSource = quote => ShowQuoteSource(session, quote),
            CurrentDateFormat = () => ActiveDateFormat,
            OpenInstruction = OpenInstruction,
            InstructionExists = InstructionExists
        };

        private static void CopyMessage(ChatDisplayMessage message)
        {
            var text = message.Text ?? "";
            if (text.Length == 0)
            {
                return;
            }

            try
            {
                Clipboard.SetText(text);
            }
            catch
            {
                // clipboard can be locked by another process
            }
        }

        /// <summary>
        /// Правка вопроса: исправленный вопрос встаёт новым вариантом, а прежний со всем, что
        /// за ним шло, остаётся соседним — его можно вернуть переключателем под вопросом.
        /// </summary>
        /// <remarks>
        /// Новое сообщение, а не правка старого на месте: у прежнего варианта должен остаться
        /// прежний вопрос, иначе вернувшийся ответ отвечал бы не на то, что над ним написано.
        /// Вложения и цитаты переезжают копией — человек правил только текст.
        /// </remarks>
        private void CommitUserEdit(ChatDisplayMessage message, string text)
        {
            if (_services is null || IsBusy(_session.Id))
            {
                return;
            }

            text = text.Trim();
            var index = _session.Messages.IndexOf(message);
            if (text.Length == 0 || index < 0 || !message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Все проверки — до развилки (см. SendPlanner.ForRerun).
            var plan = SendPlanner.ForRerun(
                new OutgoingDraft(text, message.Images.Count, message.Files.Count, message.Quotes.Count),
                HasUsableKey(),
                Turns.HasRoom);
            switch (plan.Verdict)
            {
                case SendVerdict.NoKey:
                    ShowNoKeyNotice();
                    return;

                case SendVerdict.AgentNoAttachments:
                    ShowAgentRefusal("S.Turn.AgentNoAttachments");
                    return;

                case SendVerdict.AgentNoQuotes:
                    ShowAgentRefusal("S.Turn.AgentNoQuotes");
                    return;

                case SendVerdict.LimitReached:
                    ShowTurnLimitNotice();
                    return;
            }

            var command = plan.Command;

            var anchor = new ChatDisplayMessage
            {
                Role = "user",
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.Now,
                Text = text,
                Images = [.. message.Images],
                Files = [.. message.Files],
                Quotes = [.. message.Quotes]
            };

            var services = _services;
            var kind = command is { Name: ChatCommands.Agent } ? TurnKind.AgentCommand : TurnKind.Continue;
            Detached.Run(RunTurnAsync(_session, kind, (chat, observer, token) =>
            {
                var at = chat.Messages.IndexOf(message);
                if (at < 0)
                {
                    return Task.CompletedTask;
                }

                // Команда кладёт в историю задачу, а не видимый текст, — как при отправке.
                ChatBranches.Fork(chat, at, anchor, command is { } agent
                    ? () => ChatEngine.AgentCommandTurn(agent.Argument)
                    : () => new ChatMessage
                    {
                        Role = "user",
                        Content = ChatContent.ForUser(anchor, chat.Messages, chat.Messages.Count - 1)
                    });
                ShowForkedTail(chat);

                return command is { } run
                    ? services.Chat.RunAgentCommandAsync(chat, text, run.Argument, run.Complexity, observer, token, placed: anchor)
                    : services.Chat.GenerateAssistantAsync(chat, observer, token);
            }), "edit_turn");
        }

        /// <summary>
        /// Удаление ответа. Есть у него другие варианты — удаляется показанный и встаёт соседний;
        /// нет — прежнее удаление хода.
        /// </summary>
        private void DeleteAssistant(ChatDisplayMessage message)
        {
            if (_services is null || IsBusy(_session.Id))
            {
                return;
            }

            var group = ChatBranches.FindGroupFor(_session, message);
            if (group < 0)
            {
                DeleteTurn(message);
                return;
            }

            // Вариант уносит с собой всё продолжение, а прежнее удаление хода поздние ходы
            // сохраняло. Только свой ход — то, что человек видит под рукой; есть и поздние —
            // спрашиваем.
            var ownTurn = _session.Messages[group].Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (ChatBranches.TurnsFrom(_session, group) - ownTurn > 0)
            {
                Detached.Run(ConfirmVariantDeleteAsync(_session, message), "delete_variant");
                return;
            }

            DeleteVariant(_session, message);
        }

        private async Task ConfirmVariantDeleteAsync(ChatSession session, ChatDisplayMessage message)
        {
            var confirmed = await ShowNoticeAsync(
                Loc.Get("S.Variant.DeleteTitle"),
                Loc.Get("S.Variant.DeleteText"),
                Loc.Get("S.Common.Delete"),
                Loc.Get("S.Common.Cancel"),
                NoticeTone.Danger);

            // Пока окно было открыто, человек мог уйти в другой чат или запустить ответ.
            if (confirmed && ReferenceEquals(session, _session) && !IsBusy(session.Id))
            {
                DeleteVariant(session, message);
            }
        }

        private void DeleteVariant(ChatSession session, ChatDisplayMessage message)
        {
            var group = ChatBranches.FindGroupFor(session, message);
            if (group < 0 || ChatBranches.DeleteActive(session, group) is null)
            {
                return;
            }

            ReconcileTranscript(null);
            PersistCurrent();
            RefreshChatList();
        }

        private void DeleteTurn(ChatDisplayMessage message)
        {
            if (!ChatSessionEdit.DeleteTurn(_session, message.Id))
            {
                return;
            }

            if (_session.Messages.Count == 0)
            {
                DiscardEmptySession();
                return;
            }

            ReconcileTranscript(null);
            PersistCurrent();
            RefreshChatList();
        }

        /// <summary>
        /// Показывает другой вариант продолжения переписки с этого сообщения.
        /// </summary>
        /// <param name="target">Место варианта среди всех, с нуля.</param>
        private void SwitchVariant(ChatDisplayMessage message, int target)
        {
            if (_services is null)
            {
                return;
            }

            // Стрелки гаснут на время ответа, но нажатие могло проскочить в тот же кадр.
            if (IsBusy(_session.Id))
            {
                ShowComposerNotice(Loc.Get("S.Variant.Busy"));
                return;
            }

            var index = _session.Messages.IndexOf(message);
            if (index < 0 || ChatBranches.Switch(_session, index, target) is null)
            {
                return;
            }

            // Лента меняет только хвост: лупа и место чтения остаются.
            ReconcileTranscript(null);
            PersistCurrent();
            RefreshChatList();
        }

        /// <summary>Развилка сделана: лента показывает новый хвост, чат сохраняется сразу.</summary>
        /// <remarks>
        /// Сохранение — сразу, а не по таймеру: на диске до этой строки лежит прежний вариант
        /// без спрятанного хвоста, и сбой до первого планового сохранения потерял бы его.
        /// </remarks>
        private void ShowForkedTail(ChatSession chat)
        {
            if (ReferenceEquals(chat, _session))
            {
                ReconcileTranscript(null);
            }

            Persist(chat);
        }

        private void ShowAgentRefusal(string textKey) => Detached.Run(
            ShowNoticeAsync(Loc.Get("S.Notice.AgentTitle"), Loc.Get(textKey), Loc.Get("S.Common.Close"), null),
            "agent_refusal");

        private void DiscardEmptySession()
        {
            var id = _session.Id;
            if (_services is not null && !string.IsNullOrWhiteSpace(id))
            {
                _services.ChatStore.Delete(id);
                ForgetAttention(id);
            }

            StartNewSession(persist: false);
            RefreshChatList();
        }

        /// <summary>
        /// Отдаёт строку уже идущему в этом чате ходу вместо отказа.
        /// </summary>
        /// <remarks>
        /// Движок вплетает её в контекст на границе раунда, не мешая идущей работе — инструменту
        /// и запущенным агентам. Рисуется строка здесь, а не движком: человек должен увидеть её
        /// сразу по Enter, а раунд может идти минуту.
        /// </remarks>
        /// <returns>False — хода, к которому добавить, уже нет.</returns>
        private bool QueueFollowUp(string text, bool isCommand)
        {
            if (FindTurn(_session.Id) is not { } turn)
            {
                return false;
            }

            // Команда через «/» выбирает вид хода, а второго хода здесь нет; «/agent …» обычным
            // текстом молча значил бы другое.
            if (isCommand)
            {
                ShowComposerNotice(Loc.Get("S.Turn.NoCommandWhileBusy"));
                return true;
            }

            // Вложения едут в составном сообщении, которое собирается на старте хода; контекст
            // этого хода снят раунды назад, и приложить их некуда.
            if (_pendingImages.Count > 0 || _pendingFiles.Count > 0)
            {
                ShowComposerNotice(Loc.Get("S.Turn.NoAttachmentsWhileBusy"));
                return true;
            }

            MessageTextBox.Clear();

            // Цитаты, в отличие от вложений, для модели — текст и вплетаются в саму строку: блок
            // собирается сейчас, по ленте в её нынешнем виде.
            var user = new ChatDisplayMessage
            {
                Role = "user",
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.Now,
                Text = text,
                Quotes = [.. _pendingQuotes]
            };
            _session.Messages.Add(user);
            var queued = ChatQuotes.Wrap(text, user.Quotes, _session.Messages, _session.Messages.Count - 1);
            ClearPendingAttachments();
            ForgetDraft(_session);

            var userRoot = ChatMessageViews.CreateUser(this, user, CreateMessageActions(_session), ActiveDateFormat).Root;
            AppendMessage(user, userRoot);
            MaybeAutoscroll();
            RefreshChatList();

            turn.Enqueue(queued);
            ShowComposerNotice(Loc.Get("S.Turn.Queued"));
            return true;
        }

        /// <summary>
        /// «Повторить»: новый ответ встаёт вариантом, прежний со всем продолжением остаётся
        /// соседним.
        /// </summary>
        /// <remarks>
        /// Все проверки — до развилки. Раньше «Повторить» без ключа сначала стирало ответ и
        /// только потом сообщало, что ключа нет; с вариантами отказ после развилки оставил бы
        /// прежний ответ спрятанным, а на его месте — пустой пузырь. Сама развилка — внутри
        /// хода, когда он уже заведён: между «спрятали» и «появился новый» нет мгновения, в
        /// которое сохранение записало бы чат без обоих ответов.
        /// </remarks>
        private void RegenerateAssistant(ChatDisplayMessage message)
        {
            if (_services is null || IsBusy(_session.Id) || message.Status is AssistantStatus.Streaming)
            {
                return;
            }

            var index = _session.Messages.IndexOf(message);
            if (index < 0)
            {
                return;
            }

            if (!HasUsableKey())
            {
                ShowNoKeyNotice();
                return;
            }

            var question = _session.Messages
                .Take(index)
                .LastOrDefault(item => item.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
            if (!ChatEngine.IsRequest(question))
            {
                return;
            }

            if (!Turns.HasRoom)
            {
                ShowTurnLimitNotice();
                return;
            }

            var anchor = new ChatDisplayMessage
            {
                Role = "assistant",
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.Now,
                Status = AssistantStatus.Streaming
            };

            var services = _services;
            Detached.Run(RunTurnAsync(_session, TurnKind.Continue, (chat, observer, token) =>
            {
                var at = chat.Messages.IndexOf(message);
                if (at < 0)
                {
                    return Task.CompletedTask;
                }

                ChatBranches.Fork(chat, at, anchor);
                ShowForkedTail(chat);
                return services.Chat.GenerateAssistantAsync(chat, observer, token, into: anchor);
            }), "regenerate");
        }

        /// <summary>
        /// Есть ли что продолжать: ответ оборвался и стоит последним в чате.
        /// </summary>
        /// <remarks>
        /// Только последний — потому что продолжение дописывает тот же ответ, а у ответа из
        /// середины переписки ниже уже стоят другие реплики, и дописанное оказалось бы не на
        /// своём месте. Для них остаётся «Повторить».
        /// </remarks>
        private static bool CanResume(ChatSession session, ChatDisplayMessage message) =>
            message.Status is AssistantStatus.Cancelled or AssistantStatus.Error &&
            session.Messages.Count > 0 &&
            ReferenceEquals(session.Messages[^1], message);

        /// <summary>
        /// Возобновляет прерванный ответ, ничего не выбрасывая.
        /// </summary>
        /// <remarks>
        /// В отличие от <see cref="RegenerateAssistant"/> здесь нет нового варианта: тот начал бы
        /// ответ заново, и за уже сделанную работу инструментов человек заплатил бы второй раз.
        /// </remarks>
        private void ResumeAssistant(ChatDisplayMessage message)
        {
            if (_services is null || IsBusy(_session.Id) || !CanResume(_session, message))
            {
                return;
            }

            if (!HasUsableKey())
            {
                ShowNoKeyNotice();
                return;
            }

            Detached.Run(
                RunTurnAsync(_session, TurnKind.Continue, (chat, observer, token) =>
                    _services.Chat.ResumeAssistantAsync(chat, message, observer, token)),
                "resume_turn");
        }

        private void MaybeStartTitle(ChatSession session, ChatDisplayMessage user)
        {
            if (_services is null || !ChatTitle.IsDefault(session.Title))
            {
                return;
            }

            if (session.Messages.Count(item => item.Role == "user") != 1)
            {
                return;
            }

            var sessionId = session.Id;
            var text = user.Text;
            Detached.Run(GenerateTitleAsync(sessionId, text), "generate_title");
        }

        private async Task GenerateTitleAsync(string sessionId, string userText)
        {
            if (_services is null)
            {
                return;
            }

            try
            {
                var draft = await _services.Titles.GenerateAsync(userText);

                Ui(() =>
                {
                    // Чат мог уйти в фон, пока заголовок придумывался, — ищем его и там.
                    var session = string.Equals(_session.Id, sessionId, StringComparison.Ordinal)
                        ? _session
                        : FindTurn(sessionId)?.Session;
                    if (session is null)
                    {
                        return;
                    }

                    // Цену записываем даже тогда, когда сам заголовок не удался: деньги
                    // потрачены, а человек видит в списке всё тот же «Новый чат».
                    var repriced = ChatTitleCost.Book(session, draft.Cost);
                    var renamed = !string.IsNullOrWhiteSpace(draft.Title);
                    if (!renamed && repriced is null)
                    {
                        return;
                    }

                    if (renamed)
                    {
                        session.Title = draft.Title!;
                    }

                    // Счёт уже закрытого ответа изменился. Ценник и его разбивка собираются при
                    // отрисовке из самого сообщения, поэтому пузырь пересобирается заново —
                    // но только он один: перерисовка всего чата сбрасывала бы лупу.
                    if (repriced is not null && string.Equals(_session.Id, sessionId, StringComparison.Ordinal))
                    {
                        RefreshMessageView(repriced.Id);
                    }

                    Persist(session);
                    RefreshChatList();
                });
            }
            catch
            {
                // keep «Новый чат»
            }
        }

        /// <summary>Слепок списка, по которому видно, изменилось ли в нём хоть что-нибудь.</summary>
        /// <remarks>
        /// Два буфера, которые меняются местами, а не строка на каждый вызов: список обновляют по
        /// несколько раз в секунду во время ответа, и на тысяче чатов слепок — это сотня килобайт
        /// мусора за раз. Сравнение по содержимому (<see cref="System.Text.StringBuilder.Equals(System.Text.StringBuilder)"/>)
        /// остаётся точным.
        /// </remarks>
        private System.Text.StringBuilder _chatListSignature = new();
        private System.Text.StringBuilder _chatListScratch = new();

        /// <summary>Следующий <see cref="RefreshChatList"/> пересоберёт панель, даже если состав не менялся.</summary>
        /// <remarks>Выдачу поиска по тексту — тоже: её подписи (язык, даты) рисуются при сборке.</remarks>
        private void InvalidateChatListSignature()
        {
            _chatListSignature.Clear();
            _textSearchShown = null;

            // Подписи строк (язык, даты) рисуются при постройке — оставлять прежние нельзя.
            ForgetChatListRows();
        }

        private void RefreshChatList()
        {
            if (ChatListPanel is null)
            {
                return;
            }

            if (_services is null)
            {
                ForgetChatListRows();
                ChatListPanel.Children.Clear();
                return;
            }

            // Список переходов (G6) — пять последних чатов; пересобирается, только если они сменились.
            UpdateJumpList(_services.ChatStore.List());

            var query = SearchBox.Text;

            // Поиск по тексту — своя выдача: находки в сообщениях, а не строки чатов.
            if (_searchByText && query.Trim().Length > 0)
            {
                RenderTextSearch(query);
                return;
            }

            // Панель сейчас перерисуется списком: выдача по тексту, если была, больше не на экране.
            _textSearchShown = null;
            _textSearchCancel?.Cancel();
            _textSearchCancel = null;

            var items = ChatListItems(query);
            var organize = OrganizeSnapshot();
            var sort = _services.Settings.ChatSort;
            if (_tagFilter is not null && organize.Tags.All(tag => tag.Id != _tagFilter))
            {
                _tagFilter = null;
            }

            RefreshTagFilterPill(organize, query);

            // Перерисовка стоит полной пересборки панели, а зовут её и фоновые ходы — по
            // несколько раз за секунду. Если состав списка не изменился, строки остаются на
            // месте, а признаки на них правятся поштучно.
            var signature = _chatListScratch.Clear();
            ChatListSignature.Append(
                signature,
                new ChatListView(query, _searchByContent, (int)_contentSearchState, sort, _tagFilter, _archiveExpanded, DateTime.Today),
                items,
                organize);
            if (signature.Equals(_chatListSignature) && ChatListPanel.Children.Count > 0)
            {
                RefreshChatRowStates();
                return;
            }

            // Пересборка из-под перетаскиваемой строки сорвала бы жест — она подождёт его конца.
            if (_chatDrag is { IsDragging: true } drag)
            {
                drag.RefreshPending = true;
                return;
            }

            // Панель не сносится: RenderChatListNodes сверяет её с прежней раскладкой и трогает
            // только изменившиеся строки.
            (_chatListSignature, _chatListScratch) = (signature, _chatListSignature);

            IReadOnlyList<ChatListNode> nodes;
            var searching = (IsContentSearchResult && items.Count > 0) || query.Trim().Length > 0;
            if (searching)
            {
                // Выдача поиска — одним списком. У ответа модели порядок — это близость к запросу,
                // и разложить его по «Сегодня» и «Вчера» значило бы перемешать ответ; у поиска по
                // заголовку папки и архив прятали бы найденное за свёрнутыми группами.
                var ordered = IsContentSearchResult ? items : ChatListLayout.Sort(items, sort).ToList();
                nodes = ChatListLayout.Flat(ordered, organize, "S.Search.Found");
            }
            else
            {
                nodes = ChatListLayout.Build(items, organize, sort, _tagFilter, _archiveExpanded, DateTime.Today);
            }

            // Первый кадр запуска строит только видимое (RefreshChatListFirstScreen). Подпись
            // забывается: иначе следующая сверка сочла бы недостроенный список показанным.
            if (nodes.Count > _chatListRowLimit)
            {
                nodes = nodes.Take(_chatListRowLimit).ToList();
                _chatListSignature.Clear();
                _chatListCutShort = true;
            }

            RenderChatListNodes(nodes, droppable: !searching);
            UpdateBatchBar();

            if (ChatListPanel.Children.Count == 0 && !string.IsNullOrWhiteSpace(query))
            {
                var empty = new TextBlock
                {
                    // В режиме «по чатам» пусто ещё не значит «не нашлось»: поиск мог и не
                    // начинаться, и звать это «ничего не найдено» было бы неправдой.
                    Text = ChatListEmptyText(),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(14, 8, 10, 4)
                };

                // Через ресурс, а не кистью: захардкоженный серый не менялся вместе с темой.
                empty.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
                ChatListPanel.Children.Add(empty);
            }
        }

        /// <summary>
        /// Как назвать чат в интерфейсе.
        /// </summary>
        /// <remarks>
        /// На диске заголовок нового чата — постоянная <see cref="ChatTitle.Default"/>, и она же
        /// служит признаком «заголовок ещё не придуман». Переводить её нельзя: после смены языка
        /// прежние чаты перестали бы опознаваться как безымянные. Поэтому переводится только показ.
        /// </remarks>
        internal static string DisplayTitle(string? title) =>
            ChatTitle.IsDefault(title) ? Loc.Get("S.ChatList.NewChat") : title!;

        /// <summary>
        /// Признаки строки: открыт ли этот чат, идёт ли в нём ход, ждёт ли он внимания.
        /// </summary>
        /// <remarks>
        /// Всё три — присоединённые свойства, которые ловит шаблон. Прежде «открыт» был вторым
        /// стилем, а «думает» доставалось прямым обращением в шаблон через <c>ApplyTemplate</c>:
        /// строку приходилось разворачивать в визуалы немедленно, а смена любого из состояний
        /// означала пересборку всей панели.
        /// </remarks>
        private void ApplyChatRowState(Button row, string sessionId)
        {
            Flip(row, ChatRowState.IsActiveProperty,
                string.Equals(sessionId, _session.Id, StringComparison.Ordinal));
            Flip(row, ChatRowState.IsWorkingProperty, IsBusy(sessionId));
            Flip(row, ChatRowState.NeedsAttentionProperty, Turns.NeedsAttention(sessionId));
            Flip(row, ChatRowState.IsSelectedProperty, _selection.Contains(sessionId));
        }

        /// <summary>
        /// Ставит признак, только если он изменился: <see cref="RefreshChatRowStates"/> зовётся
        /// на каждое сохранение чата, то есть несколько раз в секунду во время ответа.
        /// </summary>
        private static void Flip(DependencyObject row, DependencyProperty property, bool value)
        {
            if ((bool)row.GetValue(property) != value)
            {
                row.SetValue(property, value);
            }
        }

        /// <summary>Переставляет признаки на уже стоящих строках, не трогая саму панель.</summary>
        private void RefreshChatRowStates()
        {
            foreach (var child in ChatListPanel.Children)
            {
                if (child is Button { Tag: string id } row)
                {
                    ApplyChatRowState(row, id);
                }
            }
        }

        /// <summary>Открыть чат. Ход, идущий в нём или в прежнем, не прерывается.</summary>
        private void OpenChat(string id)
        {
            if (_services is null || id == _session.Id)
            {
                return;
            }

            using var timer = PerfLog.Measure("chat_open");
            PersistCurrent();

            // Набранное и прикреплённое принадлежит тому чату, где его набрали (D12): уходит в
            // его черновик, а открытый чат получает свой.
            StashDraft();

            // Метку «ответ готов» снимаем до загрузки: RefreshChatList ниже уже нарисует строку
            // без неё, и лишней перерисовки не будет.
            Turns.ForgetAttention(id);

            // Если по чату идёт ход — берём ЕГО объект сессии, а не читаем копию с диска:
            // движок продолжает писать в свой, и на экране оказалась бы застывшая копия.
            var live = FindTurn(id)?.Session;
            var loaded = live ?? _services.ChatStore.TryLoad(id);
            if (loaded is null)
            {
                return;
            }

            if (live is null)
            {
                ChatEngine.CloseInterruptedReplies(loaded);
            }

            LoadSession(loaded);
            RestoreDraft(loaded);
            RefreshChatList();
        }

        /// <summary>Сворачивает или разворачивает боковую панель; лента перекладывается по видимому.</summary>
        private void SetSidebarCollapsed(bool collapsed) => ReflowTranscriptOnce(() => ApplySidebarCollapsed(collapsed));

        private void ApplySidebarCollapsed(bool collapsed)
        {
            _sidebarCollapsed = collapsed;
            SidebarColumn.Width = new GridLength(collapsed ? 42 : SidebarWidths.Clamp(_services?.Settings.SidebarWidth));
            SidebarGrip.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            if (collapsed)
            {
                BatchBar.Visibility = Visibility.Collapsed;
                TagFilterPill.Visibility = Visibility.Collapsed;
            }
            else
            {
                UpdateBatchBar();
                if (_services is { } organized)
                {
                    RefreshTagFilterPill(organized.Organizer.Snapshot(), SearchBox.Text);
                }
            }

            if (_services is { } services && services.Settings.SidebarCollapsed != collapsed)
            {
                services.Settings.SidebarCollapsed = collapsed;
                services.SettingsStore.Save(services.Settings);
            }
            CollapseSidebarButton.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            SearchBorder.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            ChatListPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            AccountAvatar.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            AccountLabels.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            SideBarScrollViewer.VerticalScrollBarVisibility = collapsed
                ? ScrollBarVisibility.Disabled
                : ScrollBarVisibility.Auto;

            if (_newChatLabel is not null)
            {
                _newChatLabel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            }

            if (_newChatPlus is not null)
            {
                _newChatPlus.Margin = collapsed ? new Thickness(0) : new Thickness(0, 0, 6, 0);
            }

            // Свёрнутая колонка шириной 42, кнопка — 30, и поля по 6 съедают ровно 42: на округление
            // при дробном масштабе не остаётся ничего, а переполнение WPF обрезает квадратом и
            // срезает правый край скруглённой подсветки. В свёрнутой колонке полей нет, кнопку
            // ставит центровка: то же место на экране и 12 точек запаса.
            SidebarLogoButton.HorizontalAlignment = collapsed
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Left;
            SidebarLogoButton.Margin = collapsed
                ? new Thickness(0, 6, 0, 6)
                : new Thickness(6);

            // Ссылкой на ресурс, а не готовой строкой: подсказка обязана смениться вместе с языком.
            SidebarLogoButton.SetResourceReference(
                ToolTipProperty,
                collapsed ? "S.Sidebar.Expand" : "S.ChatList.NewChat");

            UpdateLogoGlyph(SidebarLogoButton.IsMouseOver);
        }


        private void UpdateLogoGlyph(bool hover)
        {
            if (_logoImage is null || _expandIcon is null)
            {
                return;
            }

            var showArrows = _sidebarCollapsed && hover;
            _logoImage.Visibility = showArrows ? Visibility.Collapsed : Visibility.Visible;
            _expandIcon.Visibility = showArrows ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateModelButton()
        {
            var modelId = CurrentModelId();
            if (_modelButtonLabel is not null)
            {
                _modelButtonLabel.Text = VeniceModelCatalog.GetDisplayName(modelId);
            }

            ModelBrand.Apply(this, modelId, _modelButtonLogo, _modelButtonLetter, _modelButtonLightning);
            ChatModelPicker.SetSelected(modelId, CurrentKeyId());
            RefreshContextRing();
            UpdateReasoningPicker();
        }

        private void UpdateReasoningPicker()
        {
            var modelId = CurrentModelId();
            var auto = VeniceModelCatalog.IsAuto(modelId);
            ChatReasoningPicker.SetState(
                modelId, auto, _session.DisableThinking, _session.ReasoningEffort);
            ChatReasoningPicker.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BindSettingsReasoningPickers()
        {
            if (_services is null)
            {
                return;
            }

            var settings = _services.Settings;
            if (BuiltPage<SettingsModelsPage>() is { } models)
            {
                BindSlot(models.LiteReasoningPicker, settings.LiteModelId, settings.LiteReasoning);
                BindSlot(models.HeavyReasoningPicker, settings.HeavyModelId, settings.HeavyReasoning);
                BindSlot(models.RouterReasoningPicker, settings.RouterModelId, settings.RouterReasoning);
                BindSlot(models.TitleReasoningPicker, settings.TitleModelId, settings.TitleReasoning);
                BindSlot(models.AgentFastReasoningPicker, settings.AgentFastModelId, settings.AgentFastReasoning);
                BindSlot(models.AgentLiteReasoningPicker, settings.AgentLiteModelId, settings.AgentLiteReasoning);
                BindSlot(models.AgentHeavyReasoningPicker, settings.AgentHeavyModelId, settings.AgentHeavyReasoning);
                BindSlot(models.SummaryReasoningPicker, settings.SummaryModelId, settings.SummaryReasoning);
            }

            if (BuiltPage<SettingsSecurityPage>() is { } security)
            {
                BindSlot(security.SynGuardReasoningPicker, settings.SynGuardModelId, settings.SynGuardReasoning);
            }
        }

        private static void BindSlot(ReasoningPicker picker, string modelId, ReasoningSettings? slot)
        {
            var reasoning = slot ?? new ReasoningSettings();
            picker.SetState(
                modelId,
                // Служебные слоты «Авто» не показывают: у каждого своя конкретная модель.
                autoMode: false,
                reasoning.DisableThinking,
                reasoning.ReasoningEffort);
        }

        private string CurrentModelId()
        {
            if (!string.IsNullOrWhiteSpace(_session.SelectedModelId))
            {
                return _session.SelectedModelId;
            }

            if (_services is not null && !string.IsNullOrWhiteSpace(_services.Settings.ChatModelId))
            {
                return _services.Settings.ChatModelId;
            }

            // Модель из appsettings.json — идентификатор Venice; на ключе другого провайдера
            // её пришлось бы подбирать, иначе в шапке чата стояла бы модель, которой там нет.
            return _services is null
                ? "claude-sonnet-5"
                : ModelSlotDefaults.LastResort(
                    _services.Keys.CurrentProvider, ModelSlot.Chat, _services.Options.Model);
        }

        /// <summary>
        /// Ключ, которым платит открытая переписка. Пусто — ключ по умолчанию для провайдера
        /// её модели.
        /// </summary>
        private string? CurrentKeyId()
        {
            if (!string.IsNullOrWhiteSpace(_session.SelectedModelId))
            {
                return _session.SelectedKeyId;
            }

            return _services is null ? null : ModelSlots.ReadKey(_services.Settings, ModelSlot.Chat);
        }

        private void ChatScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Раньше всех выходов: капсуле важно и движение автопрокрутки, и выросшая лента.
            UpdateScrollJump();

            // Плашка «Ответить» стоит там, где отпустили кнопку, и за текстом не едет: уехавший
            // текст оставил бы её висеть над чужой строкой. Только своя прокрутка ленты:
            // ScrollChanged всплывает и от вложенных — блока кода, внутренностей бокса, — и
            // такая, случившись при выделении, гасила бы плашку в момент её появления.
            if (e.VerticalChange != 0 && ReferenceEquals(e.OriginalSource, ChatScrollViewer))
            {
                HideReplyPill();
            }

            // Ширина ленты меняется: всё замороженное, что оказалось на виду, — сразу на новую ширину.
            if (TranscriptReflowing && ReferenceEquals(e.OriginalSource, ChatScrollViewer) &&
                (e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0 || e.VerticalChange != 0))
            {
                ThawAroundViewport();
            }

            // Видимая область выросла (окно растянули, развернули) — под ней могли открыться
            // недостроенные сообщения. Прежде их достраивала только прокрутка, и до первого
            // движения колеса на их месте стояли пустые резервы.
            if (e.ViewportHeightChange > 0 && ReferenceEquals(e.OriginalSource, ChatScrollViewer) && !_autoScrolling)
            {
                MaterializeAroundViewport();
            }

            if (e.ExtentHeightChange != 0)
            {
                if (_stickToBottom && !_autoScrolling)
                {
                    MaybeAutoscroll();
                }

                return;
            }

            if (e.VerticalChange != 0)
            {
                // Где лента стоит — пересчитывается при любом сдвиге, и нашем тоже. Пока
                // `_autoScrolling` глушил весь обработчик, прокрутка человека в эти мгновения
                // терялась: признак «у низа» оставался поднятым, и следующая порция
                // дорисовки уводила ленту обратно в конец. Наша же прокрутка признак не портит:
                // ScrollToEnd ведёт к низу, а сдвиг под якорь бывает, только когда лента не у низа.
                _stickToBottom = IsChatScrolledToBottom();
                if (_autoScrolling)
                {
                    return;
                }

                // Пока идёт жест лупы, достройку пропускаем: смещение меняется каждый кадр, и
                // проход по всей ленте с пересчётом координат шёл бы по шестьдесят раз в секунду.
                // ChatZoom позовёт её сам, когда жест кончится. Доезд капсулы «в начало / в
                // конец» — тот же случай: по кадру строилось бы всё, что мелькнуло, а по
                // прибытии достройку зовёт MainWindow.ScrollJump.
                if (ChatZoomBusy || SmoothScroll.IsGliding(ChatScrollViewer))
                {
                    return;
                }

                // Листаем к сообщениям, которые ещё не построены: строим их заранее, с запасом
                // в несколько экранов, чтобы на кромке ничего не «появлялось».
                MaterializeAroundViewport();
            }
        }

        private bool IsChatScrolledToBottom()
        {
            var viewer = ChatScrollViewer;
            var max = viewer.ExtentHeight - viewer.ViewportHeight;
            if (max <= 1)
            {
                return true;
            }

            return viewer.VerticalOffset >= max - 48;
        }

        private void MaybeAutoscroll()
        {
            if (!(_services?.Settings.AutoScroll ?? true) || !_stickToBottom)
            {
                return;
            }

            // Never move the view out from under a live flick: SmoothScroll re-asserts its own
            // position on the next frame, so the two would trade corrections once per frame.
            // This is the only thing the chat scroller does that the sidebar's does not.
            // The flick settles on its own, and the next extent change resumes following.
            //
            // Жест лупы — тот же случай, только хуже: во время наезда высота ленты меняется на
            // каждом кадре, и у нижнего края автопрокрутка швыряла бы её в конец шестьдесят раз
            // в секунду, вместо того чтобы дать разглядеть то, на что человек навёлся.
            if (SmoothScroll.IsAnimating(ChatScrollViewer) || ChatZoomBusy)
            {
                return;
            }

            _autoScrolling = true;
            ChatScrollViewer.ScrollToEnd();
            Dispatcher.BeginInvoke(() => _autoScrolling = false, DispatcherPriority.Background);
        }

        private void UpdateWorkingClock()
        {
            if (_liveAssistant is null)
            {
                return;
            }

            _liveAssistant.ShowWorking(DateTime.Now - _workingStarted);
        }

        private void Ui(Action action)
        {
            if (Dispatcher.CheckAccess())
            {
                action();
                return;
            }

            Dispatcher.Invoke(action);
        }

        /// <summary>
        /// То же, что <see cref="Ui"/>, но без ожидания: поток движка ставит работу в очередь
        /// и идёт дальше.
        /// </summary>
        /// <remarks>
        /// Для событий, чей результат вызывающему не нужен. Поток ответа приносит их десятками
        /// в секунду, и на каждом сетевой поток замирал, пока до него дойдёт очередь диспетчера.
        /// Порядок при этом сохраняется: внутри одного приоритета очередь строгая, и синхронный
        /// <see cref="Ui"/> с тем же приоритетом встанет позади уже поставленного.
        /// </remarks>
        private void UiAsync(Action action)
        {
            if (Dispatcher.CheckAccess())
            {
                action();
                return;
            }

            Dispatcher.InvokeAsync(action);
        }

        // ───────────────────────── Сообщения хода ─────────────────────────
        // Ходов может идти несколько, поэтому каждый метод первым делом решает, его ли это чат
        // на экране. Раньше окно само было наблюдателем и всё делало по полю «текущий чат» —
        // ответ фонового хода записался бы не туда.

        void IChatTurnUi.TurnUserAppended(RunningTurn turn, ChatDisplayMessage user) => Ui(() =>
        {
            if (!IsVisibleTurn(turn))
            {
                // Список всё равно обновится сохранением, а рисовать нечего.
                MaybeStartTitle(turn.Session, user);
                return;
            }

            var userRoot = ChatMessageViews.CreateUser(this, user, CreateMessageActions(turn.Session), ActiveDateFormat).Root;
            AppendMessage(user, userRoot);
            MaybeAutoscroll();
            RefreshChatList();
            MaybeStartTitle(turn.Session, user);
        });

        void IChatTurnUi.TurnAssistantStarted(RunningTurn turn, ChatDisplayMessage assistant) => Ui(() =>
        {
            if (!IsVisibleTurn(turn))
            {
                return;
            }

            _workingStarted = turn.StartedAt;
            var view = ChatMessageViews.CreateAssistant(
                this, assistant, CreateMessageActions(turn.Session), ActiveDateFormat);
            _liveAssistant = view;

            // Продолжение возобновляет ответ, который в ленте уже нарисован: старый пузырь
            // подменяется новым на том же месте, иначе рядом встал бы второй с тем же ходом.
            if (!string.IsNullOrEmpty(assistant.Id) &&
                _messageViews.TryGetValue(assistant.Id, out var existing))
            {
                FillHost(existing, view.Root);
            }
            else
            {
                AppendMessage(assistant, view.Root);
            }

            _workingTimer.Start();
            StartStreamRendering();
            MaybeAutoscroll();
        });

        void IChatTurnUi.TurnAssistantText(RunningTurn turn, ChatDisplayMessage assistant) => UiAsync(() =>
        {
            if (!IsVisibleTurn(turn) || _liveAssistant is null)
            {
                return;
            }

            // Только копим: перерисует и подкрутит прокрутку следующий тик _streamRender.
            _liveAssistant.ApplyBranding(this, assistant.ResolvedModelId ?? assistant.RequestedModelId ?? "");
        });

        /// <summary>
        /// Пускает живой рендер с наименьшего шага: прошлый ответ мог быть длинным и оставить
        /// таймер разъехавшимся, а новый начинается с пустого документа.
        /// </summary>
        private void StartStreamRendering()
        {
            _streamRender.Interval = StreamRenderMin;
            _streamRender.Start();
        }

        /// <summary>Показать накопленный кусок ответа, если он изменился с прошлого тика.</summary>
        /// <remarks>
        /// Пересборка документа идёт с нуля и дорожает вместе с длиной ответа, поэтому шаг
        /// подстраивается под её же стоимость: на коротком ответе он остаётся прежними 80 мс,
        /// а на длинном разъезжается, оставляя потоку диспетчера время на кадры. Без этого
        /// к концу большого ответа UI-поток был занят перерисовкой почти целиком.
        /// </remarks>
        private void FlushStreamText()
        {
            var turn = FindTurn(_session.Id);
            if (turn is null || _liveAssistant is null || turn.PendingText == turn.RenderedText)
            {
                return;
            }

            var clock = Stopwatch.StartNew();

            turn.RenderedText = turn.PendingText;
            _liveAssistant.SetBody(turn.RenderedText, streaming: true);

            // Прокрутка только после перерисовки — иначе она считает высоту прошлого кадра.
            MaybeAutoscroll();

            var next = TimeSpan.FromMilliseconds(Math.Clamp(
                clock.Elapsed.TotalMilliseconds * StreamRenderShare,
                StreamRenderMin.TotalMilliseconds,
                StreamRenderMax.TotalMilliseconds));

            if (next != _streamRender.Interval)
            {
                _streamRender.Interval = next;
            }
        }

        void IChatTurnUi.TurnToolsChanged(RunningTurn turn, ChatDisplayMessage assistant) => UiAsync(() =>
        {
            if (!IsVisibleTurn(turn))
            {
                return;
            }

            _liveAssistant?.UpdateTools(assistant);
            MaybeAutoscroll();
        });

        void IChatTurnUi.TurnAssistantCompleted(RunningTurn turn, ChatDisplayMessage assistant) => Ui(() =>
        {
            if (IsVisibleTurn(turn))
            {
                CloseVisibleAnswer(assistant, autoscroll: true);
            }

            // Сводка дописывается на каждом закрытом ответе — и у фонового хода тоже: искать
            // по чату, который отвечал в фоне, человек будет наравне с остальными.
            MaybeUpdateSummary(turn.Session);

            // Тост нужен и фоновому ходу: человек ждёт именно его, глядя в другой чат.
            MaybeShowCompletionToast(assistant);

            // А метка в списке — ровно на тот случай, когда тоста не будет: его глушат, пока окно
            // в фокусе, и фоновый ответ до сих пор не оставлял по себе никакого следа.
            MarkAttention(turn);
        });

        /// <summary>
        /// Ответ закрыт ради дописанного сообщения: следующий пойдёт под ним. Всё как при
        /// завершении, кроме уведомления, — ход продолжается, и «ответ готов» было бы неправдой.
        /// </summary>
        void IChatTurnUi.TurnAssistantContinued(RunningTurn turn, ChatDisplayMessage assistant) => Ui(() =>
        {
            if (IsVisibleTurn(turn))
            {
                CloseVisibleAnswer(assistant, autoscroll: true);
            }
        });

        /// <summary>
        /// Гасит живой показ ответа и дорисовывает его набело.
        /// </summary>
        /// <param name="autoscroll">
        /// Досматривать ли ленту до низа. У отменённого ответа — нет: человек нажал «стоп»
        /// и смотрит туда, где остановился, а не в конец.
        /// </param>
        private void CloseVisibleAnswer(ChatDisplayMessage assistant, bool autoscroll)
        {
            _workingTimer.Stop();
            _streamRender.Stop();

            if (_liveAssistant is not null)
            {
                _liveAssistant.ApplyBranding(
                    this, assistant.ResolvedModelId ?? assistant.RequestedModelId ?? "");
                _liveAssistant.SetBody(assistant.Text, streaming: false);
                _liveAssistant.UpdateTools(assistant);
                _liveAssistant.ShowFinished(assistant);
            }

            _liveAssistant = null;
            if (autoscroll)
            {
                MaybeAutoscroll();
            }
        }

        void IChatTurnUi.TurnAssistantCancelled(RunningTurn turn, ChatDisplayMessage assistant) => Ui(() =>
        {
            if (IsVisibleTurn(turn))
            {
                CloseVisibleAnswer(assistant, autoscroll: false);
            }
        });

        void IChatTurnUi.TurnError(RunningTurn turn, string message) => Ui(() =>
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (IsVisibleTurn(turn))
            {
                // Своим окном, а не MessageBox (см. памятку). Переполненный контекст — не авария,
                // а повод сжать переписку (D10): так и предлагаем.
                Detached.Run(
                    ContextCompaction.IsContextOverflow(message)
                        ? OfferCompactionAsync(message)
                        : ShowNoticeAsync(Loc.Get("S.Turn.ErrorTitle"), message, Loc.Get("S.Common.Close"), null, NoticeTone.Danger),
                    "turn_error");
                return;
            }

            // Модалка посреди набора в другом чате из-за сетевого сбоя в фоновом — это регресс.
            // Карточка в углу называет чат и не перехватывает ввод.
            ShowBackgroundTurnError(turn, message);
        });

        private void OnConfirmationChanged() => Ui(() =>
        {
            ShowNextConfirmation();
            NotifyConfirmationIfAway();
        });

        private void ShowNextConfirmation()
        {
            if (_services is null || !IsLoaded)
            {
                return;
            }

            if (!_services.Confirmations.TryPeek(out var request))
            {
                _shownConfirmation = null;
                ConfirmationAllowToggle.IsChecked = false;
                CancelConfirmationExplain();
                CancelConfirmationWhatIf();
                ConfirmationOverlay.Visibility = Visibility.Collapsed;
                ChatBlocked = false;
                return;
            }

            _shownConfirmation = request;
            ShowConfirmationAllow(request);
            ShowConfirmationChat(request.SessionId);
            // Удалённая машина — прямо в строке, кто спрашивает: решать «да» надо, зная, где это выполнится.
            ConfirmationAgentText.Text = request.Info.Target is { } target
                ? request.AgentLabel + " · " + Loc.Format("S.Remote.OnMachine", target)
                : request.AgentLabel;
            ConfirmationSummaryText.Text = ConfirmationSummary(request.Info);
            FillConfirmationBody(request.Info);

            // Следующий вопрос начинается сверху: прокрутка, оставшаяся от предыдущего, прятала
            // бы от человека первые строки нового — а тут он решает, запускать ли скрипт.
            SmoothScroll.Cancel(ConfirmationBodyScroll);
            ConfirmationBodyScroll.ScrollToTop();

            Detached.Run(ExplainConfirmationAsync(request), "confirmation_explain");
            Detached.Run(ProbeConfirmationAsync(request), "confirmation_whatif");
            ConfirmationOverlay.Visibility = Visibility.Visible;
            ChatBlocked = true;
        }

        /// <summary>
        /// Называет чат, из которого пришёл вопрос, и даёт перейти в него. Само не переключает:
        /// выдёргивать человека из того, что он смотрит, хуже, чем показать название.
        /// </summary>
        private void ShowConfirmationChat(string? sessionId)
        {
            _confirmationSessionId = sessionId;
            if (string.IsNullOrWhiteSpace(sessionId) ||
                string.Equals(sessionId, _session.Id, StringComparison.Ordinal))
            {
                ConfirmationChatButton.Visibility = Visibility.Collapsed;
                return;
            }

            var title = FindTurn(sessionId)?.Session.Title
                        ?? _services?.ChatStore.Search("").FirstOrDefault(item => item.Id == sessionId)?.Title;
            if (string.IsNullOrWhiteSpace(title))
            {
                ConfirmationChatButton.Visibility = Visibility.Collapsed;
                return;
            }

            ConfirmationChatButton.Content = Loc.Format("S.Confirm.FromChat", DisplayTitle(title));
            ConfirmationChatButton.Visibility = Visibility.Visible;
        }

        private string? _confirmationSessionId;

        private void ConfirmationChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (_confirmationSessionId is { Length: > 0 } id)
            {
                OpenChat(id);
                ShowConfirmationChat(id);
            }
        }

        /// <summary>Вопрос, который сейчас на экране. Кнопки отвечают ему, а не голове очереди.</summary>
        private ConfirmationRequest? _shownConfirmation;

        private void ConfirmationYesButton_Click(object sender, RoutedEventArgs e) => AnswerShownConfirmation(true);

        private void ConfirmationNoButton_Click(object sender, RoutedEventArgs e) => AnswerShownConfirmation(false);

        private void ConfirmationAllowTurnButton_Click(object sender, RoutedEventArgs e) =>
            AnswerShownConfirmation(true, AllowanceScope.Turn);

        private void ConfirmationAllowChatButton_Click(object sender, RoutedEventArgs e) =>
            AnswerShownConfirmation(true, AllowanceScope.Chat);

        private void AnswerShownConfirmation(bool approved, AllowanceScope scope = AllowanceScope.Once)
        {
            ConfirmationAllowToggle.IsChecked = false;
            if (_services is null || _shownConfirmation is not { } request)
            {
                return;
            }

            _shownConfirmation = null;
            _services.Confirmations.Complete(request, approved, scope);
        }

        /// <summary>
        /// «Разрешить…» есть только там, где разрешение впрок имеет смысл: у вопроса есть чат и
        /// это не вопрос SynGuard. «Для всего чата» у PowerShell не предлагается.
        /// </summary>
        private void ShowConfirmationAllow(ConfirmationRequest request)
        {
            ConfirmationAllowToggle.IsChecked = false;
            ConfirmationAllowToggle.Visibility = request.CanAllowAhead ? Visibility.Visible : Visibility.Collapsed;
            ConfirmationAllowChatButton.Visibility = request.CanAllowForChat ? Visibility.Visible : Visibility.Collapsed;
            ConfirmationAllowTurnDesc.Text = Loc.Format("S.Confirm.AllowTurn.Desc", request.Info.ToolName);
            ConfirmationAllowChatDesc.Text = Loc.Format("S.Confirm.AllowChat.Desc", request.Info.ToolName);
        }
    }
}
