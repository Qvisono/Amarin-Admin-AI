using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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
        /// Вьюшка ответа в открытом чате. Состояние самого хода живёт в <c>_turns</c>: у окна
        /// один экран, а ходов может идти несколько.
        /// </summary>
        private AssistantMessageView? _liveAssistant;
        private readonly DispatcherTimer _workingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };

        // Разметку в живом ответе пересобираем по таймеру, а не на каждую дельту: полная
        // перестройка FlowDocument десятки раз в секунду съела бы UI-поток.
        private readonly DispatcherTimer _streamRender = new() { Interval = TimeSpan.FromMilliseconds(80) };
        private readonly Dictionary<string, FrameworkElement> _messageViews = [];

        /// <summary>
        /// A toast younger than this ignores window activation. Long enough to outlive the
        /// activation storm around showing it, short enough that a real click-back still closes it.
        /// </summary>
        private static readonly TimeSpan ActivationDismissGrace = TimeSpan.FromMilliseconds(700);

        private NotificationToast? _toast;

        /// <summary>The completion card currently on screen, if any. For tests.</summary>
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
        private bool _stickToBottom = true;
        private bool _autoScrolling;

        public MainWindow()
        {
            InitializeComponent();
            Title = $"Amarin Admin AI v{RuntimeContext.AppVersion}";
            TitleText.Text = Title;
            SettingsVersionText.Text = $"v{RuntimeContext.AppVersion}";

            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);

            new PerformanceOptimizer(this);
            InitializeAppearance();

            // Выпадашки чата — под тем же присмотром, что и пикеры в настройках: одна открытая
            // за раз, клик мимо закрывает.
            PopupManager.Register(ModelPicker, ModelButton);
            PopupManager.Register(ActionsPopup, AttachButton);

            WindowMaximizeFix.Attach(this);

            // Второй запуск программы просит это окно показаться. Вешаем здесь, а не в Program:
            // приём сообщения — дело самого окна, и в тестах оно работает так же, как в бою.
            SingleInstance.Attach(this, ActivateFromSecondInstance);
            StateChanged += (_, _) => ApplyWindowStateChrome();

            SmoothScroll.SetIsEnabled(SideBarScrollViewer, true);
            SmoothScroll.SetIsEnabled(ChatScrollViewer, true);
            ChatScrollViewer.ScrollChanged += ChatScrollViewer_ScrollChanged;

            // Страницы настроек — тем же скроллом, что колонка и чат. Список разрешённых
            // источников вложен в страницу данных: докрутив его до края, колесо уходит наружу,
            // за это отвечает сам SmoothScroll.
            SmoothScroll.SetIsEnabled(AppearancePageScroll, true);
            SmoothScroll.SetIsEnabled(BehaviorPageScroll, true);
            SmoothScroll.SetIsEnabled(CustomizePageScroll, true);
            SmoothScroll.SetIsEnabled(DataPageScroll, true);
            SmoothScroll.SetIsEnabled(AllowedDomainsScroll, true);

            Warn.Visibility = RuntimeContext.IsAdministrator()
                ? Visibility.Collapsed
                : Visibility.Visible;

            _workingTimer.Tick += (_, _) => UpdateWorkingClock();
            _streamRender.Tick += (_, _) => FlushStreamText();
            _saveTimer.Tick += (_, _) => FlushPendingPersists();

            Loaded += OnWindowLoaded;
            Activated += (_, _) => OnWindowActivated();
            ThemeManager.EffectiveThemeChanged += OnEffectiveThemeChanged;
            LanguageManager.LanguageChanged += RelocalizeUi;
            // На Closing, а не на Closed: размер снимается через хэндл окна, а к Closed окно
            // с ним уже расстаётся.
            Closing += (_, _) => SaveWindowGeometry();
            Closed += (_, _) =>
            {
                CancelAllTurns();
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
            }

            _services = services;
            _services.Confirmations.Changed += OnConfirmationChanged;
            DownloadAccessBroker.SetHandler(RequestDownloadDomainAsync);
            ApplyUiScaleFromSettings();
            if (IsLoaded)
            {
                OnWindowLoaded(this, new RoutedEventArgs());
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
            LiteReasoningPicker.SetUsesTools(true);
            HeavyReasoningPicker.SetUsesTools(true);
            RouterReasoningPicker.SetUsesTools(false);
            TitleReasoningPicker.SetUsesTools(false);
            AgentFastReasoningPicker.SetUsesTools(true);
            AgentLiteReasoningPicker.SetUsesTools(true);
            AgentHeavyReasoningPicker.SetUsesTools(true);
            // Защитник отвечает одним словом и инструментов не получает.
            SynGuardReasoningPicker.SetUsesTools(false);

            // Подпись ставится здесь, а не только при открытии настроек: пустая строка со
            // стрелкой рядом не объясняет, что за ней прячется.
            ShowSynGuardModelName();

            if (_services is null)
            {
                return;
            }

            StartNewSession(persist: false);

            RefreshChatList();
            UpdateModelButton();
            LoadSettingsUi();

            // Прошлое обновление оставило рядом прежний exe и папку загрузки — убираем.
            UpdateInstaller.CleanupLeftovers(Environment.ProcessPath);
            ScheduleAutoUpdateCheck();
            _ = LoadModelCatalogAsync();

            if (!string.IsNullOrWhiteSpace(_services.StartupPrompt))
            {
                MessageTextBox.Text = _services.StartupPrompt;
                _ = SendAsync();
            }
            else
            {
                FocusMessageInput();
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            Application.Current?.Shutdown();
        }

        private void Button_Click_1(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current?.MainWindow;
            if (mw != null)
                mw.WindowState = WindowState.Minimized;
        }

        private void Button_Click_2(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current?.MainWindow;
            if (mw == null) return;
            mw.WindowState = mw.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void Grid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            WindowMoveBehavior.HandleMouseLeftButtonDownForMove(this, e);
        }

        /// <summary>
        /// Разворот и сворачивание гасят открытые попапы: они висят отдельными окнами и остаются
        /// на прежнем месте экрана. Кромку для растягивания здесь трогать нечем — ею заведует
        /// <c>WindowChrome</c>, и у развёрнутого окна она отключается сама.
        /// </summary>
        private void ApplyWindowStateChrome()
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
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DeleteAllChatsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            var confirm = MessageBox.Show(
                this,
                Loc.Get("S.ChatList.DeleteAllConfirm"),
                Title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            CancelAllTurns();
            _services.ChatStore.DeleteAll();
            _attention.Clear();
            StartNewSession(persist: false);
            RefreshChatList();
        }

        private void Button_Click_3(object sender, RoutedEventArgs e)
        {
            // Панель показываем первой: вся загрузка шла до этой строки, и человек несколько
            // кадров смотрел на замерший интерфейс, прежде чем настройки вообще появлялись.
            SettingsOverlay.Visibility = Visibility.Visible;
            LoadSettingsUi();
        }

        private void AutoScrollToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            _services.Settings.AutoScroll = AutoScrollToggle.IsChecked == true;
            _services.SettingsStore.Save(_services.Settings);
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

        /// <summary>
        /// Re-fetches the ImageSources that were assigned from code — action icons and model
        /// logos: those hold the previous theme's object and do not follow a DynamicResource.
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

        // ───────── Уведомление о завершении ответа ─────────

        private void NotifyOnCompleteToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            _services.Settings.NotifyOnResponseComplete = NotifyOnCompleteToggle.IsChecked == true;
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

            _services.Settings.NotifySound = NotifySoundToggle.IsChecked == true;
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

            // Only when the user is looking elsewhere. IsActive is not that test — it stays
            // true while the window is merely covered by another app, which is the common case.
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

            // The toast goes away on its own; the taskbar button keeps blinking until the user
            // comes back, so a missed card does not mean a missed answer. This runs whenever
            // the window is not in front, not just when minimized — being buried behind another
            // app is the ordinary case, and it was silently skipped before.
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
        /// The user came back to the app. Clears the taskbar flash, and puts the toast away —
        /// but only if it has been up long enough to have been a deliberate return.
        /// <para>
        /// The toast is only ever shown while the window is *not* in front, so an activation can
        /// arrive in the same breath as the toast for reasons that have nothing to do with the
        /// user: focus moving inside the app, a modal opening, the window restoring. Dismissing
        /// on those is what made the notification appear for a frame and vanish.
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
            // Replace any toast still on screen outright — no fade, so the cards don't overlap.
            _toast?.Close();

            var modelId = assistant.ResolvedModelId ?? assistant.RequestedModelId ?? "";
            var id = assistant.Id;
            var toast = NotificationToast.Show(
                modelId,
                FirstLine(assistant.Text),
                BuildToastMeta(modelId, assistant.Duration),
                _services?.Settings.UiScalePercent ?? 100,
                OwnHandle(),
                () => OpenFromToast(id));

            _toast = toast;
            toast.Closed += (_, _) =>
            {
                if (ReferenceEquals(_toast, toast))
                {
                    _toast = null;
                }
            };
        }

        /// <summary>
        /// Сбой в чате, который сейчас не на экране. Карточкой, а не модалкой: модальное окно
        /// посреди набора в другом чате перехватило бы ввод из-за чужого сетевого сбоя.
        /// </summary>
        private void ShowBackgroundTurnError(RunningTurn turn, string message)
        {
            _toast?.Close();

            var sessionId = turn.SessionId;
            var toast = NotificationToast.Show(
                turn.Assistant?.ResolvedModelId ?? turn.Assistant?.RequestedModelId ?? "",
                FirstLine(message),
                Loc.Format("S.Turn.BackgroundFailed", DisplayTitle(turn.Session.Title)),
                _services?.Settings.UiScalePercent ?? 100,
                OwnHandle(),
                () => OpenChat(sessionId));

            _toast = toast;
            toast.Closed += (_, _) =>
            {
                if (ReferenceEquals(_toast, toast))
                {
                    _toast = null;
                }
            };
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

        /// <summary>True when this window is the one the user is actually looking at.</summary>
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
            if (string.IsNullOrEmpty(id) || !_messageViews.TryGetValue(id, out var element))
            {
                return;
            }

            // Stop autoscroll from yanking the view back to the bottom.
            _stickToBottom = false;
            Dispatcher.BeginInvoke(element.BringIntoView, DispatcherPriority.Loaded);
        }

        private void TrackMessageView(ChatDisplayMessage message, FrameworkElement element)
        {
            if (!string.IsNullOrEmpty(message.Id))
            {
                _messageViews[message.Id] = element;
            }
        }

        // ───────── Белый список загрузок ─────────

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

        private void RefreshAllowedDomainsUi()
        {
            var domains = AllowedDomains;
            // List<string> raises no change notifications, so rebind rather than mutate in place.
            AllowedDomainsList.ItemsSource = null;
            AllowedDomainsList.ItemsSource = domains.ToList();
            AllowedDomainsEmpty.Visibility = domains.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

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

        private void AddDomainButton_Click(object sender, RoutedEventArgs e) => OpenDomainDialog("");

        private void RemoveDomainButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null || sender is not Button { Tag: string domain })
            {
                return;
            }

            AllowedDomains.RemoveAll(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase));
            SaveAllowedDomains();
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
            Chat.IsHitTestVisible = false;
            Dispatcher.BeginInvoke(() =>
            {
                DomainInput.Focus();
                DomainInput.SelectAll();
            }, DispatcherPriority.Input);
        }

        private void CloseDomainDialog()
        {
            DomainOverlay.Visibility = Visibility.Collapsed;
            Chat.IsHitTestVisible = ConfirmationOverlay.Visibility != Visibility.Visible;
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

        // ───────── Запрос разрешения на загрузку ─────────

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
                Chat.IsHitTestVisible = false;
                return;
            }

            DownloadRequestOverlay.Visibility = Visibility.Collapsed;
            Chat.IsHitTestVisible = ConfirmationOverlay.Visibility != Visibility.Visible;
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

        private void ApprovalModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            if (ApprovalModeCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
                Enum.TryParse(tag, ignoreCase: true, out ApprovalMode mode))
            {
                _services.Settings.ApprovalMode = mode;
                _services.SettingsStore.Save(_services.Settings);
            }
        }

        private void SettingsModelPicked(object sender, string id)
        {
            if (_settingsUiLoading || _services is null || string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            if (ReferenceEquals(sender, LiteModelPicker))
            {
                _services.Settings.LiteModelId = id;
                LiteReasoningPicker.SetModel(id);
            }
            else if (ReferenceEquals(sender, HeavyModelPicker))
            {
                _services.Settings.HeavyModelId = id;
                HeavyReasoningPicker.SetModel(id);
            }
            else if (ReferenceEquals(sender, RouterModelPicker))
            {
                _services.Settings.RouterModelId = id;
                RouterReasoningPicker.SetModel(id);
            }
            else if (ReferenceEquals(sender, TitleModelPicker))
            {
                _services.Settings.TitleModelId = id;
                TitleReasoningPicker.SetModel(id);
            }
            else if (ReferenceEquals(sender, AgentFastModelPicker))
            {
                _services.Settings.AgentFastModelId = id;
                AgentFastReasoningPicker.SetModel(id);
            }
            else if (ReferenceEquals(sender, AgentLiteModelPicker))
            {
                _services.Settings.AgentLiteModelId = id;
                AgentLiteReasoningPicker.SetModel(id);
            }
            else if (ReferenceEquals(sender, AgentHeavyModelPicker))
            {
                _services.Settings.AgentHeavyModelId = id;
                AgentHeavyReasoningPicker.SetModel(id);
            }
            else if (ReferenceEquals(sender, SynGuardModelPicker))
            {
                _services.Settings.SynGuardModelId = id;
                SynGuardReasoningPicker.SetModel(id);
                ShowSynGuardModelName();
            }
            else
            {
                return;
            }

            _services.SettingsStore.Save(_services.Settings);
        }

        private void SettingsReasoningChanged(object sender, ReasoningChoiceChangedEventArgs e)
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

        private ReasoningSettings? SlotForReasoningPicker(object sender)
        {
            if (_services is null)
            {
                return null;
            }

            var settings = _services.Settings;
            if (ReferenceEquals(sender, LiteReasoningPicker))
            {
                return settings.LiteReasoning ??= new ReasoningSettings();
            }

            if (ReferenceEquals(sender, HeavyReasoningPicker))
            {
                return settings.HeavyReasoning ??= new ReasoningSettings();
            }

            if (ReferenceEquals(sender, RouterReasoningPicker))
            {
                return settings.RouterReasoning ??= new ReasoningSettings();
            }

            if (ReferenceEquals(sender, TitleReasoningPicker))
            {
                return settings.TitleReasoning ??= new ReasoningSettings();
            }

            if (ReferenceEquals(sender, AgentFastReasoningPicker))
            {
                return settings.AgentFastReasoning ??= new ReasoningSettings();
            }

            if (ReferenceEquals(sender, AgentLiteReasoningPicker))
            {
                return settings.AgentLiteReasoning ??= new ReasoningSettings();
            }

            if (ReferenceEquals(sender, AgentHeavyReasoningPicker))
            {
                return settings.AgentHeavyReasoning ??= new ReasoningSettings();
            }

            if (ReferenceEquals(sender, SynGuardReasoningPicker))
            {
                return settings.SynGuardReasoning ??= new ReasoningSettings();
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
            _services.Settings.SynGuardEnabled = SynGuardToggle.IsChecked == true;
            _services.SettingsStore.Save(_services.Settings);
        }

        /// <summary>
        /// Подпись раскрывающегося пункта — голый идентификатор модели, а не человеческое имя:
        /// строка служебная, и в ней важно точно знать, что именно стоит в настройке.
        /// </summary>
        private void ShowSynGuardModelName() =>
            SynGuardModelLabel.Text = _services is null
                ? SynGuard.FallbackModelId
                : SynGuard.ResolveModel(_services.Settings);

        private void ChatModelPicker_ModelPicked(object sender, string id)
        {
            ModelButton.IsChecked = false;
            _session.SelectedModelId = id;
            if (_services is not null)
            {
                _services.Settings.ChatModelId = id;
                _services.SettingsStore.Save(_services.Settings);
                // The per-chat model lives on the session, so write it out now rather than
                // leaving it to ride along on whatever unrelated save happens next.
                PersistCurrent();
            }

            UpdateModelButton();
        }

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
            ChatModelPicker.SetSelected(CurrentModelId());
            _ = LoadModelCatalogAsync();
        }

        private async Task LoadModelCatalogAsync()
        {
            if (_services is null)
            {
                return;
            }

            if (_services.Models.Cached is { } cached)
            {
                ApplyCatalog(cached, error: null);
                return;
            }

            ChatModelPicker.ShowLoading();
            foreach (var field in SettingsPickers())
            {
                field.ShowLoading();
            }

            var models = await _services.Models.GetAgenticAsync();
            ApplyCatalog(models, models.Count == 0 ? _services.Models.Error : null);
        }

        private void ApplyCatalog(IReadOnlyList<VeniceModelInfo> models, string? error)
        {
            HealUnusableModelSelections(models);
            ChatModelPicker.SetCatalog(models, error);
            ChatModelPicker.SetSelected(CurrentModelId());
            foreach (var field in SettingsPickers())
            {
                field.SetCatalog(models, error);
            }

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
        private void HealUnusableModelSelections(IReadOnlyList<VeniceModelInfo> models)
        {
            if (_services is null || models.Count == 0)
            {
                return;
            }

            var settings = _services.Settings;
            var defaults = new AppSettings();
            var changed = false;

            if (!VeniceModelCatalog.IsSelectable(models, _session.SelectedModelId))
            {
                // Пустая строка — «бери из настроек», а не «нет модели».
                _session.SelectedModelId = "";
                PersistCurrent();
            }

            if (!VeniceModelCatalog.IsSelectable(models, settings.ChatModelId))
            {
                settings.ChatModelId = "";
                changed = true;
            }

            foreach (var slot in ServiceModelSlots())
            {
                var current = slot.Read(settings);
                if (VeniceModelCatalog.IsSelectable(models, current))
                {
                    continue;
                }

                var fallback = slot.Read(defaults);
                slot.Write(
                    settings,
                    VeniceModelCatalog.IsSelectable(models, fallback) ? fallback : models[0].Id);
                changed = true;
            }

            if (changed)
            {
                _services.SettingsStore.Save(settings);
            }
        }

        private static (Func<AppSettings, string> Read, Action<AppSettings, string> Write)[] ServiceModelSlots() =>
        [
            (s => s.LiteModelId, (s, v) => s.LiteModelId = v),
            (s => s.HeavyModelId, (s, v) => s.HeavyModelId = v),
            (s => s.RouterModelId, (s, v) => s.RouterModelId = v),
            (s => s.TitleModelId, (s, v) => s.TitleModelId = v),
            (s => s.AgentFastModelId, (s, v) => s.AgentFastModelId = v),
            (s => s.AgentLiteModelId, (s, v) => s.AgentLiteModelId = v),
            (s => s.AgentHeavyModelId, (s, v) => s.AgentHeavyModelId = v),
            (s => s.SynGuardModelId, (s, v) => s.SynGuardModelId = v)
        ];

        private ModelPickerField[] SettingsPickers() =>
        [
            LiteModelPicker,
            HeavyModelPicker,
            RouterModelPicker,
            TitleModelPicker,
            AgentFastModelPicker,
            AgentLiteModelPicker,
            AgentHeavyModelPicker,
            SynGuardModelPicker
        ];

        private ReasoningPicker[] SettingsReasoningPickers() =>
        [
            LiteReasoningPicker,
            HeavyReasoningPicker,
            RouterReasoningPicker,
            TitleReasoningPicker,
            AgentFastReasoningPicker,
            AgentLiteReasoningPicker,
            AgentHeavyReasoningPicker,
            SynGuardReasoningPicker
        ];

        private void SaveMainPromptButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            _services.Settings.MainPrompt = MainPromptTextBox.Text ?? "";
            _services.SettingsStore.Save(_services.Settings);
        }

        private void SaveTechAiPromptButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            _services.Settings.TechAiPrompt = TechAiPromptTextBox.Text ?? "";
            _services.SettingsStore.Save(_services.Settings);
        }

        private void SaveTechAgentPromptButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            _services.Settings.TechAgentPrompt = TechAgentPromptTextBox.Text ?? "";
            _services.SettingsStore.Save(_services.Settings);
        }

        private void LoadSettingsUi()
        {
            if (_services is null)
            {
                return;
            }

            using var timer = PerfLog.Measure("settings_open");

            _settingsUiLoading = true;
            try
            {
                _services.ReloadSettings();
                var settings = _services.Settings;
                AutoScrollToggle.IsChecked = settings.AutoScroll;
                NotifyOnCompleteToggle.IsChecked = settings.NotifyOnResponseComplete;
                NotifySoundToggle.IsChecked = settings.NotifySound;
                RememberWindowSizeToggle.IsChecked = settings.RememberWindowSize;
                ThemeManager.Apply(settings.Theme);
                LoadAppearanceUi(settings);
                SelectUiScale(settings.UiScalePercent);
                ApplyUiScaleFromSettings();
                ApprovalModeCombo.SelectedIndex = settings.ApprovalMode == ApprovalMode.AlwaysApprove ? 0 : 1;
                ChatSharingToggle.IsChecked = settings.ChatSharingEnabled;
            LanguagePicker.SetSelected(settings.LanguageCode);
                LoadAccountUi();
                LoadUpdatesUi();
                RefreshAllowedDomainsUi();

                LiteModelPicker.SetSelected(settings.LiteModelId);
                HeavyModelPicker.SetSelected(settings.HeavyModelId);
                RouterModelPicker.SetSelected(settings.RouterModelId);
                TitleModelPicker.SetSelected(settings.TitleModelId);
                AgentFastModelPicker.SetSelected(settings.AgentFastModelId);
            AgentLiteModelPicker.SetSelected(settings.AgentLiteModelId);
                AgentHeavyModelPicker.SetSelected(settings.AgentHeavyModelId);
                SynGuardModelPicker.SetSelected(settings.SynGuardModelId);
                SynGuardToggle.IsChecked = settings.SynGuardEnabled;
                ShowSynGuardModelName();
                BindSettingsReasoningPickers();

                MainPromptTextBox.Text = settings.MainPrompt ?? "";
                TechAiPromptTextBox.Text = string.IsNullOrWhiteSpace(settings.TechAiPrompt)
                    ? ChatEngine.DefaultTechPrompt
                    : settings.TechAiPrompt;
                TechAgentPromptTextBox.Text = string.IsNullOrWhiteSpace(settings.TechAgentPrompt)
                    ? Agent.BaseSystemPrompt
                    : settings.TechAgentPrompt;
            }
            finally
            {
                _settingsUiLoading = false;
            }

            // Обход диска — только когда открыта его собственная страница. Считать при каждом
            // заходе в настройки значило читать все файлы чатов ради разбивки, которую человек
            // чаще всего и не смотрит. Флаг снят и вызов вне try: обход асинхронный, и держать на
            // нём _settingsUiLoading значило бы глушить обработчики всех остальных настроек.
            if (NavData.IsChecked == true)
            {
                _ = RefreshDataUsageAsync();
            }
        }

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
            for (var i = 0; i < UiScaleComboBox.Items.Count; i++)
            {
                if (UiScaleComboBox.Items[i] is ComboBoxItem item &&
                    int.TryParse(Convert.ToString(item.Tag), out var value) &&
                    value == percent)
                {
                    UiScaleComboBox.SelectedIndex = i;
                    return;
                }
            }

            UiScaleComboBox.SelectedIndex = 2;
        }

        private int ReadUiScaleCombo()
        {
            if (UiScaleComboBox.SelectedItem is ComboBoxItem item &&
                int.TryParse(Convert.ToString(item.Tag), out var percent))
            {
                return UiScale.Normalize(percent);
            }

            return 100;
        }

        private void CollapseSidebarButton_Click(object sender, RoutedEventArgs e) => SetSidebarCollapsed(true);

        private void SidebarLogoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_sidebarCollapsed)
            {
                SetSidebarCollapsed(false);
            }
        }

        private void SidebarLogoButton_MouseEnter(object sender, MouseEventArgs e) => UpdateLogoGlyph(hover: true);

        private void SidebarLogoButton_MouseLeave(object sender, MouseEventArgs e) => UpdateLogoGlyph(hover: false);

        private void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            PersistCurrent();
            ClearPendingAttachments();
            StartNewSession(persist: false);
            RenderSession();
            RefreshChatList();
            MessageTextBox.Focus();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // A pasted share code is not a search term — open the conversation it carries.
            if (ChatShareCodec.LooksLikeShareCode(SearchBox.Text))
            {
                var shared = ChatShareCodec.TryDecode(SearchBox.Text);
                SearchBox.Clear();
                if (shared is null)
                {
                    MessageBox.Show(
                        this,
                        "Код чата повреждён или не распознан.",
                        Title,
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                OpenSharedSession(shared);
                return;
            }

            RefreshChatList();
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            // Clicking the button leaves keyboard focus on it, which is both a worse place for
            // the caret than the field the user is about to type in again, and — with the
            // compact composer on — enough to hold the pill unfolded for the whole turn,
            // because focus anywhere on the toolbar suppresses the collapse.
            FocusMessageInput();
            _ = SendAsync();
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

            // Checked before anything reads the focused element, and without reading it: the
            // overlay takes focus itself when it opens, but between that and the first click there
            // is a moment with nothing focused at all, and the early return below would hand those
            // keystrokes to the composer -- including the Escape meant to close the journal.
            if (JournalOverlay.Visibility == Visibility.Visible)
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

            if (focused is TextBox { IsReadOnly: false })
            {
                return true;
            }

            if (focused is System.Windows.Controls.RichTextBox { IsReadOnly: false })
            {
                return true;
            }

            if (SettingsOverlay.Visibility == Visibility.Visible && IsInside(SettingsOverlay, focused))
            {
                return true;
            }

            // The viewer owns the arrow keys and Escape while it is up; without this the
            // window-level typing sink would push them into the composer instead.
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
                _ = SendAsync();
            }
        }

        private void MessageTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+V attaches an image when the clipboard holds one; otherwise the TextBox
            // handles the paste itself and text keeps working exactly as before.
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
            _ = SendAsync();
        }

        private async Task SendAsync()
        {
            if (_services is null)
            {
                return;
            }

            var text = MessageTextBox.Text.Trim();
            // Attachments alone are a valid message — "look at this" needs no words.
            if (string.IsNullOrWhiteSpace(text) && _pendingImages.Count == 0 && _pendingFiles.Count == 0)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_services.Options.ApiKey))
            {
                MessageBox.Show(
                    this,
                    Loc.Get("S.Turn.NoApiKey"),
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var command = ChatCommands.TryParse(text);

            // The agent works from a text brief and takes no attachments of its own, so refuse
            // rather than send them off into nothing.
            if (command is not null && (_pendingImages.Count > 0 || _pendingFiles.Count > 0))
            {
                MessageBox.Show(
                    this,
                    Loc.Get("S.Turn.AgentNoAttachments"),
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // If the turn finished in the moment between the check and the call, QueueFollowUp
            // says so and the message goes out as an ordinary one instead of vanishing.
            if (IsBusy(_session.Id) && QueueFollowUp(text, command is not null))
            {
                return;
            }

            MessageTextBox.Clear();
            var images = _pendingImages.Count == 0 ? null : _pendingImages.ToArray();
            var files = _pendingFiles.Count == 0 ? null : _pendingFiles.ToArray();
            ClearPendingAttachments();

            var session = _session;
            if (command is { Name: ChatCommands.Agent } agent)
            {
                await RunTurnAsync(session, TurnKind.AgentCommand, (chat, observer, token) =>
                    _services.Chat.RunAgentCommandAsync(
                        chat, text, agent.Argument, agent.Complexity, observer, token));
                return;
            }

            await RunTurnAsync(session, TurnKind.Send, (chat, observer, token) =>
                _services.Chat.RunTurnAsync(chat, text, images, files, observer, token));
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
            _session = session;
            // Image handles written into earlier answers only resolve while the pictures they
            // name are registered, and the registry does not survive a restart.
            ChatImageRegistry.RestoreAll(session);
            RenderSession();
            UpdateModelButton();
            FocusMessageInput();
        }

        private void RenderSession()
        {
            MessagesPanel.Children.Clear();
            _messageViews.Clear();
            _liveAssistant = null;
            var actions = CreateMessageActions(_session);
            var live = FindTurn(_session.Id);
            foreach (var message in _session.Messages)
            {
                if (message.Role == "user")
                {
                    var userRoot = ChatMessageViews.CreateUser(this, message, actions).Root;
                    MessagesPanel.Children.Add(userRoot);
                    TrackMessageView(message, userRoot);
                    continue;
                }

                var view = ChatMessageViews.CreateAssistant(this, message, actions);
                MessagesPanel.Children.Add(view.Root);
                TrackMessageView(message, view.Root);

                // Вернулись в чат, который ещё отвечает, — подхватываем его вьюшку заново.
                if (live is not null && message.Id == live.AssistantId)
                {
                    _liveAssistant = view;
                }
            }

            if (live is not null)
            {
                ResumeLiveRendering(live);
            }

            // Подпись под композером принадлежит чату: при переходе показывается его, а не
            // та, что осталась от разговора, из которого ушли.
            UpdateAttachmentWarning();
            MaybeAutoscroll();
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
            if (!string.IsNullOrEmpty(turn.RenderedText))
            {
                _liveAssistant.SetBody(turn.RenderedText, streaming: true);
            }

            if (turn.Assistant is not null)
            {
                _liveAssistant.UpdateTools(turn.Assistant);
            }

            _liveAssistant.ShowWorking(DateTime.Now - turn.StartedAt);
            _liveAssistant.ShowCancelOnly();
            _workingTimer.Start();
            _streamRender.Start();
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
            Continue = ResumeAssistant,
            CanContinue = message => CanResume(session, message),
            Cancel = _ => CancelTurn(session.Id),
            Share = ShareMessage,
            Export = ExportMessage,
            SharingEnabled = SharingEnabled,
            AddDownloadDomain = OpenDomainDialog
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

        private void CommitUserEdit(ChatDisplayMessage message, string text)
        {
            if (_services is null || IsBusy(_session.Id))
            {
                return;
            }

            if (!ChatSessionEdit.ReplaceUserText(_session, message.Id, text))
            {
                return;
            }

            RenderSession();
            PersistCurrent();
            RefreshChatList();
            _ = ContinueAssistantAsync();
        }

        private void DeleteAssistant(ChatDisplayMessage message)
        {
            if (_services is null || IsBusy(_session.Id))
            {
                return;
            }

            if (!ChatSessionEdit.DeleteTurn(_session, message.Id))
            {
                return;
            }

            if (_session.Messages.Count == 0)
            {
                DiscardEmptySession();
                return;
            }

            RenderSession();
            PersistCurrent();
            RefreshChatList();
        }

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
        /// Hands a line to the turn already running in this chat instead of refusing it.
        /// </summary>
        /// <remarks>
        /// The engine folds it into the context on the next round boundary, so the work in flight
        /// — the tool that is running, the agents it started — is not disturbed. Drawn here rather
        /// than by the engine: the person needs to see the line land the moment they press Enter,
        /// and a round can take a minute.
        /// </remarks>
        /// <returns>False when there is no longer a turn to queue onto.</returns>
        private bool QueueFollowUp(string text, bool isCommand)
        {
            if (FindTurn(_session.Id) is not { } turn)
            {
                return false;
            }

            // A slash command picks its own kind of turn, and there is no second turn to pick.
            // Folding "/agent …" in as plain text would silently mean something else.
            if (isCommand)
            {
                ShowComposerNotice(Loc.Get("S.Turn.NoCommandWhileBusy"));
                return true;
            }

            // Attachments travel in a multipart message built when the turn starts; the context
            // for this one was snapshotted rounds ago and there is nowhere to graft them on.
            if (_pendingImages.Count > 0 || _pendingFiles.Count > 0)
            {
                ShowComposerNotice(Loc.Get("S.Turn.NoAttachmentsWhileBusy"));
                return true;
            }

            MessageTextBox.Clear();

            var user = new ChatDisplayMessage
            {
                Role = "user",
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.Now,
                Text = text
            };
            _session.Messages.Add(user);

            var userRoot = ChatMessageViews.CreateUser(this, user, CreateMessageActions(_session)).Root;
            MessagesPanel.Children.Add(userRoot);
            TrackMessageView(user, userRoot);
            MaybeAutoscroll();
            RefreshChatList();

            turn.Enqueue(text);
            ShowComposerNotice(Loc.Get("S.Turn.Queued"));
            return true;
        }

        private void RegenerateAssistant(ChatDisplayMessage message)
        {
            if (_services is null || IsBusy(_session.Id))
            {
                return;
            }

            if (message.Status is AssistantStatus.Streaming)
            {
                return;
            }

            if (!ChatSessionEdit.TruncateFromMessage(_session, message.Id))
            {
                return;
            }

            RenderSession();
            PersistCurrent();
            _ = ContinueAssistantAsync();
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
        /// В отличие от <see cref="RegenerateAssistant"/> здесь нет
        /// <c>ChatSessionEdit.TruncateFromMessage</c>: тот снёс бы вместе с ответом и работу
        /// инструментов, за которую уже заплачено.
        /// </remarks>
        private void ResumeAssistant(ChatDisplayMessage message)
        {
            if (_services is null || IsBusy(_session.Id) || !CanResume(_session, message))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_services.Options.ApiKey))
            {
                MessageBox.Show(
                    this,
                    Loc.Get("S.Turn.NoApiKey"),
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _ = RunTurnAsync(_session, TurnKind.Continue, (chat, observer, token) =>
                _services.Chat.ResumeAssistantAsync(chat, message, observer, token));
        }

        private async Task ContinueAssistantAsync()
        {
            if (_services is null || IsBusy(_session.Id))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_services.Options.ApiKey))
            {
                MessageBox.Show(
                    this,
                    Loc.Get("S.Turn.NoApiKey"),
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            await RunTurnAsync(_session, TurnKind.Continue, (chat, observer, token) =>
                _services.Chat.GenerateAssistantAsync(chat, observer, token));
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
            _ = GenerateTitleAsync(sessionId, text);
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
                    if (!renamed && !repriced)
                    {
                        return;
                    }

                    if (renamed)
                    {
                        session.Title = draft.Title!;
                    }

                    // Счёт уже закрытого ответа изменился. Ценник и его разбивка собираются при
                    // отрисовке из самого сообщения, а типизированной ссылки на завершённую
                    // вьюшку окно не держит, — обновить их можно только перерисовав чат.
                    if (repriced && string.Equals(_session.Id, sessionId, StringComparison.Ordinal))
                    {
                        RenderSession();
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
        private string _chatListSignature = "";

        private void RefreshChatList()
        {
            if (ChatListPanel is null)
            {
                return;
            }

            if (_services is null)
            {
                ChatListPanel.Children.Clear();
                return;
            }

            var query = SearchBox.Text;
            var items = _services.ChatStore.Search(query);

            // Перерисовка стоит чтения index.json с диска и полной пересборки панели, а зовут её
            // теперь и фоновые ходы — по несколько раз за секунду. Если ничего не поменялось,
            // делать нечего.
            var signature = BuildChatListSignature(query, items);
            if (signature == _chatListSignature && ChatListPanel.Children.Count > 0)
            {
                return;
            }

            _chatListSignature = signature;
            ChatListPanel.Children.Clear();
            var today = DateTime.Today;
            var yesterday = today.AddDays(-1);

            var first = true;

            void AddGroup(string title, IEnumerable<ChatIndexEntry> group)
            {
                var list = group.ToList();
                if (list.Count == 0)
                {
                    return;
                }

                var header = new TextBlock
                {
                    Style = (Style)ChatListPanel.FindResource("GroupHeader"),
                    Text = title
                };
                if (first)
                {
                    // The topmost header sits right under the search box and needs less air.
                    header.Margin = new Thickness(14, 6, 6, 4);
                    first = false;
                }

                ChatListPanel.Children.Add(header);
                foreach (var item in list)
                {
                    var button = new Button
                    {
                        Content = DisplayTitle(item.Title),
                        Tag = item.Id,
                        Style = item.Id == _session.Id
                            ? (Style)ChatListPanel.FindResource("ChatItemActive")
                            : (Style)ChatListPanel.FindResource("ChatItem")
                    };
                    button.Click += ChatItem_Click;
                    ChatListPanel.Children.Add(button);
                    AttachChatActions(button, item);
                    MarkChatWorking(button, item.Id);
                    ChatRowState.SetNeedsAttention(button, _attention.Contains(item.Id));
                }
            }

            AddGroup(Loc.Get("S.ChatList.Pinned"), items.Where(i => i.IsPinned));
            AddGroup(Loc.Get("S.ChatList.Today"), items.Where(i => !i.IsPinned && i.UpdatedAt.Date == today));
            AddGroup(Loc.Get("S.ChatList.Yesterday"), items.Where(i => !i.IsPinned && i.UpdatedAt.Date == yesterday));
            AddGroup(Loc.Get("S.ChatList.Earlier"), items.Where(i => !i.IsPinned && i.UpdatedAt.Date < yesterday));

            if (ChatListPanel.Children.Count == 0 && !string.IsNullOrWhiteSpace(query))
            {
                var empty = new TextBlock
                {
                    Text = Loc.Get("S.Common.NothingFound"),
                    FontSize = 12,
                    Margin = new Thickness(14, 8, 6, 4)
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

        /// <summary>Зажигает точку «думает» у чата, по которому идёт ход.</summary>
        private void MarkChatWorking(Button row, string sessionId)
        {
            if (!IsBusy(sessionId))
            {
                return;
            }

            row.ApplyTemplate();
            if (row.Template.FindName("Working", row) is FrameworkElement dot)
            {
                dot.Visibility = Visibility.Visible;
            }
        }

        private string BuildChatListSignature(string query, IReadOnlyList<ChatIndexEntry> items)
        {
            var builder = new System.Text.StringBuilder(query).Append('|').Append(_session.Id);
            foreach (var item in items)
            {
                builder.Append('|')
                    .Append(item.Id).Append('~')
                    .Append(item.Title).Append('~')
                    .Append(item.UpdatedAt.Ticks).Append('~')
                    .Append(item.IsPinned ? '1' : '0').Append('~')
                    .Append(IsBusy(item.Id) ? '1' : '0').Append('~')
                    .Append(_attention.Contains(item.Id) ? '1' : '0');
            }

            return builder.ToString();
        }

        private void ChatItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string id })
            {
                OpenChat(id);
            }
        }

        /// <summary>Открыть чат. Ход, идущий в нём или в прежнем, не прерывается.</summary>
        private void OpenChat(string id)
        {
            if (_services is null || id == _session.Id)
            {
                return;
            }

            PersistCurrent();

            // Прикреплённое, но не отправленное, принадлежит тому чату, где его набрали.
            ClearPendingAttachments();

            // Метку «ответ готов» снимаем до загрузки: RefreshChatList ниже уже нарисует строку
            // без неё, и лишней перерисовки не будет.
            _attention.Remove(id);

            // Если по чату идёт ход — берём ЕГО объект сессии, а не читаем копию с диска:
            // движок продолжает писать в свой, и на экране оказалась бы застывшая копия.
            var loaded = FindTurn(id)?.Session ?? _services.ChatStore.TryLoad(id);
            if (loaded is null)
            {
                return;
            }

            LoadSession(loaded);
            RefreshChatList();
        }

        private void SetSidebarCollapsed(bool collapsed)
        {
            _sidebarCollapsed = collapsed;
            SidebarColumn.Width = new GridLength(collapsed ? 42 : 184);
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

            // The collapsed rail is 42 wide and the button is 30, so a 6px margin on each side
            // needs exactly 42 — nothing left for layout rounding at fractional UI scales, and
            // WPF answers an overflow with a square layout clip that shears the right edge off
            // the rounded hover plate. Collapsed, the horizontal margin goes away and centring
            // places the button instead: same spot on screen, 12px of slack behind it.
            SidebarLogoButton.HorizontalAlignment = collapsed
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Left;
            SidebarLogoButton.Margin = collapsed
                ? new Thickness(0, 6, 0, 6)
                : new Thickness(6);

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
            ChatModelPicker.SetSelected(modelId);
            RefreshContextRing();
            UpdateReasoningPicker();
        }

        private void UpdateReasoningPicker()
        {
            var modelId = CurrentModelId();
            var auto = VeniceModelCatalog.IsAuto(modelId);
            ChatReasoningPicker.SetAutoMode(auto);
            ChatReasoningPicker.SetModel(modelId);
            ChatReasoningPicker.SetChoice(_session.DisableThinking, _session.ReasoningEffort);
            ChatReasoningPicker.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BindSettingsReasoningPickers()
        {
            if (_services is null)
            {
                return;
            }

            var settings = _services.Settings;
            BindSlot(LiteReasoningPicker, settings.LiteModelId, settings.LiteReasoning);
            BindSlot(HeavyReasoningPicker, settings.HeavyModelId, settings.HeavyReasoning);
            BindSlot(RouterReasoningPicker, settings.RouterModelId, settings.RouterReasoning);
            BindSlot(TitleReasoningPicker, settings.TitleModelId, settings.TitleReasoning);
            BindSlot(AgentFastReasoningPicker, settings.AgentFastModelId, settings.AgentFastReasoning);
            BindSlot(AgentLiteReasoningPicker, settings.AgentLiteModelId, settings.AgentLiteReasoning);
            BindSlot(AgentHeavyReasoningPicker, settings.AgentHeavyModelId, settings.AgentHeavyReasoning);
            BindSlot(SynGuardReasoningPicker, settings.SynGuardModelId, settings.SynGuardReasoning);
        }

        private static void BindSlot(ReasoningPicker picker, string modelId, ReasoningSettings? slot)
        {
            var reasoning = slot ?? new ReasoningSettings();
            picker.SetModel(modelId);
            picker.SetChoice(reasoning.DisableThinking, reasoning.ReasoningEffort);
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

            return _services?.Options.Model ?? "claude-sonnet-5";
        }

        private void ChatScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_autoScrolling)
            {
                return;
            }

            if (e.ExtentHeightChange != 0)
            {
                if (_stickToBottom)
                {
                    MaybeAutoscroll();
                }

                return;
            }

            if (e.VerticalChange != 0)
            {
                _stickToBottom = IsChatScrolledToBottom();
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
            if (SmoothScroll.IsAnimating(ChatScrollViewer))
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

        // ═════════ Сообщения хода ═════════
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

            var userRoot = ChatMessageViews.CreateUser(this, user, CreateMessageActions(turn.Session)).Root;
            MessagesPanel.Children.Add(userRoot);
            TrackMessageView(user, userRoot);
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
            var view = ChatMessageViews.CreateAssistant(this, assistant, CreateMessageActions(turn.Session));
            _liveAssistant = view;

            // Продолжение возобновляет ответ, который в ленте уже нарисован: старый пузырь
            // подменяется новым на том же месте, иначе рядом встал бы второй с тем же ходом.
            if (!string.IsNullOrEmpty(assistant.Id) &&
                _messageViews.TryGetValue(assistant.Id, out var previous) &&
                MessagesPanel.Children.IndexOf(previous) is var slot and >= 0)
            {
                MessagesPanel.Children.RemoveAt(slot);
                MessagesPanel.Children.Insert(slot, view.Root);
            }
            else
            {
                MessagesPanel.Children.Add(view.Root);
            }

            TrackMessageView(assistant, view.Root);
            _workingTimer.Start();
            _streamRender.Start();
            MaybeAutoscroll();
        });

        void IChatTurnUi.TurnAssistantText(RunningTurn turn, ChatDisplayMessage assistant) => Ui(() =>
        {
            if (!IsVisibleTurn(turn) || _liveAssistant is null)
            {
                return;
            }

            // Только копим: перерисует и подкрутит прокрутку следующий тик _streamRender.
            _liveAssistant.ApplyBranding(this, assistant.ResolvedModelId ?? assistant.RequestedModelId ?? "");
        });

        /// <summary>Показать накопленный кусок ответа, если он изменился с прошлого тика.</summary>
        private void FlushStreamText()
        {
            var turn = FindTurn(_session.Id);
            if (turn is null || _liveAssistant is null || turn.PendingText == turn.RenderedText)
            {
                return;
            }

            turn.RenderedText = turn.PendingText;
            _liveAssistant.SetBody(turn.RenderedText, streaming: true);

            // Прокрутка только после перерисовки — иначе она считает высоту прошлого кадра.
            MaybeAutoscroll();
        }

        void IChatTurnUi.TurnToolsChanged(RunningTurn turn, ChatDisplayMessage assistant) => Ui(() =>
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
                CloseVisibleAnswer(assistant);
            }

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
                CloseVisibleAnswer(assistant);
            }
        });

        private void CloseVisibleAnswer(ChatDisplayMessage assistant)
        {
            _workingTimer.Stop();
            _streamRender.Stop();
            _liveAssistant?.ApplyBranding(
                this, assistant.ResolvedModelId ?? assistant.RequestedModelId ?? "");
            if (_liveAssistant is not null)
            {
                _liveAssistant.SetBody(assistant.Text, streaming: false);
                _liveAssistant.UpdateTools(assistant);
                _liveAssistant.ShowFinished(assistant);
            }

            _liveAssistant = null;
            MaybeAutoscroll();
        }

        void IChatTurnUi.TurnAssistantCancelled(RunningTurn turn, ChatDisplayMessage assistant) => Ui(() =>
        {
            if (!IsVisibleTurn(turn))
            {
                return;
            }

            _workingTimer.Stop();
            _streamRender.Stop();
            if (_liveAssistant is not null)
            {
                _liveAssistant.SetBody(assistant.Text, streaming: false);
                _liveAssistant.UpdateTools(assistant);
                _liveAssistant.ShowFinished(assistant);
            }

            _liveAssistant = null;
        });

        void IChatTurnUi.TurnError(RunningTurn turn, string message) => Ui(() =>
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (IsVisibleTurn(turn))
            {
                MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Модалка посреди набора в другом чате из-за сетевого сбоя в фоновом — это регресс.
            // Карточка в углу называет чат и не перехватывает ввод.
            ShowBackgroundTurnError(turn, message);
        });

        private void OnConfirmationChanged() => Ui(ShowNextConfirmation);

        private void ShowNextConfirmation()
        {
            if (_services is null || !IsLoaded)
            {
                return;
            }

            if (!_services.Confirmations.TryPeek(out var request))
            {
                CancelConfirmationExplain();
                ConfirmationOverlay.Visibility = Visibility.Collapsed;
                Chat.IsHitTestVisible = true;
                return;
            }

            ShowConfirmationChat(request.SessionId);
            ConfirmationAgentText.Text = request.AgentLabel;
            ConfirmationSummaryText.Text = string.IsNullOrWhiteSpace(request.Info.ChangeSummary)
                ? request.Info.ToolName
                : request.Info.ChangeSummary;
            FillConfirmationBody(request.Info);
            ExplainConfirmation(request);
            ConfirmationOverlay.Visibility = Visibility.Visible;
            Chat.IsHitTestVisible = false;
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

        private void ConfirmationYesButton_Click(object sender, RoutedEventArgs e) =>
            _services?.Confirmations.CompleteCurrent(true);

        private void ConfirmationNoButton_Click(object sender, RoutedEventArgs e) =>
            _services?.Confirmations.CompleteCurrent(false);
    }
}
