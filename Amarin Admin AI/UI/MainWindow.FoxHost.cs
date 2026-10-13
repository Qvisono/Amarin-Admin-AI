using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.Fox;

namespace Amarin.UI
{
    public partial class MainWindow
    {
        /// <summary>
        /// Окно для Розы: где кромки, что сейчас в окне, две настоящие шалости и меню — и обратно,
        /// события окна для Розы (<see cref="IFoxHooks"/>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Вложенный класс, а не отдельный: ему нужно закрытое состояние окна (выбор чатов, рамка,
        /// лупа, вложения), а открывать его ради Розы всему остальному коду незачем. Тип грузится,
        /// только когда его создают, — через пять секунд после окна (<see cref="CreateFox"/>).
        /// </para>
        /// <para>
        /// Кадры и память Розы читаются на рабочем потоке: это сотни килобайт текста и файл с диска,
        /// а поток окна в первые секунды занят достройкой списка чатов.
        /// </para>
        /// </remarks>
        private sealed class FoxWindowHost : IFoxHost, IFoxHooks
        {
            /// <summary>Неактивное столько окно усыпляет Розу совсем: он не тратит кадры на окно, на которое не смотрят.</summary>
            private static readonly TimeSpan InactivePause = TimeSpan.FromMinutes(2);

            private readonly MainWindow _window;
            private readonly DispatcherTimer _inactive;
            private FoxStore? _store;
            private FoxStage? _stage;
            private string _root = "";
            private DateTime? _typedAt;
            private DateTime? _minimizedAt;
            private bool _stopped;
            private bool _locked;
            private bool _settingsShown;
            private bool _agentSaid;
            private int _importing;

            public FoxWindowHost(MainWindow window)
            {
                _window = window;
                _inactive = new DispatcherTimer(DispatcherPriority.Background) { Interval = InactivePause };
                _inactive.Tick += (_, _) =>
                {
                    _inactive.Stop();
                    UpdatePause();
                };
                Detached.Run(StartAsync(), "fox_start");
            }

            public Canvas Layer => _window.FoxLayer;

            public bool Holding => _stage?.Holding == true;

            private async Task StartAsync()
            {
                var services = _window._services!;
                var root = services.Profile.DataRoot;
                _root = root;
                var (sprites, store) = await Task.Run(() =>
                {
                    var loaded = FoxSprites.Load();
                    var opened = new FoxStore(root, () => services.Settings.EncryptChats);
                    _ = opened.Snapshot();
                    return (loaded, opened);
                });

                if (_stopped)
                {
                    store.Dispose();
                    return;
                }

                _store = store;
                if (_importing > 0)
                {
                    store.BeginImport();
                }

                _window.FoxLayer.Visibility = Visibility.Visible;
                _stage = new FoxStage(this, store, sprites, () => DateTime.Now, new Random());
                Subscribe();
                _stage.Start();
                UpdatePause();
            }

            private void Subscribe()
            {
                _window.MessageTextBox.TextChanged += OnTyped;
                _window.ComposerBorder.SizeChanged += OnLayout;
                _window.FoxLayer.SizeChanged += OnLayout;
                _window.DpiChanged += OnDpi;
                _window.StateChanged += OnState;
                _window.IsVisibleChanged += OnVisible;
                _window.Activated += OnActivated;
                _window.Deactivated += OnDeactivated;
                _window.SettingsOverlay.IsVisibleChanged += OnSettingsVisible;
            }

            private void Unsubscribe()
            {
                _window.MessageTextBox.TextChanged -= OnTyped;
                _window.ComposerBorder.SizeChanged -= OnLayout;
                _window.FoxLayer.SizeChanged -= OnLayout;
                _window.DpiChanged -= OnDpi;
                _window.StateChanged -= OnState;
                _window.IsVisibleChanged -= OnVisible;
                _window.Activated -= OnActivated;
                _window.Deactivated -= OnDeactivated;
                _window.SettingsOverlay.IsVisibleChanged -= OnSettingsVisible;
            }

            private void OnTyped(object sender, TextChangedEventArgs e) => _typedAt = DateTime.Now;

            private void OnLayout(object sender, SizeChangedEventArgs e) => _stage?.Relayout();

            private void OnDpi(object sender, DpiChangedEventArgs e) => _stage?.Relayout();

            private void OnState(object? sender, EventArgs e)
            {
                if (_window.WindowState == WindowState.Minimized)
                {
                    _minimizedAt = DateTime.Now;
                }
                else if (_minimizedAt is { } since)
                {
                    _minimizedAt = null;
                    UpdatePause();

                    // «Ты вернулся!» — только после настоящего ухода, а не мгновенного свёртывания.
                    if (DateTime.Now - since >= TimeSpan.FromMinutes(1))
                    {
                        _stage?.Signal(FoxSignalKind.Restored);
                    }

                    return;
                }

                UpdatePause();
            }

            private void OnVisible(object sender, DependencyPropertyChangedEventArgs e) => UpdatePause();

            private void OnActivated(object? sender, EventArgs e)
            {
                _inactive.Stop();
                UpdatePause();
            }

            private void OnDeactivated(object? sender, EventArgs e) => _inactive.Start();

            /// <summary>Роза замирает в свёрнутом, спрятанном, заблокированном и давно брошенном окне.</summary>
            private void UpdatePause() => _stage?.SetPaused(
                _locked || !_window.IsVisible || _window.WindowState == WindowState.Minimized ||
                (!_window.IsActive && !_inactive.IsEnabled));

            /// <summary>
            /// Открыли настройки — на карточку, закрыли — домой. По видимости слоя, а не по странице:
            /// повторное открытие на той же странице её «показом» не считается.
            /// </summary>
            private void OnSettingsVisible(object sender, DependencyPropertyChangedEventArgs e)
            {
                if (_window.SettingsOverlay.IsVisible)
                {
                    SettingsPage(_window._settingsView?.CurrentPage is SettingsKeyPage ? "keys" : "page");
                    return;
                }

                _settingsShown = false;
                _stage?.Signal(FoxSignalKind.SettingsClosed);
            }

            public FoxSituation Situation()
            {
                var w = _window;
                var fox = w._services?.Settings.Fox;
                return new FoxSituation
                {
                    WindowActive = w.IsActive && w.IsVisible && w.WindowState != WindowState.Minimized,
                    Covered = !w.MainLayoutUncovered(),
                    InputEmpty = w.MessageTextBox.Text.Length == 0 &&
                                 w._pendingImages.Count == 0 && w._pendingFiles.Count == 0 && w._pendingQuotes.Count == 0,
                    SinceTyping = _typedAt is { } typed ? DateTime.Now - typed : TimeSpan.MaxValue,
                    TurnRunning = w._services is not null && w.IsBusy(w._session.Id),
                    UiBusy = !w._selection.IsEmpty || w._marquee is { IsSelecting: true } || w._chatDrag is { IsDragging: true } ||
                             w._chatZoom?.IsZoomed == true || w.IsRecording || w.SearchBox.Text.Length > 0 ||
                             w.FindBar.Visibility == Visibility.Visible,
                    MischiefEnabled = fox?.Mischief ?? false,
                    ReadChat = fox?.ReadChat ?? false
                };
            }

            public FoxEdge Home()
            {
                var box = Bounds(_window.ComposerBorder);

                // Мимо скруглённых углов поля: на дуге Роза висел бы в воздухе.
                return box.IsEmpty ? new FoxEdge(0, Width(), Floor()) : new FoxEdge(box.Left + 16, box.Right - 16, box.Top);
            }

            public FoxEdge? Card()
            {
                if (_window._settingsView is not { } view || !_window.SettingsOverlay.IsVisible)
                {
                    return null;
                }

                var card = Bounds(view.SettingsCard);

                // Правый край — мимо крестика: на нём Роза закрывал бы кнопку.
                return card.IsEmpty || card.Width < 200 ? null : new FoxEdge(card.Left + 24, card.Right - 64, card.Top);
            }

            public double Floor() => Math.Max(0, _window.FoxLayer.ActualHeight - 2);

            public double Width() => _window.FoxLayer.ActualWidth;

            public int Size() => _window._services?.Settings.Fox.Size ?? FoxSettings.SmallSize;

            /// <summary>Плавно — только при включённых анимациях, на своём экране и с отрисовкой видеокартой.</summary>
            public bool Motion() =>
                UiMotion.Enabled && !SystemParameters.IsRemoteSession && (RenderCapability.Tier >> 16) >= 1;

            public Point? Pointer() =>
                _window.IsMouseOver || _window.FoxLayer.IsMouseCaptureWithin ? Mouse.GetPosition(_window.FoxLayer) : null;

            public Rect SendButton() => _window.SendButton.IsVisible ? Bounds(_window.SendButton) : Rect.Empty;

            public Rect ModelChip() => _window.ModelButton.IsVisible ? Bounds(_window.ModelButton) : Rect.Empty;

            /// <summary>Шалость: «Новый чат» — тем же путём, что кнопка; черновик открытого чата прячется, как при обычном переходе.</summary>
            public void NewChat() => _window.StartNewChatFromUi();

            /// <summary>Шалость: толкнуть колонку чатов — бросок прокрутки с её же инерцией и резинкой.</summary>
            public void NudgeChatList()
            {
                var list = _window.SideBarScrollViewer;
                var down = list.VerticalOffset < list.ScrollableHeight - 1;
                SmoothScroll.Fling(list, down ? 900 : -900);
            }

            public void ShowMenu(FoxStage stage)
            {
                var anchor = (FrameworkElement)stage.Element;
                var at = Mouse.GetPosition(_window.FoxLayer);
                var menu = AppMenu.At(anchor, PlacementMode.MousePoint);
                menu.Items.Add(AppMenu.Item(_window, Loc.Get("S.Fox.Menu.Pet"), stage.Pet));
                menu.Items.Add(AppMenu.Item(_window, Loc.Get("S.Fox.Menu.Scratch"), stage.Scratch));
                menu.Items.Add(AppMenu.Item(_window, Loc.Get("S.Fox.Menu.Treat"), stage.Treat));
                menu.Items.Add(AppMenu.Divider(_window));
                menu.Items.Add(AppMenu.Item(_window, Loc.Get("S.Fox.Menu.Ball"), () => stage.PlayBall(at)));
                menu.Items.Add(AppMenu.Item(_window, Loc.Get("S.Fox.Menu.Laser"), stage.PlayLaser));
                menu.Items.Add(AppMenu.Divider(_window));
                menu.Items.Add(AppMenu.Item(_window, Loc.Get("S.Fox.Menu.HideHour"), () => stage.HideFor(1)));
                menu.Items.Add(AppMenu.Item(_window, Loc.Get("S.Fox.Menu.HideDay"), () => stage.HideFor(0)));
                menu.IsOpen = true;
            }

            /// <summary>Прямоугольник элемента в координатах слоя Розы; пустой — элемент не в окне.</summary>
            private Rect Bounds(FrameworkElement element)
            {
                if (!element.IsVisible || element.ActualWidth <= 0 || !element.IsDescendantOf(_window.ScaledRoot))
                {
                    return Rect.Empty;
                }

                return element.TransformToVisual(_window.FoxLayer).TransformBounds(new Rect(element.RenderSize));
            }

            public void LetGo() => _stage?.LetGo();

            public void Sent(string text) => _stage?.Signal(new FoxSignal(FoxSignalKind.UserSent, Text: text));

            public void AnswerStarted()
            {
                _agentSaid = false;
                _stage?.Signal(FoxSignalKind.AssistantStarted);
            }

            public void AnswerFinished(string text) => _stage?.Signal(new FoxSignal(FoxSignalKind.AssistantCompleted, Text: text));

            public void AnswerFailed() => _stage?.Signal(FoxSignalKind.AssistantFailed);

            public void AnswerCancelled() => _stage?.Signal(FoxSignalKind.AssistantCancelled);

            /// <summary>Агент взялся за работу на ПК — раз на ход, а не на каждый его шаг.</summary>
            public void AgentWorking()
            {
                if (!_agentSaid)
                {
                    _agentSaid = true;
                    _stage?.Signal(FoxSignalKind.AgentWorking);
                }
            }

            public void SpendWarning() => _stage?.Signal(FoxSignalKind.SpendWarning);

            public void ChatCreated() => _stage?.Signal(FoxSignalKind.NewChat);

            public void ChatsDeleted(int count) => _stage?.Signal(new FoxSignal(FoxSignalKind.ChatsDeleted, Value: count));

            public void ThemeChanged() => _stage?.Signal(FoxSignalKind.ThemeChanged);

            /// <summary>
            /// Открыли настройки — запрыгивает на карточку; дальше переходы по страницам его не
            /// гоняют, кроме страницы ключей: на ней он закрывает глаза лапками.
            /// </summary>
            public void SettingsPage(string page)
            {
                if (_settingsShown && page != "keys")
                {
                    return;
                }

                _settingsShown = true;

                // Карточка раскладывается в этом же кадре: место на ней известно только после него.
                _window.Dispatcher.BeginInvoke(
                    () => _stage?.Signal(new FoxSignal(FoxSignalKind.SettingsOpened, Text: page)),
                    DispatcherPriority.Loaded);
            }

            public void DangerAsked() => _stage?.Signal(FoxSignalKind.DangerAsked);

            public void Locked(bool locked)
            {
                _locked = locked;
                UpdatePause();
                if (!locked)
                {
                    _stage?.Signal(FoxSignalKind.Unlocked);
                }
            }

            /// <summary>Другой профиль — другая память и другой Роза: приходит заново.</summary>
            public void ProfileChanged()
            {
                if (_store is null || _stage is null || _window._services is not { } services)
                {
                    return;
                }

                // После импорта окно перекореняет тот же профиль: память уже перечитана в EndImport.
                var root = services.Profile.DataRoot;
                if (string.Equals(root, _root, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _root = root;
                _stage.Stop();
                _store.UseRoot(root, () => services.Settings.EncryptChats);
                _stage.Start();
            }

            public void BeginImport()
            {
                _importing++;
                _store?.BeginImport();
            }

            /// <summary>Импорт лёг: память перечитывается с диска, и Роза приходит с ней заново.</summary>
            public void EndImport()
            {
                _importing = Math.Max(0, _importing - 1);
                if (_store is null || _stage is null)
                {
                    return;
                }

                _stage.Stop();
                _store.EndImport();
                _stage.Start();
            }

            public void Stop()
            {
                _stopped = true;
                _inactive.Stop();
                if (_stage is not null)
                {
                    Unsubscribe();
                    _stage.Stop();
                    _stage = null;
                }

                _store?.Dispose();
                _store = null;
                _window.FoxLayer.Visibility = Visibility.Collapsed;
            }
        }
    }
}
