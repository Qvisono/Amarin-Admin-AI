using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Amarin.Core;

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

            SmoothScroll.SetIsEnabled(SideBarScrollViewer, true);
            SmoothScroll.SetIsEnabled(ChatScrollViewer, true);
            ChatScrollViewer.ScrollChanged += ChatScrollViewer_ScrollChanged;

            Warn.Visibility = RuntimeContext.IsAdministrator()
                ? Visibility.Collapsed
                : Visibility.Visible;

            _workingTimer.Tick += (_, _) => UpdateWorkingClock();
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                PersistCurrent();
            };

            Loaded += OnWindowLoaded;
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

            if (_services is null)
            {
                return;
            }

            StartNewSession(persist: false);

            RefreshChatList();
            UpdateModelButton();
            LoadSettingsUi();
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
            }
            else if (ReferenceEquals(sender, HeavyModelPicker))
            {
                _services.Settings.HeavyModelId = id;
            }
            else if (ReferenceEquals(sender, RouterModelPicker))
            {
                _services.Settings.RouterModelId = id;
            }
            else if (ReferenceEquals(sender, TitleModelPicker))
            {
                _services.Settings.TitleModelId = id;
            }
            else if (ReferenceEquals(sender, AgentLiteModelPicker))
            {
                _services.Settings.AgentLiteModelId = id;
            }
            else if (ReferenceEquals(sender, AgentHeavyModelPicker))
            {
                _services.Settings.AgentHeavyModelId = id;
            }
            else
            {
                return;
            }

            _services.SettingsStore.Save(_services.Settings);
        }

        private void ChatModelPicker_ModelPicked(object sender, string id)
        {
            ModelButton.IsChecked = false;
            _session.SelectedModelId = id;
            if (_services is not null)
            {
                _services.Settings.ChatModelId = id;
                _services.SettingsStore.Save(_services.Settings);
            }

            UpdateModelButton();
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
                SelectUiScale(settings.UiScalePercent);
                ApplyUiScaleFromSettings();
                ApprovalModeCombo.SelectedIndex = settings.ApprovalMode == ApprovalMode.AlwaysApprove ? 0 : 1;

                LiteModelPicker.SetSelected(settings.LiteModelId);
                HeavyModelPicker.SetSelected(settings.HeavyModelId);
                RouterModelPicker.SetSelected(settings.RouterModelId);
                TitleModelPicker.SetSelected(settings.TitleModelId);
                AgentLiteModelPicker.SetSelected(settings.AgentLiteModelId);
                AgentHeavyModelPicker.SetSelected(settings.AgentHeavyModelId);

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

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshChatList();

        private void SendButton_Click(object sender, RoutedEventArgs e) => _ = SendAsync();

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
            if (string.IsNullOrWhiteSpace(text))
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

            MessageTextBox.Clear();
            _turnCts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                await _services.Chat.RunTurnAsync(_session, text, this, _turnCts.Token);
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
            SendButton.IsEnabled = !busy;
            NewChatButton.IsEnabled = !busy;
            ChatListPanel.IsEnabled = !busy;
            if (!busy)
            {
                _workingTimer.Stop();
                FocusMessageInput();
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
            if (persist)
            {
                PersistCurrent();
            }

            RenderSession();
            UpdateModelButton();
            FocusMessageInput();
        }

        private void LoadSession(ChatSession session)
        {
            _stickToBottom = true;
            _session = session;
            RenderSession();
            UpdateModelButton();
            FocusMessageInput();
        }

        private void RenderSession()
        {
            MessagesPanel.Children.Clear();
            _liveAssistant = null;
            var actions = CreateMessageActions();
            foreach (var message in _session.Messages)
            {
                if (message.Role == "user")
                {
                    MessagesPanel.Children.Add(ChatMessageViews.CreateUser(this, message, actions).Root);
                    continue;
                }

                var view = ChatMessageViews.CreateAssistant(this, message, actions);
                MessagesPanel.Children.Add(view.Root);
            }

            MaybeAutoscroll();
        }

        private MessageActions CreateMessageActions() => new()
        {
            Copy = CopyMessage,
            CommitEdit = CommitUserEdit,
            Delete = DeleteAssistant,
            Regenerate = RegenerateAssistant,
            Cancel = _ => CancelTurn()
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

            if (!ChatSessionEdit.DeleteAssistantTurn(_session, message.Id))
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
                if (title == "Сегодня")
                {
                    header.Margin = new Thickness(14, 6, 6, 4);
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
                }
            }

            AddGroup("Сегодня", items.Where(i => i.UpdatedAt.Date == today));
            AddGroup("Вчера", items.Where(i => i.UpdatedAt.Date == yesterday));
            AddGroup("Ранее", items.Where(i => i.UpdatedAt.Date < yesterday));

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
                ? ScrollBarVisibility.Hidden
                : ScrollBarVisibility.Auto;

            if (_newChatLabel is not null)
            {
                _newChatLabel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            }

            if (_newChatPlus is not null)
            {
                _newChatPlus.Margin = collapsed ? new Thickness(0) : new Thickness(0, 0, 6, 0);
            }

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
            MessagesPanel.Children.Add(ChatMessageViews.CreateUser(this, user, CreateMessageActions()).Root);
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
            _workingTimer.Start();
            MaybeAutoscroll();
        });

        void IChatTurnObserver.OnAssistantText(ChatDisplayMessage assistant) => Ui(() =>
        {
            if (_liveAssistant is null)
            {
                return;
            }

            _liveAssistant.SetBody(assistant.Text, markdown: false);
            _liveAssistant.ApplyBranding(this, assistant.ResolvedModelId ?? assistant.RequestedModelId ?? "");
            MaybeAutoscroll();
        });

        void IChatTurnObserver.OnToolsChanged(ChatDisplayMessage assistant) => Ui(() =>
        {
            _liveAssistant?.UpdateTools(assistant);
            SchedulePersist();
            MaybeAutoscroll();
        });

        void IChatTurnObserver.OnAssistantCompleted(ChatDisplayMessage assistant) => Ui(() =>
        {
            _workingTimer.Stop();
            _liveAssistant?.ApplyBranding(this, assistant.ResolvedModelId ?? assistant.RequestedModelId ?? "");
            if (_liveAssistant is not null)
            {
                _liveAssistant.SetBody(assistant.Text, markdown: true);
                _liveAssistant.UpdateTools(assistant);
                _liveAssistant.ShowFinished(assistant);
            }

            _liveAssistant = null;
            PersistCurrent();
            MaybeAutoscroll();
        });

        void IChatTurnObserver.OnAssistantCancelled(ChatDisplayMessage assistant) => Ui(() =>
        {
            _workingTimer.Stop();
            if (_liveAssistant is not null)
            {
                _liveAssistant.SetBody(assistant.Text, markdown: true);
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
