using System.Runtime.InteropServices;
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

            /// <summary>Слой Розы обычно: выше настроек (без Z, объявлены раньше) и ниже всех вопросов.</summary>
            private const int LayerZ = 27;

            /// <summary>
            /// Пока открыт журнал (34) или «Состояние ПК» (33), слой поднимается выше них, чтобы Роза
            /// выглядывал из-за карточки. Вопрос о домене тоже 35, но объявлен позже и остаётся выше;
            /// остальные вопросы — 36 и выше.
            /// </summary>
            private const int ViewerZ = 35;

            /// <summary>Скорость тыка лапкой по колонке: строка-другая, а не экран, как прежний бросок на 900.</summary>
            private const double PawFling = 280;

            /// <summary>Принюхивается к поиску по чатам не чаще этого: иначе каждая буква запроса была бы событием.</summary>
            private static readonly TimeSpan SniffEvery = TimeSpan.FromSeconds(20);

            /// <summary>
            /// Словарь значков меню Розы — с именем сборки: голый «/UI/…» разрешается от
            /// Application.ResourceAssembly, а под тест-раннером это не наша сборка.
            /// </summary>
            private static readonly Uri IconsUri = new(
                "pack://application:,,,/" + Uri.EscapeDataString(typeof(MainWindow).Assembly.GetName().Name ?? "") +
                ";component/UI/FoxIcons.xaml", UriKind.Absolute);

            /// <summary>Значки меню Розы: читаются при первом открытии меню, а не при запуске окна.</summary>
            private ResourceDictionary? _icons;

            private readonly MainWindow _window;
            private readonly DispatcherTimer _inactive;
            private FoxStore? _store;
            private FoxStage? _stage;
            private FoxMind? _mind;
            private string _root = "";
            private DateTime? _typedAt;
            private DateTime _sniffedAt;
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

            /// <summary>
            /// Esc — Розе, когда человек держит его или пишет ему. Игру Esc заканчивает, только если
            /// не останавливает им ответ: игру мог затеять и сам Роза, а ответа человек ждёт.
            /// </summary>
            /// <remarks>
            /// Открытую переписку Esc закрывает, когда в ней печатают, и когда ответа он не
            /// останавливает; пока идёт ответ, а фокус вне панели, Esc — ответу.
            /// </remarks>
            public bool TakesEscape(bool stopsAnswer) =>
                _stage is { } stage && (stage.InHands || (!stopsAnswer && (stage.Holding || stage.Chat is not null)));

            private async Task StartAsync()
            {
                var services = _window._services!;
                var root = services.Profile.DataRoot;
                _root = root;
                var (sprites, store) = await Task.Run(() =>
                {
                    var loaded = FoxSprites.Load();
                    var opened = new FoxStore(root, EncryptFor(root, services));
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
                _mind = CreateMind(services, store);
                _stage.MindRequested += (kind, text, acts) => _mind?.Ask(kind, text, acts);
                _stage.DiaryDue += () => _mind?.WriteDiary();
                Subscribe();
                _stage.Start();
                UpdatePause();
            }

            /// <summary>Своя модель Розы; ответы приходят с рабочего потока и переносятся в поток окна.</summary>
            private FoxMind CreateMind(AppServices services, FoxStore store) =>
                new(services.Http, services.Options, () => services.Settings, store, reply =>
                    _window.Dispatcher.BeginInvoke(() => Deliver(reply), DispatcherPriority.Background),
                    resolveModelInfo: services.Models.Find);

            /// <summary>Ответ своей модели — сигналом мозгу: реплика, мордочка и сдвиг дружбы, потом побег.</summary>
            private void Deliver(FoxReply reply)
            {
                if (_stage is null)
                {
                    return;
                }

                // Ответ на сказанное в переписке там и показан: над головой — только мордочка и
                // сдвиг дружбы, а «слова убежали» переписка пишет сама.
                var inChat = reply.Kind == "talk" && _stage.ChatAnswered(reply);
                if (reply.Failed)
                {
                    if (!inChat)
                    {
                        _stage.Signal(new FoxSignal(FoxSignalKind.MindReaction, Name: "mute"));
                    }

                    return;
                }

                var line = inChat ? "" : reply.Line;
                if (line.Length > 0 || reply.Emotion.Length > 0 || reply.Mood != 0)
                {
                    _stage.Signal(new FoxSignal(FoxSignalKind.MindReaction, Text: line, Value: reply.Mood, Name: reply.Emotion));
                }

                if (reply.EscapeMinutes > 0)
                {
                    _stage.Signal(new FoxSignal(FoxSignalKind.MindEscape, Value: reply.EscapeMinutes));
                    return;
                }

                foreach (var act in reply.Acts)
                {
                    _stage.Signal(new FoxSignal(FoxSignalKind.MindAct, Name: act));
                }
            }

            private void Subscribe()
            {
                _window.MessageTextBox.TextChanged += OnTyped;
                _window.SearchBox.TextChanged += OnSearchTyped;
                _window.ComposerBorder.SizeChanged += OnLayout;
                _window.FoxLayer.SizeChanged += OnLayout;
                _window.DpiChanged += OnDpi;
                _window.StateChanged += OnState;
                _window.IsVisibleChanged += OnVisible;
                _window.Activated += OnActivated;
                _window.Deactivated += OnDeactivated;
                _window.SettingsOverlay.IsVisibleChanged += OnSettingsVisible;
                _window.JournalOverlay.IsVisibleChanged += OnViewerVisible;
                _window.HealthOverlay.IsVisibleChanged += OnViewerVisible;
            }

            private void Unsubscribe()
            {
                _window.MessageTextBox.TextChanged -= OnTyped;
                _window.SearchBox.TextChanged -= OnSearchTyped;
                _window.ComposerBorder.SizeChanged -= OnLayout;
                _window.FoxLayer.SizeChanged -= OnLayout;
                _window.DpiChanged -= OnDpi;
                _window.StateChanged -= OnState;
                _window.IsVisibleChanged -= OnVisible;
                _window.Activated -= OnActivated;
                _window.Deactivated -= OnDeactivated;
                _window.SettingsOverlay.IsVisibleChanged -= OnSettingsVisible;
                _window.JournalOverlay.IsVisibleChanged -= OnViewerVisible;
                _window.HealthOverlay.IsVisibleChanged -= OnViewerVisible;
                Panel.SetZIndex(_window.FoxLayer, LayerZ);
            }

            private void OnTyped(object sender, TextChangedEventArgs e) => _typedAt = DateTime.Now;

            private void OnSearchTyped(object sender, TextChangedEventArgs e)
            {
                var now = DateTime.Now;
                if (_window.SearchBox.Text.Length > 0 && now - _sniffedAt >= SniffEvery)
                {
                    _sniffedAt = now;
                    _stage?.Signal(FoxSignalKind.SearchTyping);
                }
            }

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

            /// <summary>Открыли журнал или «Состояние ПК» — выше их и на карточку; закрыли — на место.</summary>
            private void OnViewerVisible(object sender, DependencyPropertyChangedEventArgs e)
            {
                var open = _window.JournalOverlay.IsVisible || _window.HealthOverlay.IsVisible;
                Panel.SetZIndex(_window.FoxLayer, open ? ViewerZ : LayerZ);
                if (e.NewValue is true || !open)
                {
                    _stage?.Signal(open ? FoxSignalKind.ViewerOpened : FoxSignalKind.ViewerClosed);
                }
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
                    ReadChat = fox?.ReadChat ?? false,
                    Thoughts = fox?.Thoughts ?? false,
                    Motion = Motion()
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

            /// <summary>
            /// Пол без кнопок: щель между колонкой и полем ввода, место справа от поля и пустая середина
            /// его нижнего ряда — правее плашки модели и левее микрофона и отправки. Поле стоит на 20
            /// точек выше края окна, и сидящий на полу закрыл бы собой его нижний ряд.
            /// </summary>
            public IReadOnlyList<FoxEdge> FloorSpans()
            {
                var floor = Floor();
                var sidebar = Bounds(_window.SidebarBorder);
                var left = sidebar.IsEmpty ? 0 : sidebar.Right;
                var box = Bounds(_window.ComposerBorder);
                if (box.IsEmpty)
                {
                    return [new FoxEdge(left, Width(), floor)];
                }

                var chip = Bounds(_window.ModelButton);
                var from = chip.IsEmpty ? box.Left + 60 : chip.Right + 8;
                var to = ((Rect[])[Bounds(_window.MicButton), Bounds(_window.SendButton)])
                    .Where(rect => !rect.IsEmpty)
                    .Select(rect => rect.Left - 8)
                    .DefaultIfEmpty(box.Right - 60)
                    .Min();
                return ((FoxEdge[])
                    [
                        new FoxEdge(left + 4, box.Left - 4, floor),
                        new FoxEdge(from, to, floor),
                        new FoxEdge(box.Right + 4, Width() - 4, floor)
                    ])
                    .Where(span => span.Right > span.Left)
                    .ToList();
            }

            /// <summary>Верх нижнего блока колонки (профиль и шестерёнка); у свёрнутой колонки его нет.</summary>
            public FoxEdge? Viewer()
            {
                var card = _window.JournalOverlay.IsVisible ? Bounds(_window.JournalCard)
                    : _window.HealthOverlay.IsVisible ? Bounds(_window.HealthOverlay.Card)
                    : Rect.Empty;

                // Правый край — мимо крестика, как у карточки настроек.
                return card.IsEmpty || card.Width < 200 ? null : new FoxEdge(card.Left + 24, card.Right - 64, card.Top);
            }

            public FoxEdge? Column()
            {
                var footer = Bounds(_window.SidebarFooter);
                return footer.IsEmpty || footer.Width < 120 ? null : new FoxEdge(footer.Left + 8, footer.Right - 8, footer.Top);
            }

            public double Width() => _window.FoxLayer.ActualWidth;

            public int Size() => _window._services?.Settings.Fox.Size ?? FoxSettings.SmallSize;

            /// <summary>Плавно — только при включённых анимациях, на своём экране и с отрисовкой видеокартой.</summary>
            public bool Motion() =>
                UiMotion.Enabled && !SystemParameters.IsRemoteSession && (RenderCapability.Tier >> 16) >= 1;

            public Point? Pointer() =>
                _window.IsMouseOver || _window.FoxLayer.IsMouseCaptureWithin ? Mouse.GetPosition(_window.FoxLayer) : null;

            /// <summary>
            /// Укус сбил курсор: настоящий курсор — в точку слоя, не дальше краёв окна. Только в
            /// настоящей программе и в активном окне: оконные тесты поднимают то же окно, и мышь
            /// человека посреди его работы двигать нельзя.
            /// </summary>
            /// <remarks>
            /// В экранные пиксели переводит <c>PointToScreen</c>: он знает и подделанный DPI масштаба
            /// интерфейса (<see cref="UiScale"/>), которым окно на деле нарисовано.
            /// </remarks>
            public void ShoveCursor(Point onLayer)
            {
                var layer = _window.FoxLayer;
                if (!IsRealApp || !_window.IsActive || PresentationSource.FromVisual(layer) is null)
                {
                    return;
                }

                var inside = new Point(
                    Math.Clamp(onLayer.X, 2, Math.Max(2, layer.ActualWidth - 2)),
                    Math.Clamp(onLayer.Y, 2, Math.Max(2, layer.ActualHeight - 2)));
                var screen = layer.PointToScreen(inside);
                _ = SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
            }

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetCursorPos(int x, int y);

            public Rect SendButton() => _window.SendButton.IsVisible ? Bounds(_window.SendButton) : Rect.Empty;

            public Rect ModelChip() => _window.ModelButton.IsVisible ? Bounds(_window.ModelButton) : Rect.Empty;

            /// <summary>Шалость: «Новый чат» — тем же путём, что кнопка; черновик открытого чата прячется, как при обычном переходе.</summary>
            public void NewChat() => _window.StartNewChatFromUi();

            /// <summary>
            /// Шалость: тык лапкой в колонку — бросок прокрутки с её же инерцией, но малый. Курсор над
            /// колонкой — человек ею занят, и лапка её не трогает.
            /// </summary>
            public void PawChats()
            {
                var list = _window.SideBarScrollViewer;
                if (!list.IsVisible || list.ScrollableHeight <= 0 || list.IsMouseOver)
                {
                    return;
                }

                var down = list.VerticalOffset < list.ScrollableHeight - 1;
                SmoothScroll.Fling(list, down ? PawFling : -PawFling);
            }

            public void ShowMenu(FoxStage stage)
            {
                var anchor = (FrameworkElement)stage.Element;
                var at = Mouse.GetPosition(_window.FoxLayer);
                var menu = AppMenu.At(anchor, PlacementMode.MousePoint);
                menu.Items.Add(Item("S.Fox.Menu.Pet", stage.Pet, "FoxIcon.Pet"));
                menu.Items.Add(Item("S.Fox.Menu.Scratch", stage.Scratch, "FoxIcon.Scratch"));
                menu.Items.Add(Item("S.Fox.Menu.Treat", stage.Treat, "FoxIcon.Treat"));
                menu.Items.Add(AppMenu.Divider(_window));
                if (stage.Playing)
                {
                    menu.Items.Add(Item("S.Fox.Menu.StopGame", stage.EndGame, "FoxIcon.StopGame"));
                }

                menu.Items.Add(Item("S.Fox.Menu.Ball", () => stage.PlayBall(at), "FoxIcon.Ball"));
                menu.Items.Add(Item("S.Fox.Menu.Laser", stage.PlayLaser, "FoxIcon.Laser"));
                menu.Items.Add(Item("S.Fox.Menu.HideSeek", () => stage.PlayGame(FoxWill.HideSeek), "FoxIcon.HideSeek"));
                menu.Items.Add(Item("S.Fox.Menu.Chase", () => stage.PlayGame(FoxWill.Catch), "FoxIcon.Chase"));
                menu.Items.Add(Item("S.Fox.Menu.Talk", stage.OpenTalk, "FoxIcon.Talk"));
                menu.Items.Add(Item("S.Fox.Menu.Chat", stage.OpenChat, "FoxIcon.Chat"));
                menu.Items.Add(AppMenu.Divider(_window));
                menu.Items.Add(Item("S.FoxSettings.Ours", () => Detached.Run(_window.ShowFoxFriendshipAsync(), "fox_friendship"), "FoxIcon.Friendship"));
                menu.Items.Add(Item("S.FoxSettings.Diary", () => Detached.Run(_window.ShowFoxDiaryAsync(), "fox_diary"), "FoxIcon.Diary"));
                menu.Items.Add(AppMenu.Divider(_window));
                menu.Items.Add(Item("S.Fox.Menu.HideHour", () => stage.HideFor(1), "FoxIcon.HideHour"));
                menu.Items.Add(Item("S.Fox.Menu.HideDay", () => stage.HideFor(0), "FoxIcon.HideDay"));
                menu.Items.Add(AppMenu.Item(_window, Loc.Get("S.Fox.Menu.Settings"), _window.OpenFoxSettings, icon: "Icon.Menu.ChatSettings"));
                menu.IsOpen = true;
            }

            /// <summary>Пункт меню Розы со значком из её словаря.</summary>
            private MenuItem Item(string key, Action invoke, string icon)
            {
                var item = AppMenu.Item(_window, Loc.Get(key), invoke);
                _icons ??= new ResourceDictionary { Source = IconsUri };
                MenuIcon.SetData(item, (Geometry)_icons[icon]);
                return item;
            }

            public void TalkClosed() => _window.FocusMessageInput();

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

            public void SettingsChanged() => _stage?.Relayout();

            public void Flush() => _store?.Flush();

            public void Unhide() => _stage?.Unhide();

            public void ResetFriendship()
            {
                if (_store is null || _stage is null)
                {
                    return;
                }

                _stage.Stop();
                _store.Reset(DateTime.Now);
                _stage.Start();
            }

            public void ForgetMemory() => _store?.Update(FoxMemory.Forget);

            public void ChatOpened(string title, int messages) =>
                _stage?.Signal(new FoxSignal(FoxSignalKind.ChatOpened, Text: title, Value: messages));

            public void RecordingStarted() => _stage?.Signal(FoxSignalKind.RecordingStarted);

            public void AttachmentAdded() => _stage?.Signal(FoxSignalKind.AttachmentAdded);

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

                // Начатое для прежнего профиля туда и не попадёт: разум — новый.
                _mind?.Dispose();
                _store.UseRoot(root, EncryptFor(root, services));
                _mind = CreateMind(services, _store);
                _stage.Start();
            }

            /// <summary>
            /// Шифровать ли память Розы в папке <paramref name="root"/> — по галочке «Шифровать чаты»
            /// того профиля, чья это папка, а не того, что открыт сейчас.
            /// </summary>
            /// <remarks>
            /// Окно узнаёт о смене профиля, когда настройки уже подменены (<c>ProfileScope.UseProfile</c>),
            /// а отложенная память прежнего профиля пишется позже: при пересадке на новый профиль, при
            /// уходе Розы или таймером. До 1.37.0-beta.7 она бралась по галочке нового профиля, и память
            /// профиля с шифрованием ложилась в его папку открытым текстом, если в новом шифрования нет.
            /// </remarks>
            private static Func<bool> EncryptFor(string root, AppServices services) => () =>
                string.Equals(services.Profile.DataRoot, root, StringComparison.OrdinalIgnoreCase)
                    ? services.Settings.EncryptChats
                    : new AppSettingsStore(root).Load().EncryptChats;

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
                _mind?.Dispose();
                _mind = null;
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
