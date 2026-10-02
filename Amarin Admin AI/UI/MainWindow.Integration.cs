using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Amarin.Core;
using Amarin.Tools;
using Microsoft.Win32;

namespace Amarin.UI
{
    /// <summary>
    /// Интеграция с Windows (G1–G6): значок в трее, уведомления, глобальные сочетания, автозапуск,
    /// пункт Проводника и список переходов.
    /// </summary>
    /// <remarks>
    /// Всё, что пишет в реестр или трогает панель задач, делается только в настоящей программе
    /// (<see cref="IsRealApp"/>): оконные тесты поднимают это же окно, и прописать тестовый
    /// процесс в автозапуск человека было бы худшим из возможных побочных эффектов.
    /// </remarks>
    public partial class MainWindow
    {
        private const int WM_HOTKEY = 0x0312;
        private const int HotkeyBase = 0xA100;

        private TrayIcon? _tray;
        private bool _trayError;
        private bool _hiddenToTray;
        private bool _sessionEnding;
        private WindowState _stateBeforeTray = WindowState.Normal;
        private HwndSource? _hotkeySource;
        private readonly Dictionary<int, string> _hotkeyIds = [];
        private readonly Dictionary<string, string> _hotkeyProblems = new(StringComparer.Ordinal);
        private string? _jumpSignature;
        private string? _notifiedConfirmation;

        /// <summary>Сочетания, которые Windows не дала занять: действие → «занято другой программой».</summary>
        internal IReadOnlyDictionary<string, string> GlobalHotkeyProblems => _hotkeyProblems;

        /// <summary>Идёт ли это окно в настоящей программе, а не в тестах.</summary>
        private static bool IsRealApp =>
            Assembly.GetEntryAssembly() == typeof(MainWindow).Assembly && OperatingSystem.IsWindows();

        private bool _startInTray;

        /// <summary>
        /// Запуск с <c>--tray</c> (автозапуск, G3): окно показывается свёрнутым и без кнопки на
        /// панели задач, а после первого кадра прячется в трей. Совсем без показа нельзя: на
        /// первом кадре заводятся расписание, копии и прочая фоновая работа.
        /// </summary>
        internal void PrepareStartInTray()
        {
            _startInTray = true;
            _stateBeforeTray = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            ShowActivated = false;
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
        }

        private void FinishStartInTray()
        {
            if (_tray is { IsShown: true })
            {
                _hiddenToTray = true;
                Hide();
                ShowInTaskbar = true;
                WindowState = _stateBeforeTray;
                return;
            }

            // Значок не поднялся — окно без него было бы недостижимо.
            ShowFromTray();
        }

        /// <summary>Спрятано ли окно в трей: список переходов и второй запуск возвращают его.</summary>
        internal bool IsHiddenToTray => _hiddenToTray;

        private bool _integrationUiWired;

        /// <summary>
        /// Behavior: подстраницы «Интеграция с Windows» и «Голосовой ввод», сочетания из любой
        /// программы и вид уведомлений. Строки-ссылки показывают справа, что внутри включено.
        /// </summary>
        private void LoadIntegrationUi(AppServices services)
        {
            if (!_integrationUiWired)
            {
                _integrationUiWired = true;
                WindowsSettings.Applied = ApplyWindowsIntegration;
                WindowsSettings.Changed += RefreshBehaviorLinks;
                VoiceSettings.Changed += RefreshBehaviorLinks;
                GlobalHotkeysSettings.Applied = () =>
                {
                    ApplyWindowsIntegration();
                    return GlobalHotkeyProblems;
                };
            }

            WindowsSettings.Load(services);
            VoiceSettings.Load(services);
            GlobalHotkeysSettings.Load(services);
            GlobalHotkeysSettings.ShowProblems(GlobalHotkeyProblems);
            NotifyStyleCombo.SelectedIndex = services.Settings.Windows?.Notifications == NotificationStyle.System ? 1 : 0;
            RefreshBehaviorLinks();
        }

        private void RefreshBehaviorLinks()
        {
            if (_services is null)
            {
                return;
            }

            WindowsLinkRow.Tag = WindowsIntegrationBlock.Summary(_services.Settings.Windows);
            VoiceLinkRow.Tag = VoiceSettingsBlock.Summary(_services.Settings, IsVoiceAvailable());
        }

        private void NotifyStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            var settings = _services.Settings.Windows ??= new WindowsIntegrationSettings();
            settings.Notifications = NotifyStyleCombo.SelectedIndex == 1 ? NotificationStyle.System : NotificationStyle.Card;
            _services.SettingsStore.Save(_services.Settings);
            ApplyWindowsIntegration();
        }

        private void WireWindowsIntegration()
        {
            if (Application.Current is { } application)
            {
                application.SessionEnding += (_, _) => _sessionEnding = true;
            }

            StateChanged += (_, _) =>
            {
                if (WindowState != WindowState.Minimized)
                {
                    _stateBeforeTray = WindowState;
                }
            };
            Closed += (_, _) =>
            {
                _tray?.Dispose();
                _tray = null;
                UnregisterGlobalHotkeys();
            };
        }

        /// <summary>Приводит трей, сочетания и записи в реестре к настройкам. Зовётся при старте и из настроек.</summary>
        internal void ApplyWindowsIntegration()
        {
            if (_services is not { } services)
            {
                return;
            }

            var settings = services.Settings.Windows ??= new WindowsIntegrationSettings();
            ApplyTray(settings);
            if (IsRealApp)
            {
                RegisterGlobalHotkeys(settings);
                var exe = Environment.ProcessPath ?? "";
                if (exe.Length > 0)
                {
                    AutoStart.Apply(Registry.CurrentUser, settings.AutoStart, exe);
                    Amarin.Core.ExplorerMenu.Apply(Registry.CurrentUser, settings.ExplorerMenu, exe, Loc.Get("S.Windows.ExplorerVerb"));
                }
            }
        }

        // ───────────────────────── трей (G1) ─────────────────────────

        private void ApplyTray(WindowsIntegrationSettings settings)
        {
            if (settings.ShowTrayIcon && _tray is null && !_exiting && IsRealApp)
            {
                try
                {
                    _tray = new TrayIcon(this, AppIconImage());
                    _tray.Activated += ToggleFromTray;
                    _tray.MenuRequested += _ => OpenTrayMenu();
                    _tray.Show(TrayTip());
                }
                catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
                {
                    PerfLog.Write("tray failed " + ex.Message);
                    _tray = null;
                }
            }
            else if (!settings.ShowTrayIcon && _tray is not null)
            {
                _tray.Dispose();
                _tray = null;

                // Без значка спрятанное окно было бы недостижимо.
                if (_hiddenToTray)
                {
                    ShowFromTray();
                }
            }

            UpdateTrayState();
        }

        private static ImageSource AppIconImage()
        {
            var decoder = BitmapDecoder.Create(
                new Uri("pack://application:,,,/jeklE-removebg-preview_1.ico"),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderBy(frame => Math.Abs(frame.PixelWidth - 32)).First();
        }

        /// <summary>Точка на значке: ждёт вопроса, работает, сбой — или ничего.</summary>
        private void UpdateTrayState()
        {
            if (_tray is null || _services is null)
            {
                return;
            }

            var waiting = _services.Confirmations.PendingCount;
            var state = waiting > 0 ? TrayState.Waiting
                : _turns.Count > 0 ? TrayState.Busy
                : _trayError ? TrayState.Error
                : TrayState.Idle;
            _tray.SetState(state, TrayTip());
        }

        private string TrayTip()
        {
            var waiting = _services?.Confirmations.PendingCount ?? 0;
            return waiting > 0 ? Loc.Format("S.Windows.TipWaiting", waiting)
                : _turns.Count > 0 ? Loc.Format("S.Windows.TipBusy", _turns.Count)
                : "Amarin Admin AI";
        }

        private void ToggleFromTray()
        {
            if (IsVisible && WindowState != WindowState.Minimized && IsForeground())
            {
                HideToTray();
            }
            else
            {
                ShowFromTray();
            }
        }

        /// <summary>Прячет окно в трей. Геометрия снимается до Hide: спрятанное Windows отдаёт как «скрыто».</summary>
        internal void HideToTray()
        {
            if (_tray is not { IsShown: true } || _hiddenForExit)
            {
                return;
            }

            SaveWindowGeometry();
            FlushDraft();
            PopupManager.CloseAll();
            _hiddenToTray = true;
            Hide();
        }

        internal void ShowFromTray()
        {
            if (!ReviveFromBackgroundExit())
            {
                return;
            }

            _hiddenToTray = false;
            _trayError = false;
            if (!IsVisible)
            {
                Show();
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = _stateBeforeTray == WindowState.Minimized ? WindowState.Normal : _stateBeforeTray;
            }

            ShowInTaskbar = true;
            if (!Activate())
            {
                TaskbarFlash.Flash(this);
            }

            UpdateTrayState();
        }

        /// <summary>
        /// Крестик и Alt+F4 прячут в трей, если так выбрано. Не при выходе, не при выключении
        /// Windows и не без значка — иначе окно стало бы недостижимым.
        /// </summary>
        private bool ShouldCloseToTray() =>
            OwnsApplication && !_exiting && !_sessionEnding && !_hiddenForExit &&
            _tray is { IsShown: true } &&
            _services?.Settings.Windows is { CloseToTray: true };

        private void OpenTrayMenu()
        {
            var menu = new ContextMenu
            {
                Style = (Style)FindResource("AppContextMenu"),
                Placement = PlacementMode.MousePoint
            };
            menu.Items.Add(MenuItemFor(Loc.Get("S.Windows.MenuOpen"), ShowFromTray));
            menu.Items.Add(MenuItemFor(Loc.Get("S.Windows.MenuNewChat"), () => RunStartupAction(StartupAction.NewChat, null, null)));
            menu.Items.Add(MenuItemFor(Loc.Get("S.Windows.MenuHealth"), () => RunStartupAction(StartupAction.Health, null, null)));
            var waiting = _services?.Confirmations.PendingCount ?? 0;
            var pending = MenuItemFor(Loc.Format("S.Windows.MenuWaiting", waiting), ShowFromTray);
            pending.IsEnabled = waiting > 0;
            menu.Items.Add(pending);
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor(Loc.Get("S.Windows.MenuExit"), RequestExit));

            // Меню значка закрывается щелчком мимо, только если программа на переднем плане —
            // так устроены меню трея в Windows, и WPF здесь не исключение.
            SetForegroundWindow(new WindowInteropHelper(this).Handle);
            menu.IsOpen = true;
        }

        // ───────────────────────── уведомления (G4) ─────────────────────────

        /// <summary>
        /// Одна точка для всех уведомлений: своя карточка или системное — через значок в трее.
        /// Без значка системное уведомление показать нечем, и тогда — карточка.
        /// </summary>
        private void Notify(string modelId, string text, string meta, Action onOpen, bool warning = false)
        {
            if (_services?.Settings.Windows is { Notifications: NotificationStyle.System } &&
                _tray is { IsShown: true } tray &&
                tray.Balloon(string.IsNullOrWhiteSpace(meta) ? "Amarin Admin AI" : meta, text, warning, () => Ui(() =>
                {
                    ShowFromTray();
                    onOpen();
                })))
            {
                return;
            }

            _toast?.Close();
            var toast = NotificationToast.Show(
                modelId,
                text,
                meta,
                _services?.Settings.UiScalePercent ?? 100,
                OwnHandle(),
                () =>
                {
                    ShowFromTray();
                    onOpen();
                });
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
        /// Короткое сообщение программы (лимит близко, остаток кончается, копия не удалась): в окне —
        /// подписью у поля, а когда окно не перед глазами — уведомлением.
        /// </summary>
        private void NotifyStatus(string text, bool warning = true)
        {
            ShowTransientNotice(_session.Id, text);
            if (!IsVisible || WindowState == WindowState.Minimized || !IsForeground())
            {
                Notify("", ToastText(text), "", () => { }, warning);
            }
        }

        /// <summary>
        /// Вопрос об опасном действии пришёл, пока окно не перед глазами. В уведомлении — только
        /// «открыть»: отвечают на вопрос в окне, где видно, о чём он.
        /// </summary>
        private void NotifyConfirmationIfAway()
        {
            UpdateTrayState();
            if (_services is null || !_services.Confirmations.TryPeek(out var request))
            {
                _notifiedConfirmation = null;
                return;
            }

            var key = request.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (key == _notifiedConfirmation || (IsVisible && WindowState != WindowState.Minimized && IsForeground()))
            {
                return;
            }

            _notifiedConfirmation = key;
            Notify(
                "",
                IsLocked ? Loc.Get("S.Windows.ConfirmLocked") : ToastText(request.Info.ChangeSummary),
                Loc.Get("S.Windows.ConfirmTitle"),
                () => { },
                warning: true);
        }

        // ───────────────────────── глобальные сочетания (G2) ─────────────────────────

        private void RegisterGlobalHotkeys(WindowsIntegrationSettings settings)
        {
            UnregisterGlobalHotkeys();
            var handle = new WindowInteropHelper(this).EnsureHandle();
            _hotkeySource ??= HwndSource.FromHwnd(handle);
            _hotkeySource?.AddHook(HotkeyHook);

            var id = HotkeyBase;
            foreach (var action in GlobalHotkeys.All)
            {
                id++;
                var gesture = GlobalHotkeys.Effective(settings, action);
                if (gesture is null)
                {
                    continue;
                }

                if (!GlobalHotkeys.TryParse(gesture, out var modifiers, out var key))
                {
                    _hotkeyProblems[action] = Loc.Get("S.Windows.HotkeyInvalid");
                    continue;
                }

                if (RegisterHotKey(handle, id, modifiers | GlobalHotkeys.ModNoRepeat, key))
                {
                    _hotkeyIds[id] = action;
                }
                else
                {
                    _hotkeyProblems[action] = Loc.Get("S.Windows.HotkeyTaken");
                }
            }
        }

        private void UnregisterGlobalHotkeys()
        {
            var handle = new WindowInteropHelper(this).Handle;
            foreach (var id in _hotkeyIds.Keys)
            {
                UnregisterHotKey(handle, id);
            }

            _hotkeyIds.Clear();
            _hotkeyProblems.Clear();
            _hotkeySource?.RemoveHook(HotkeyHook);
        }

        private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && _hotkeyIds.TryGetValue((int)wParam, out var action))
            {
                handled = true;
                RunGlobalHotkey(action);
            }

            return IntPtr.Zero;
        }

        private void RunGlobalHotkey(string action)
        {
            switch (action)
            {
                case GlobalHotkeys.ShowHide:
                    ToggleFromTray();
                    break;
                case GlobalHotkeys.NewFromClipboard:
                    var text = "";
                    try
                    {
                        text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                    }
                    catch (ExternalException)
                    {
                        // Буфер занят другой программой — откроем пустой чат.
                    }

                    ShowFromTray();
                    StartNewChatFromUi();
                    PlaceIncomingPrompt(text, send: false);
                    break;
                case GlobalHotkeys.NewWithScreenshot:
                    // Снимок — до того, как окно покажется: снимают то, что на экране сейчас.
                    string? png = null;
                    try
                    {
                        png = ScreenshotTool.CapturePrimaryPng();
                    }
                    catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException)
                    {
                        PerfLog.Write("hotkey screenshot failed " + ex.Message);
                    }

                    ShowFromTray();
                    StartNewChatFromUi();
                    if (png is not null)
                    {
                        AddImage(new ImageAttachment(png, "image/png", Loc.Get("S.Windows.ScreenshotName")));
                    }

                    FocusMessageInput();
                    break;
            }
        }

        // ───────────────────────── действия запуска и список переходов (G6) ─────────────────────────

        /// <summary>Действие из командной строки, второго запуска, меню трея или списка переходов.</summary>
        internal void RunStartupAction(StartupAction action, string? chatId, string? askPath)
        {
            if (action != StartupAction.Tray)
            {
                ShowFromTray();
            }

            if (!string.IsNullOrWhiteSpace(chatId))
            {
                OpenChat(chatId);
            }

            switch (action)
            {
                case StartupAction.NewChat:
                    StartNewChatFromUi();
                    break;
                case StartupAction.Health:
                    OpenHealth();
                    break;
                case StartupAction.AskPath when !string.IsNullOrWhiteSpace(askPath):
                    // В поле, а не в отправку: путь пришёл из Проводника, и что с ним делать,
                    // человек допишет сам.
                    StartNewChatFromUi();
                    PlaceIncomingPrompt(Loc.Format("S.Windows.AskPathPrompt", askPath) + "\n", send: false);
                    break;
            }
        }

        /// <summary>
        /// Список переходов на кнопке панели задач: новый чат, состояние ПК и пять последних
        /// чатов. Пересобирается, только когда эти пять сменились.
        /// </summary>
        private void UpdateJumpList(IReadOnlyList<ChatIndexEntry> items)
        {
            if (!IsRealApp || Environment.ProcessPath is not { } exe || Application.Current is not { } application)
            {
                return;
            }

            var recent = items.OrderByDescending(item => item.UpdatedAt).Take(5).ToList();
            var signature = LanguageManager.Current + "|" + string.Join('|', recent.Select(item => item.Id + "~" + item.Title));
            if (signature == _jumpSignature)
            {
                return;
            }

            _jumpSignature = signature;
            try
            {
                var list = new JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
                list.JumpItems.Add(new JumpTask
                {
                    Title = Loc.Get("S.Windows.JumpNewChat"),
                    Arguments = "--new-chat",
                    ApplicationPath = exe,
                    IconResourcePath = exe
                });
                list.JumpItems.Add(new JumpTask
                {
                    Title = Loc.Get("S.Windows.JumpHealth"),
                    Arguments = "--health",
                    ApplicationPath = exe,
                    IconResourcePath = exe
                });

                var category = Loc.Get("S.Windows.JumpRecent");
                foreach (var item in recent)
                {
                    var title = DisplayTitle(item.Title);
                    list.JumpItems.Add(new JumpTask
                    {
                        Title = title.Length > 60 ? title[..59] + "…" : title,
                        Arguments = "--open-chat " + item.Id,
                        ApplicationPath = exe,
                        IconResourcePath = exe,
                        CustomCategory = category
                    });
                }

                JumpList.SetJumpList(application, list);
                list.Apply();
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or UnauthorizedAccessException)
            {
                PerfLog.Write("jump_list failed " + ex.Message);
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hwnd);
    }
}
