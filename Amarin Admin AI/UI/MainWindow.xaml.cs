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
    public partial class MainWindow : Window, IChatTurnObserver
    {
        private AppServices? _services;
        private ChatSession _session = new();
        private bool _sidebarCollapsed;
        private bool _busy;
        private CancellationTokenSource? _turnCts;
        private AssistantMessageView? _liveAssistant;
        private readonly DispatcherTimer _workingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };

        // Разметку в живом ответе пересобираем по таймеру, а не на каждую дельту: полная
        // перестройка FlowDocument десятки раз в секунду съела бы UI-поток.
        private readonly DispatcherTimer _streamRender = new() { Interval = TimeSpan.FromMilliseconds(80) };
        private string _pendingStreamText = "";
        private string _renderedStreamText = "";
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
            Title = $"Amarin Admin AI v{RuntimeContext.AppVersionDisplay}";
            TitleText.Text = Title;
            SettingsVersionText.Text = $"v{RuntimeContext.AppVersionDisplay}";

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
            StateChanged += (_, _) => ApplyWindowStateChrome();

            SmoothScroll.SetIsEnabled(SideBarScrollViewer, true);
            SmoothScroll.SetIsEnabled(ChatScrollViewer, true);
            ChatScrollViewer.ScrollChanged += ChatScrollViewer_ScrollChanged;

            Warn.Visibility = RuntimeContext.IsAdministrator()
                ? Visibility.Collapsed
                : Visibility.Visible;

            _workingTimer.Tick += (_, _) => UpdateWorkingClock();
            _streamRender.Tick += (_, _) => FlushStreamText();
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                PersistCurrent();
            };

            Loaded += OnWindowLoaded;
            Activated += (_, _) => OnWindowActivated();
            ThemeManager.EffectiveThemeChanged += OnEffectiveThemeChanged;
            Closed += (_, _) =>
            {
                ThemeManager.EffectiveThemeChanged -= OnEffectiveThemeChanged;
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
            AgentLiteReasoningPicker.SetUsesTools(true);
            AgentHeavyReasoningPicker.SetUsesTools(true);

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

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void ResizeBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { Tag: string direction } && e.LeftButton == MouseButtonState.Pressed)
            {
                ResizeWindowLogic.ResizeWindow(direction, this);
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
        /// Полоски для растягивания у развёрнутого окна только мешают: тянуть его всё равно
        /// некуда. Вместе с обычным размером они возвращаются. Скруглением углов занимается
        /// сама Windows, отсюда его трогать нечем.
        /// </summary>
        private void ApplyWindowStateChrome()
        {
            ResizeGrips.Visibility = WindowState == WindowState.Maximized
                ? Visibility.Collapsed
                : Visibility.Visible;

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
                "Удалить все сохранённые чаты? Это нельзя отменить.",
                Title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            CancelTurn();
            _services.ChatStore.DeleteAll();
            StartNewSession(persist: false);
            RefreshChatList();
        }

        private void Button_Click_3(object sender, RoutedEventArgs e)
        {
            LoadSettingsUi();
            SettingsOverlay.Visibility = Visibility.Visible;
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
        /// Re-fetches the ImageSources that were assigned from code: those hold the previous
        /// theme's object and, unlike brushes, do not follow a DynamicResource.
        /// </summary>
        private void OnEffectiveThemeChanged()
        {
            if (!IsLoaded)
            {
                return;
            }

            UpdateModelButton();

            // RenderSession drops _liveAssistant, so never rebuild mid-turn;
            // the next render picks the new logos up anyway.
            if (!_busy)
            {
                RenderSession();
            }
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

        private static string FirstLine(string? text)
        {
            var line = (text ?? "")
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (string.IsNullOrWhiteSpace(line))
            {
                return "Ответ без текста";
            }

            return line.Length <= 160 ? line : line[..160] + "…";
        }

        private static string BuildToastMeta(string modelId, TimeSpan duration)
        {
            var name = VeniceModelCatalog.GetDisplayName(modelId);
            if (duration <= TimeSpan.Zero)
            {
                return name;
            }

            var elapsed = duration.TotalSeconds < 60
                ? $"{duration.TotalSeconds:0.#} с"
                : $"{(int)duration.TotalMinutes} мин {duration.Seconds} с";
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
                DownloadRequestText.Text =
                    "Агент хочет скачать файл с сайта, которого нет в списке разрешённых источников. " +
                    "Без разрешения загрузка не состоится.";
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

            if (ReferenceEquals(sender, AgentLiteReasoningPicker))
            {
                return settings.AgentLiteReasoning ??= new ReasoningSettings();
            }

            if (ReferenceEquals(sender, AgentHeavyReasoningPicker))
            {
                return settings.AgentHeavyReasoning ??= new ReasoningSettings();
            }

            return null;
        }

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
        }

        private ModelPickerField[] SettingsPickers() =>
        [
            LiteModelPicker,
            HeavyModelPicker,
            RouterModelPicker,
            TitleModelPicker,
            AgentLiteModelPicker,
            AgentHeavyModelPicker
        ];

        private ReasoningPicker[] SettingsReasoningPickers() =>
        [
            LiteReasoningPicker,
            HeavyReasoningPicker,
            RouterReasoningPicker,
            TitleReasoningPicker,
            AgentLiteReasoningPicker,
            AgentHeavyReasoningPicker
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

            _settingsUiLoading = true;
            try
            {
                _services.ReloadSettings();
                var settings = _services.Settings;
                AutoScrollToggle.IsChecked = settings.AutoScroll;
                NotifyOnCompleteToggle.IsChecked = settings.NotifyOnResponseComplete;
                NotifySoundToggle.IsChecked = settings.NotifySound;
                ThemeManager.Apply(settings.Theme);
                LoadAppearanceUi(settings);
                SelectUiScale(settings.UiScalePercent);
                ApplyUiScaleFromSettings();
                ApprovalModeCombo.SelectedIndex = settings.ApprovalMode == ApprovalMode.AlwaysApprove ? 0 : 1;
                ChatSharingToggle.IsChecked = settings.ChatSharingEnabled;
                LoadAccountUi();
                LoadUpdatesUi();
                RefreshAllowedDomainsUi();

                LiteModelPicker.SetSelected(settings.LiteModelId);
                HeavyModelPicker.SetSelected(settings.HeavyModelId);
                RouterModelPicker.SetSelected(settings.RouterModelId);
                TitleModelPicker.SetSelected(settings.TitleModelId);
                AgentLiteModelPicker.SetSelected(settings.AgentLiteModelId);
                AgentHeavyModelPicker.SetSelected(settings.AgentHeavyModelId);
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

            CancelTurn();
            PersistCurrent();
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
        }

        private bool ShouldKeepKeyboardFocus()
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
                Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
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
                if (TryPasteImageFromClipboard())
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
            if (_services is null || _busy)
            {
                return;
            }

            var text = MessageTextBox.Text.Trim();
            // Images alone are a valid message — "look at this" needs no words.
            if (string.IsNullOrWhiteSpace(text) && _pendingImages.Count == 0)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_services.Options.ApiKey))
            {
                MessageBox.Show(
                    this,
                    "Не задан API-ключ Venice.ai.\nЗадайте переменную окружения VENICE_API_KEY.",
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var command = ChatCommands.TryParse(text);

            // The agent works from a text brief and has no vision input, so refuse rather than
            // send the images off into nothing.
            if (command is not null && _pendingImages.Count > 0)
            {
                MessageBox.Show(
                    this,
                    "Команда /agent не принимает изображения.\nУберите вложения или отправьте их обычным сообщением.",
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            MessageTextBox.Clear();
            var images = _pendingImages.Count == 0 ? null : _pendingImages.ToArray();
            ClearPendingImages();
            _turnCts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                if (command is { Name: ChatCommands.Agent } agent)
                {
                    await _services.Chat.RunAgentCommandAsync(
                        _session, text, agent.Argument, agent.Complexity, this, _turnCts.Token);
                }
                else
                {
                    await _services.Chat.RunTurnAsync(_session, text, images, this, _turnCts.Token);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
                _turnCts?.Dispose();
                _turnCts = null;
            }
        }

        private void CancelTurn()
        {
            try
            {
                _turnCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // ignored
            }

            _services?.Confirmations.CancelAll();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _compact?.SetBusy(busy);
            SendButton.IsEnabled = !busy;
            NewChatButton.IsEnabled = !busy;
            ChatListPanel.IsEnabled = !busy;
            if (!busy)
            {
                _workingTimer.Stop();
                StopStreamRender();

                // The single place every turn passes through, however it ended — finished,
                // cancelled or failed. Venice stamps the remaining balance on the headers of
                // each request, so by now the client holds the figure this turn left behind.
                _balance?.Show(_services?.Venice.LastBalance);

                // Focusing an element in an inactive window activates that window, so a turn
                // finishing while the user works elsewhere used to yank the app to the front —
                // and, on the way, dismiss the very toast announcing it. The toast and the
                // taskbar flash are the cues for that case; the caret can wait until they return.
                if (IsForeground())
                {
                    FocusMessageInput();
                }
            }
        }

        private void StartNewSession(bool persist)
        {
            _stickToBottom = true;
            _session = _services?.ChatStore.CreateNew(CurrentModelId()) ?? new ChatSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = "Новый чат",
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
            var actions = CreateMessageActions();
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
            }

            MaybeAutoscroll();
        }

        private MessageActions CreateMessageActions() => new()
        {
            Copy = CopyMessage,
            CommitEdit = CommitUserEdit,
            Delete = DeleteAssistant,
            Regenerate = RegenerateAssistant,
            Cancel = _ => CancelTurn(),
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
            if (_busy || _services is null)
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
            if (_busy || _services is null)
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
            }

            StartNewSession(persist: false);
            RefreshChatList();
        }

        private void RegenerateAssistant(ChatDisplayMessage message)
        {
            if (_busy || _services is null)
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

        private async Task ContinueAssistantAsync()
        {
            if (_services is null || _busy)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_services.Options.ApiKey))
            {
                MessageBox.Show(
                    this,
                    "Не задан API-ключ Venice.ai.\nЗадайте переменную окружения VENICE_API_KEY.",
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _turnCts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                await _services.Chat.GenerateAssistantAsync(_session, this, _turnCts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
                _turnCts?.Dispose();
                _turnCts = null;
            }
        }

        private void MaybeStartTitle(ChatDisplayMessage user)
        {
            if (_services is null || !ChatTitle.IsDefault(_session.Title))
            {
                return;
            }

            if (_session.Messages.Count(item => item.Role == "user") != 1)
            {
                return;
            }

            var sessionId = _session.Id;
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
                var title = await _services.Titles.GenerateAsync(userText);
                if (string.IsNullOrWhiteSpace(title))
                {
                    return;
                }

                Ui(() =>
                {
                    if (_session.Id != sessionId)
                    {
                        return;
                    }

                    _session.Title = title;
                    PersistCurrent();
                    RefreshChatList();
                });
            }
            catch
            {
                // keep «Новый чат»
            }
        }

        private void RefreshChatList()
        {
            if (ChatListPanel is null)
            {
                return;
            }

            ChatListPanel.Children.Clear();
            if (_services is null)
            {
                return;
            }

            var query = SearchBox.Text;
            var items = _services.ChatStore.Search(query);
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
                        Content = item.Title,
                        Tag = item.Id,
                        Style = item.Id == _session.Id
                            ? (Style)ChatListPanel.FindResource("ChatItemActive")
                            : (Style)ChatListPanel.FindResource("ChatItem")
                    };
                    button.Click += ChatItem_Click;
                    ChatListPanel.Children.Add(button);
                    AttachChatActions(button, item);
                }
            }

            AddGroup("Закреплённые", items.Where(i => i.IsPinned));
            AddGroup("Сегодня", items.Where(i => !i.IsPinned && i.UpdatedAt.Date == today));
            AddGroup("Вчера", items.Where(i => !i.IsPinned && i.UpdatedAt.Date == yesterday));
            AddGroup("Ранее", items.Where(i => !i.IsPinned && i.UpdatedAt.Date < yesterday));

            if (ChatListPanel.Children.Count == 0 && !string.IsNullOrWhiteSpace(query))
            {
                ChatListPanel.Children.Add(new TextBlock
                {
                    Text = "Ничего не найдено",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A)),
                    Margin = new Thickness(14, 8, 6, 4)
                });
            }
        }

        private void ChatItem_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null || sender is not Button { Tag: string id })
            {
                return;
            }

            if (id == _session.Id)
            {
                return;
            }

            CancelTurn();
            PersistCurrent();
            var loaded = _services.ChatStore.TryLoad(id);
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
            BindSlot(AgentLiteReasoningPicker, settings.AgentLiteModelId, settings.AgentLiteReasoning);
            BindSlot(AgentHeavyReasoningPicker, settings.AgentHeavyModelId, settings.AgentHeavyReasoning);
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

        private void PersistCurrent()
        {
            if (_services is null || string.IsNullOrWhiteSpace(_session.Id) || _session.Messages.Count == 0)
            {
                return;
            }

            _services.ChatStore.Save(_session);
            RefreshChatList();
        }

        private void SchedulePersist()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
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

        void IChatTurnObserver.OnUserAppended(ChatDisplayMessage user) => Ui(() =>
        {
            var userRoot = ChatMessageViews.CreateUser(this, user, CreateMessageActions()).Root;
            MessagesPanel.Children.Add(userRoot);
            TrackMessageView(user, userRoot);
            SchedulePersist();
            MaybeAutoscroll();
            RefreshChatList();
            MaybeStartTitle(user);
        });

        void IChatTurnObserver.OnAssistantStarted(ChatDisplayMessage assistant) => Ui(() =>
        {
            _workingStarted = DateTime.Now;
            var view = ChatMessageViews.CreateAssistant(this, assistant, CreateMessageActions());
            _liveAssistant = view;
            MessagesPanel.Children.Add(view.Root);
            TrackMessageView(assistant, view.Root);
            _workingTimer.Start();
            _pendingStreamText = "";
            _renderedStreamText = "";
            _streamRender.Start();
            MaybeAutoscroll();
        });

        void IChatTurnObserver.OnAssistantText(ChatDisplayMessage assistant) => Ui(() =>
        {
            if (_liveAssistant is null)
            {
                return;
            }

            // Только копим: перерисует и подкрутит прокрутку следующий тик _streamRender.
            _pendingStreamText = assistant.Text;
            _liveAssistant.ApplyBranding(this, assistant.ResolvedModelId ?? assistant.RequestedModelId ?? "");
        });

        /// <summary>Показать накопленный кусок ответа, если он изменился с прошлого тика.</summary>
        private void FlushStreamText()
        {
            if (_liveAssistant is null || _pendingStreamText == _renderedStreamText)
            {
                return;
            }

            _renderedStreamText = _pendingStreamText;
            _liveAssistant.SetBody(_renderedStreamText, streaming: true);

            // Прокрутка только после перерисовки — иначе она считает высоту прошлого кадра.
            MaybeAutoscroll();
        }

        private void StopStreamRender()
        {
            _streamRender.Stop();
            _pendingStreamText = "";
            _renderedStreamText = "";
        }

        void IChatTurnObserver.OnToolsChanged(ChatDisplayMessage assistant) => Ui(() =>
        {
            _liveAssistant?.UpdateTools(assistant);
            SchedulePersist();
            MaybeAutoscroll();
        });

        void IChatTurnObserver.OnAssistantCompleted(ChatDisplayMessage assistant) => Ui(() =>
        {
            _workingTimer.Stop();
            StopStreamRender();
            _liveAssistant?.ApplyBranding(this, assistant.ResolvedModelId ?? assistant.RequestedModelId ?? "");
            if (_liveAssistant is not null)
            {
                _liveAssistant.SetBody(assistant.Text, streaming: false);
                _liveAssistant.UpdateTools(assistant);
                _liveAssistant.ShowFinished(assistant);
            }

            _liveAssistant = null;
            PersistCurrent();
            MaybeAutoscroll();
            MaybeShowCompletionToast(assistant);
        });

        void IChatTurnObserver.OnAssistantCancelled(ChatDisplayMessage assistant) => Ui(() =>
        {
            _workingTimer.Stop();
            StopStreamRender();
            if (_liveAssistant is not null)
            {
                _liveAssistant.SetBody(assistant.Text, streaming: false);
                _liveAssistant.UpdateTools(assistant);
                _liveAssistant.ShowFinished(assistant);
            }

            _liveAssistant = null;
            PersistCurrent();
        });

        void IChatTurnObserver.OnError(string message) => Ui(() =>
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                ConfirmationOverlay.Visibility = Visibility.Collapsed;
                Chat.IsHitTestVisible = true;
                return;
            }

            ConfirmationAgentText.Text = request.AgentLabel;
            ConfirmationSummaryText.Text = string.IsNullOrWhiteSpace(request.Info.ChangeSummary)
                ? request.Info.ToolName
                : request.Info.ChangeSummary;
            var explanation = string.IsNullOrWhiteSpace(request.Info.Explanation)
                ? request.Info.Details
                : request.Info.Explanation;
            ConfirmationExplanationText.Text = explanation ?? "";
            ConfirmationOverlay.Visibility = Visibility.Visible;
            Chat.IsHitTestVisible = false;
        }

        private void ConfirmationYesButton_Click(object sender, RoutedEventArgs e) =>
            _services?.Confirmations.CompleteCurrent(true);

        private void ConfirmationNoButton_Click(object sender, RoutedEventArgs e) =>
            _services?.Confirmations.CompleteCurrent(false);
    }
}
