using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KeyEventHandler = System.Windows.Input.KeyEventHandler;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using ScrollEventArgs = System.Windows.Controls.Primitives.ScrollEventArgs;
using ScrollEventHandler = System.Windows.Controls.Primitives.ScrollEventHandler;
using ScrollEventType = System.Windows.Controls.Primitives.ScrollEventType;
using ScrollViewer = System.Windows.Controls.ScrollViewer;

namespace Amarin.UI
{
    /// <summary>Край, к которому доезжает <see cref="SmoothScroll.GlideTo"/>.</summary>
    public enum ScrollEdge
    {
        Top,
        Bottom
    }

    /// <summary>
    /// Плавная прокрутка колесом: инерция, трение и резинка на краю. По желанию — ещё и
    /// перетаскиванием левой кнопкой (<see cref="DragScrollProperty"/>) и доездом к краю
    /// (<see cref="GlideTo"/>) — всё на одной физике.
    /// </summary>
    /// <remarks>
    /// Публичный — ради разметки: внутри шаблонов (выпадашка <c>DarkComboBox</c>, поле
    /// <c>PromptTextBox</c>) включать её больше неоткуда, а attached-свойство из markup видно
    /// только у публичного типа. Тем же и <see cref="RoundedClip"/>.
    /// </remarks>
    public static class SmoothScroll
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(SmoothScroll),
                new PropertyMetadata(false, OnIsEnabledChanged));

        private static readonly DependencyProperty HookProperty =
            DependencyProperty.RegisterAttached(
                "Hook",
                typeof(Hook),
                typeof(SmoothScroll));

        /// <summary>
        /// Листать ещё и перетаскиванием: зажатая левая кнопка тянет содержимое за собой, а
        /// отпущенная на ходу бросает его с той же инерцией, что и колесо.
        /// </summary>
        /// <remarks>
        /// Действует только вместе с <see cref="IsEnabledProperty"/>: физика броска и резинки
        /// живёт в том же хуке. Нажатие не забирается — клик по строке или кнопке работает как
        /// обычно, перетаскиванием оно становится лишь за порогом сдвига.
        /// </remarks>
        public static readonly DependencyProperty DragScrollProperty =
            DependencyProperty.RegisterAttached(
                "DragScroll",
                typeof(bool),
                typeof(SmoothScroll),
                new PropertyMetadata(false, OnDragScrollChanged));

        /// <summary>
        /// Тянуть зажатой кнопкой и тогда, когда содержимое влезло целиком: оно пружинит и
        /// возвращается на место.
        /// </summary>
        /// <remarks>
        /// По умолчанию короткое содержимое жеста не берёт: в колонке чатов нажатие на пустом
        /// месте иначе отнималось бы у перетаскивания окна. Страницы настроек лежат поверх окна,
        /// и этой беды у них нет, — а страница, которая то длинная, то короткая (список
        /// инструкций), без резинки казалась бы сломанной ровно тогда, когда в ней одна строка.
        /// </remarks>
        public static readonly DependencyProperty BounceWhenShortProperty =
            DependencyProperty.RegisterAttached(
                "BounceWhenShort",
                typeof(bool),
                typeof(SmoothScroll),
                new PropertyMetadata(false));

        public static bool GetBounceWhenShort(DependencyObject obj) =>
            (bool)obj.GetValue(BounceWhenShortProperty);

        public static void SetBounceWhenShort(DependencyObject obj, bool value) =>
            obj.SetValue(BounceWhenShortProperty, value);

        public static bool GetIsEnabled(DependencyObject obj) =>
            (bool)obj.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(DependencyObject obj, bool value) =>
            obj.SetValue(IsEnabledProperty, value);

        public static bool GetDragScroll(DependencyObject obj) =>
            (bool)obj.GetValue(DragScrollProperty);

        public static void SetDragScroll(DependencyObject obj, bool value) =>
            obj.SetValue(DragScrollProperty, value);

        /// <summary>
        /// Плавно доезжает до края и зовёт <paramref name="arrived"/>, когда встал — или когда
        /// доезд прервали колесом, клавишей или полосой.
        /// </summary>
        /// <remarks>
        /// Цель пересчитывается каждый кадр: низ ленты растёт, пока дописывается ответ, и доезд
        /// к запомненному числу остановился бы выше настоящего конца. Дальше трёх экранов
        /// сначала мгновенный перескок на полтора экрана от цели: пролетать сотни сообщений по
        /// кадру значило бы показывать размазанную пустоту и строить всё, что мелькнуло.
        /// </remarks>
        public static void GlideTo(ScrollViewer viewer, ScrollEdge edge, Action? arrived = null)
        {
            if (viewer.GetValue(HookProperty) is Hook hook)
            {
                hook.Glide(edge, arrived);
                return;
            }

            if (edge == ScrollEdge.Top)
            {
                viewer.ScrollToTop();
            }
            else
            {
                viewer.ScrollToEnd();
            }

            arrived?.Invoke();
        }

        /// <summary>Идёт доезд к краю (<see cref="GlideTo"/>).</summary>
        public static bool IsGliding(ScrollViewer viewer) =>
            ((Hook?)viewer.GetValue(HookProperty))?.IsGliding ?? false;

        /// <summary>Содержимое тащат мышью прямо сейчас.</summary>
        public static bool IsDragging(ScrollViewer viewer) =>
            ((Hook?)viewer.GetValue(HookProperty))?.IsDragging ?? false;

        /// <summary>Нажатие для перетаскивания — в обход событий, ради тестов.</summary>
        /// <remarks>
        /// Положение мыши в поднятом <c>RaiseEvent</c> событии берётся у настоящего курсора, и
        /// подставить его нельзя — тем же приёмом отделены <c>ChatZoom.ArmPan</c> и <c>PanTo</c>.
        /// </remarks>
        internal static void ArmDrag(ScrollViewer viewer, Point press) =>
            ((Hook?)viewer.GetValue(HookProperty))?.ArmDrag(press);

        /// <inheritdoc cref="ArmDrag"/>
        internal static bool DragTo(ScrollViewer viewer, Point point) =>
            ((Hook?)viewer.GetValue(HookProperty))?.DragTo(point) ?? false;

        /// <inheritdoc cref="ArmDrag"/>
        internal static void EndDrag(ScrollViewer viewer, bool fling) =>
            ((Hook?)viewer.GetValue(HookProperty))?.EndDrag(fling);

        private static void OnDragScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer viewer && viewer.GetValue(HookProperty) is Hook hook)
            {
                hook.DragEnabled = e.NewValue is true;
            }
        }

        /// <summary>
        /// True while a flick is still being carried by inertia.
        ///
        /// The hook owns <see cref="ScrollViewer.VerticalOffset"/> for as long as this is true —
        /// it re-asserts its own position every frame — so anything else that scrolls the viewer
        /// in that window is undone on the next frame, once per frame. Callers that move the view
        /// on their own check this first and let the flick finish.
        /// </summary>
        public static bool IsAnimating(ScrollViewer viewer) =>
            ((Hook?)viewer.GetValue(HookProperty))?.IsAnimating ?? false;

        /// <summary>
        /// Гасит инерцию и отдаёт прокрутку тому, кто зовёт.
        /// </summary>
        /// <remarks>
        /// Нужно всякому, кто собирается двигать вид сам: пока идёт бросок, хук переустанавливает
        /// <see cref="ScrollViewer.VerticalOffset"/> каждый кадр (см. <see cref="IsAnimating"/>),
        /// и чужое движение он бы затирал. Заодно снимает резинку: пока она растянута, экранные
        /// координаты разъезжаются с содержимым на её длину.
        /// </remarks>
        public static void Cancel(ScrollViewer viewer) =>
            ((Hook?)viewer.GetValue(HookProperty))?.CancelInertia(snap: true);

        /// <summary>
        /// Бросок извне — с той же инерцией, трением и резинкой, что и у колеса.
        /// </summary>
        /// <param name="velocity">
        /// Пикселей в секунду; положительная — вниз по ленте, как у смещения прокрутки.
        /// </param>
        /// <remarks>
        /// Затем, чтобы перетаскивание не заводило второй физики: доводит бросок тот же код, что
        /// и после колеса, и рука разницы не чувствует.
        /// </remarks>
        public static void Fling(ScrollViewer viewer, double velocity) =>
            ((Hook?)viewer.GetValue(HookProperty))?.Fling(velocity);

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ScrollViewer viewer)
                return;

            var old = (Hook?)viewer.GetValue(HookProperty);
            old?.Detach();
            viewer.ClearValue(HookProperty);

            if (e.NewValue is true)
            {
                var hook = new Hook(viewer);
                viewer.SetValue(HookProperty, hook);
                hook.Attach();
            }
        }

        private sealed class Hook
        {
            private const double MaxOverscroll = 60.0;
            private const double ReturnDamping = 0.85;
            private const double Friction = 5.6;
            private const double EdgeAbsorb = 0.55;
            private const double StopVelocity = 16.0;
            private const double Impulse = 9.0;
            private const double MinDt = 1.0 / 240.0;
            private const double MaxDt = 1.0 / 30.0;

            /// <summary>Сколько длится доезд к краю, секунд.</summary>
            private const double GlideSeconds = 0.42;

            /// <summary>Скорость старше этого для броска не годится: рука остановилась прежде, чем отпустить.</summary>
            private const double DragVelocityStaleSeconds = 0.09;

            /// <summary>Потолок скорости броска: рывок мышью не должен уносить в конец длинного списка.</summary>
            private const double MaxDragVelocity = 9000.0;

            private static readonly Stopwatch Clock = Stopwatch.StartNew();

            private readonly ScrollViewer _viewer;
            private readonly EventHandler _onRendering;
            private readonly MouseWheelEventHandler _onWheel;
            private readonly KeyEventHandler _onKeyDown;
            private readonly MouseButtonEventHandler _onBarMouseDown;
            private readonly ScrollEventHandler _onBarScroll;
            private readonly RoutedEventHandler _onLoaded;
            private readonly RoutedEventHandler _onUnloaded;

            private TranslateTransform? _translate;
            private ScrollContentPresenter? _presenter;
            private ScrollBar? _bar;
            private bool _attached;
            private bool _ticking;
            private bool _savedCanContentScroll;
            private bool _hadLocalCanContentScroll;
            private double _lastTime;
            private double _velocity;
            private double _virtual;
            private bool _virtualValid;

            private bool _gliding;
            private ScrollEdge _glideEdge;
            private double _glideFrom;
            private double _glideStarted;
            private Action? _glideArrived;

            private bool _dragArmed;
            private bool _dragging;
            private bool _dragCapturing;
            private Point _dragPress;
            private double _dragOriginY;
            private double _dragBase;
            private double _dragLastVirtual;
            private double _dragLastAt;
            private double _dragVelocity;

            /// <summary>Текущую инерцию завело отпущенное перетаскивание, а не колесо.</summary>
            private bool _dragFling;

            public Hook(ScrollViewer viewer)
            {
                _viewer = viewer;
                _onRendering = OnRendering;
                _onWheel = OnWheel;
                _onKeyDown = OnKeyDown;
                _onBarMouseDown = OnBarMouseDown;
                _onBarScroll = OnBarScroll;
                _onLoaded = OnLoaded;
                _onUnloaded = OnUnloaded;
            }

            public bool IsAnimating => _ticking;

            public bool IsGliding => _gliding;

            public bool IsDragging => _dragging;

            public bool DragEnabled { get; set; }

            public void Attach()
            {
                if (_attached)
                    return;
                _attached = true;

                _hadLocalCanContentScroll = _viewer.ReadLocalValue(ScrollViewer.CanContentScrollProperty)
                                            != DependencyProperty.UnsetValue;
                _savedCanContentScroll = _viewer.CanContentScroll;
                _viewer.CanContentScroll = false;
                _viewer.ClipToBounds = true;

                _viewer.PreviewMouseWheel += _onWheel;
                _viewer.PreviewKeyDown += _onKeyDown;
                _viewer.Loaded += _onLoaded;
                _viewer.Unloaded += _onUnloaded;

                DragEnabled = GetDragScroll(_viewer);
                _viewer.PreviewMouseLeftButtonDown += OnDragPress;
                _viewer.PreviewMouseMove += OnDragMove;
                _viewer.PreviewMouseLeftButtonUp += OnDragRelease;
                _viewer.LostMouseCapture += OnDragLostCapture;

                // Всплывающее нажатие на пустом месте иначе дошло бы до корня окна, а там оно
                // уводит мышь в перетаскивание окна (WM_NCLBUTTONDOWN, модальный цикл системы) —
                // и листать было бы уже нечем.
                _viewer.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnDragBubblePress));

                if (_viewer.IsLoaded)
                    BindTemplateParts();
            }

            public void Detach()
            {
                if (!_attached)
                    return;
                _attached = false;

                EndDrag(fling: false);
                FinishGlide();
                StopTicking();
                UnbindTemplateParts();

                _viewer.PreviewMouseWheel -= _onWheel;
                _viewer.PreviewKeyDown -= _onKeyDown;
                _viewer.Loaded -= _onLoaded;
                _viewer.Unloaded -= _onUnloaded;
                _viewer.PreviewMouseLeftButtonDown -= OnDragPress;
                _viewer.PreviewMouseMove -= OnDragMove;
                _viewer.PreviewMouseLeftButtonUp -= OnDragRelease;
                _viewer.LostMouseCapture -= OnDragLostCapture;
                _viewer.RemoveHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnDragBubblePress));

                if (_presenter is not null && ReferenceEquals(_presenter.RenderTransform, _translate))
                    _presenter.RenderTransform = null;

                _translate = null;
                _presenter = null;

                _viewer.ClearValue(UIElement.ClipToBoundsProperty);
                if (_hadLocalCanContentScroll)
                    _viewer.CanContentScroll = _savedCanContentScroll;
                else
                    _viewer.ClearValue(ScrollViewer.CanContentScrollProperty);
            }

            private void OnLoaded(object sender, RoutedEventArgs e) => BindTemplateParts();

            private void OnUnloaded(object sender, RoutedEventArgs e)
            {
                // Popup unloads the viewer every close — keep the hook, just stop the loop.
                EndDrag(fling: false);
                FinishGlide();
                StopTicking();
                _velocity = 0;
                _virtualValid = false;
                if (_translate is not null)
                    _translate.Y = 0;
            }

            private void BindTemplateParts()
            {
                _viewer.ApplyTemplate();
                _presenter ??= FindChild<ScrollContentPresenter>(_viewer);
                if (_presenter is not null && _translate is null)
                {
                    _presenter.ClipToBounds = true;
                    _translate = new TranslateTransform();
                    _presenter.RenderTransform = _translate;
                }

                if (_bar is null)
                {
                    _bar = FindNamedScrollBar(_viewer);
                    if (_bar is not null)
                    {
                        _bar.Scroll += _onBarScroll;
                        _bar.PreviewMouseLeftButtonDown += _onBarMouseDown;
                    }
                }
            }

            private void UnbindTemplateParts()
            {
                if (_bar is null)
                    return;
                _bar.Scroll -= _onBarScroll;
                _bar.PreviewMouseLeftButtonDown -= _onBarMouseDown;
                _bar = null;
            }

            /// <summary>
            /// Колесо: разгон в свою сторону — либо, если крутить нечего или незачем, передача
            /// дальше.
            /// </summary>
            /// <remarks>
            /// <para>
            /// Слушаем <c>PreviewMouseWheel</c>, а он туннелирующий: до вложенного списка событие
            /// идёт через нас, и безусловное <c>e.Handled</c> у страницы настроек забирало колесо
            /// себе прежде, чем его увидят список разрешённых источников, поле промпта или
            /// раскрытая выпадашка. Со стороны это выглядело как «внутри ничего не листается, а
            /// листается страница за ним». Поэтому первым делом смотрим, кому событие вообще
            /// адресовано.
            /// </para>
            /// <para>
            /// Обратный случай — вложенный список, докрученный до края: колесо должно продолжить
            /// страницу за ним, как это делает обычный WPF. Инерция при этом неприкосновенна:
            /// пока она идёт, край краем не считается, там работает резинка.
            /// </para>
            /// </remarks>
            private void OnWheel(object sender, MouseWheelEventArgs e)
            {
                if (AimedDeeper(e))
                {
                    return;
                }

                // Колесо посреди доезда — человек передумал: доезд кончается там, где его
                // застало колесо, а дальше едет обычная инерция с того же места.
                FinishGlide();
                _dragFling = false;

                if (IsStuckFor(e.Delta))
                {
                    HandOver(e);
                    return;
                }

                e.Handled = true;
                SeedVirtual();

                var notch = Math.Max(1, SystemParameters.WheelScrollLines) * 16.0;
                var pixels = -e.Delta / 120.0 * notch;
                _velocity += pixels * Impulse;
                EnsureTicking();
            }

            /// <summary>
            /// Событие метит не в нас, а во что-то внутри, что прокручивается само.
            /// </summary>
            /// <remarks>
            /// Выпадашка живёт в своём окне, поэтому сравнением источников она и ловится: путь по
            /// визуальным родителям из неё в страницу не ведёт, а маршрут события — ведёт.
            /// </remarks>
            private bool AimedDeeper(MouseWheelEventArgs e)
            {
                if (e.OriginalSource is not DependencyObject source)
                {
                    return false;
                }

                if (source is Visual visual &&
                    !ReferenceEquals(
                        PresentationSource.FromVisual(visual),
                        PresentationSource.FromVisual(_viewer)))
                {
                    return true;
                }

                for (var node = source; node is not null && !ReferenceEquals(node, _viewer); node = Up(node))
                {
                    // Уступаем только тому, кому вправду есть что прокрутить. Иначе колесо
                    // застревало бы на всякой мелочи со своим скроллом: у блока кода в чате
                    // внутри RichTextBox, у короткого промпта — пустой PART_ContentHost.
                    if (node is ScrollViewer
                        {
                            VerticalScrollBarVisibility: not ScrollBarVisibility.Disabled,
                            ScrollableHeight: > 0
                        })
                    {
                        return true;
                    }
                }

                return false;
            }

            private static DependencyObject? Up(DependencyObject node) =>
                node is Visual or Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);

            /// <summary>
            /// Отдаёт колесо ближайшему прокручиваемому предку.
            /// </summary>
            /// <remarks>
            /// Не «оставить событие как есть»: пузырьковый <c>MouseWheel</c> до предка всё равно
            /// не дойдёт — его по дороге забирает собственный <c>OnMouseWheel</c> этого
            /// ScrollViewer, и колесо просто залипало бы. Сначала пробуем туннелем, чтобы предок
            /// со своим плавным скроллом принял событие так же, как принял бы своё; не принял —
            /// отдаём обычным путём.
            /// </remarks>
            private void HandOver(MouseWheelEventArgs e)
            {
                if (CodeBlockView.OuterScroller(_viewer) is not { } outer)
                {
                    // Отдавать некому — крутим сами, ради резинки на краю.
                    e.Handled = true;
                    SeedVirtual();
                    _velocity += -e.Delta / 120.0 * Math.Max(1, SystemParameters.WheelScrollLines) * 16.0 * Impulse;
                    EnsureTicking();
                    return;
                }

                e.Handled = true;

                var tunnelled = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                    Source = outer
                };
                outer.RaiseEvent(tunnelled);

                if (tunnelled.Handled)
                {
                    return;
                }

                outer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = outer
                });
            }

            private void OnKeyDown(object sender, KeyEventArgs e)
            {
                switch (e.Key)
                {
                    case Key.Up:
                    case Key.Down:
                    case Key.PageUp:
                    case Key.PageDown:
                    case Key.Home:
                    case Key.End:
                    case Key.Left:
                    case Key.Right:
                        CancelInertia(snap: true);
                        break;
                }
            }

            private void OnBarMouseDown(object sender, MouseButtonEventArgs e) =>
                CancelInertia(snap: true);

            private void OnBarScroll(object sender, ScrollEventArgs e)
            {
                if (e.ScrollEventType is ScrollEventType.ThumbTrack
                    or ScrollEventType.ThumbPosition
                    or ScrollEventType.EndScroll)
                {
                    CancelInertia(snap: true);
                }
            }

            public void CancelInertia(bool snap)
            {
                FinishGlide();
                _velocity = 0;
                if (snap)
                {
                    _virtual = Clamp(_viewer.VerticalOffset, 0, GetMaxOffset());
                    _virtualValid = true;
                    ApplyVisual();
                }

                if (IsSettled())
                    StopTicking();
            }

            /// <summary>
            /// Принимает бросок снаружи.
            /// </summary>
            /// <remarks>
            /// Пересев <c>_virtual</c> здесь безусловный, а не через <c>SeedVirtual</c>: тот молчит,
            /// когда позиция уже считается своей, а перетаскивание перед броском писало
            /// <c>VerticalOffset</c> напрямую — и запомненная позиция отстала от настоящей на всю
            /// длину жеста. С ней инерция на первом же кадре дёрнула бы ленту обратно к началу
            /// перетаскивания.
            /// </remarks>
            public void Fling(double velocity)
            {
                FinishGlide();
                _virtual = Clamp(_viewer.VerticalOffset, 0, GetMaxOffset());
                _virtualValid = true;
                _velocity = velocity;

                if (Math.Abs(velocity) < StopVelocity)
                {
                    StopTicking();
                    return;
                }

                EnsureTicking();
            }

            private void OnRendering(object? sender, EventArgs e)
            {
                if (_gliding)
                {
                    StepGlide();
                    return;
                }

                // Пока содержимое держит рука, кадр не двигает ничего: иначе инерция спорила бы
                // с ней за смещение.
                if (_dragging)
                {
                    return;
                }

                var now = e is RenderingEventArgs re
                    ? re.RenderingTime.TotalSeconds
                    : 0;
                var dt = _lastTime <= 0 || now <= _lastTime ? 1.0 / 120.0 : now - _lastTime;
                _lastTime = now;
                if (dt < MinDt) dt = MinDt;
                else if (dt > MaxDt) dt = MaxDt;

                var max = GetMaxOffset();
                _virtual += _velocity * dt;
                _velocity *= Math.Exp(-Friction * dt);

                if (_virtual < 0 || _virtual > max)
                    _velocity *= Math.Pow(EdgeAbsorb, dt * 60.0);

                if (Math.Abs(_velocity) < StopVelocity)
                {
                    _velocity = 0;
                    if (_virtual < 0)
                    {
                        _virtual *= Math.Pow(ReturnDamping, dt * 60.0);
                        if (_virtual > -0.4)
                            _virtual = 0;
                    }
                    else if (_virtual > max)
                    {
                        var overflow = _virtual - max;
                        overflow *= Math.Pow(ReturnDamping, dt * 60.0);
                        _virtual = overflow < 0.4 ? max : max + overflow;
                    }
                }

                ApplyVisual();

                if (IsSettled())
                {
                    _virtual = Clamp(_virtual, 0, max);
                    _velocity = 0;
                    ApplyVisual();
                    StopTicking();
                }
            }

            // ───────────────────────── доезд к краю ─────────────────────────

            public void Glide(ScrollEdge edge, Action? arrived)
            {
                EndDrag(fling: false);
                CancelInertia(snap: true);

                var max = GetMaxOffset();
                var target = edge == ScrollEdge.Top ? 0 : max;
                var from = _virtual;
                if (Math.Abs(target - from) < 0.5)
                {
                    arrived?.Invoke();
                    return;
                }

                var viewport = _viewer.ViewportHeight;
                if (viewport > 0 && Math.Abs(target - from) > viewport * 3)
                {
                    from = edge == ScrollEdge.Top ? viewport * 1.5 : Math.Max(0, max - (viewport * 1.5));
                    _virtual = from;
                    ApplyVisual();
                }

                _gliding = true;
                _glideEdge = edge;
                _glideFrom = from;
                _glideStarted = Clock.Elapsed.TotalSeconds;
                _glideArrived = arrived;
                EnsureTicking();
            }

            private void StepGlide()
            {
                var t = Math.Clamp((Clock.Elapsed.TotalSeconds - _glideStarted) / GlideSeconds, 0, 1);

                // Кубическое затухание: быстрый старт и мягкая посадка, как у инерции колеса.
                var eased = 1 - Math.Pow(1 - t, 3);
                var target = _glideEdge == ScrollEdge.Top ? 0 : GetMaxOffset();
                _virtual = _glideFrom + ((target - _glideFrom) * eased);
                ApplyVisual();

                if (t >= 1)
                {
                    _virtual = target;
                    _velocity = 0;
                    ApplyVisual();

                    // Цикл — раньше обратного вызова: тот может попросить новый доезд, и
                    // остановка после него погасила бы уже его.
                    StopTicking();
                    FinishGlide();
                }
            }

            /// <summary>Заканчивает доезд, где бы он ни был, и сообщает об этом тому, кто его просил.</summary>
            private void FinishGlide()
            {
                if (!_gliding)
                    return;

                _gliding = false;
                var arrived = _glideArrived;
                _glideArrived = null;
                arrived?.Invoke();
            }

            // ───────────────────────── перетаскивание ─────────────────────────

            private void OnDragPress(object sender, MouseButtonEventArgs e)
            {
                if (!DragEnabled || !SameSource(e) || StartsOwnGesture(e.OriginalSource as DependencyObject))
                {
                    return;
                }

                // Нажатие посреди броска мышью его останавливает, и только: так ведёт себя список
                // под пальцем, и строка, случайно оказавшаяся под курсором, не должна открыться.
                // Инерцию колеса так не гасим — после неё человек сразу щёлкает по найденному.
                if (_ticking && _dragFling && Math.Abs(_velocity) >= StopVelocity * 10)
                {
                    CancelInertia(snap: true);
                    e.Handled = true;
                }

                ArmDrag(e.GetPosition(_viewer));
            }

            public void ArmDrag(Point press)
            {
                if (GetMaxOffset() <= 0 && !GetBounceWhenShort(_viewer))
                {
                    return;
                }

                _dragArmed = true;
                _dragPress = press;
            }

            private void OnDragBubblePress(object sender, MouseButtonEventArgs e)
            {
                if (_dragArmed)
                {
                    e.Handled = true;
                }
            }

            private void OnDragMove(object sender, MouseEventArgs e)
            {
                // _dragCapturing: собственный Mouse.Capture синхронно приводит сюда же движение,
                // и в нём рука, может быть, уже отпущена — жест оборвался бы, не начавшись.
                if (!_dragArmed || _dragCapturing)
                {
                    return;
                }

                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    EndDrag(fling: false);
                    return;
                }

                if (DragTo(e.GetPosition(_viewer)))
                {
                    e.Handled = true;
                }
            }

            /// <summary>Тянет содержимое за мышью. <c>true</c> — перетаскивание вправду идёт.</summary>
            public bool DragTo(Point point)
            {
                if (!_dragArmed)
                {
                    return false;
                }

                if (!_dragging && !BeginDrag(point))
                {
                    return false;
                }

                var now = Clock.Elapsed.TotalSeconds;
                _virtual = _dragBase - (point.Y - _dragOriginY);
                _virtualValid = true;
                ApplyVisual();

                var dt = now - _dragLastAt;
                if (dt > 0.001)
                {
                    // Сглаживание: одно дрогнувшее событие мыши не должно решать силу броска.
                    var instant = (_virtual - _dragLastVirtual) / dt;
                    _dragVelocity = (0.65 * instant) + (0.35 * _dragVelocity);
                    _dragLastVirtual = _virtual;
                    _dragLastAt = now;
                }

                return true;
            }

            private bool BeginDrag(Point point)
            {
                var dx = Math.Abs(point.X - _dragPress.X);
                var dy = Math.Abs(point.Y - _dragPress.Y);
                if (dx < SystemParameters.MinimumHorizontalDragDistance &&
                    dy < SystemParameters.MinimumVerticalDragDistance)
                {
                    return false;
                }

                // Рука пошла вбок — это чужой жест (выделение, слайдер), а не прокрутка.
                if (dx > dy)
                {
                    _dragArmed = false;
                    return false;
                }

                // Мышь уже у кого-то своего: палитра цвета, собственное перетаскивание. Кнопки и
                // списки берут её на каждое нажатие по привычке — у них её забрать можно.
                if (Mouse.Captured is { } owner && !ReferenceEquals(owner, _viewer) &&
                    owner is not (ButtonBase or Selector or ListBoxItem))
                {
                    _dragArmed = false;
                    return false;
                }

                CancelInertia(snap: true);
                _dragging = true;
                _dragBase = _virtual;
                _dragOriginY = point.Y;
                _dragLastVirtual = _virtual;
                _dragLastAt = Clock.Elapsed.TotalSeconds;
                _dragVelocity = 0;

                // Захват отбирает мышь у кнопки под курсором — она теряет нажатие, и клика на
                // отпускании не будет.
                _dragCapturing = true;
                try
                {
                    Mouse.Capture(_viewer);
                }
                finally
                {
                    _dragCapturing = false;
                }

                Mouse.OverrideCursor = Cursors.ScrollNS;
                return true;
            }

            private void OnDragRelease(object sender, MouseButtonEventArgs e)
            {
                var dragged = _dragging;
                EndDrag(fling: true);

                // Кнопка срабатывает на отпускании: без этого перетаскивание, брошенное над
                // строкой чата, открывало бы её.
                if (dragged)
                {
                    e.Handled = true;
                }
            }

            private void OnDragLostCapture(object sender, MouseEventArgs e)
            {
                // Только свой захват: всплывающее «мышь потеряна» от кнопки, у которой мы её и
                // отобрали, оборвало бы жест ровно в мгновение его начала.
                if (_dragging && !_dragCapturing && ReferenceEquals(e.OriginalSource, _viewer))
                {
                    EndDrag(fling: false);
                }
            }

            public void EndDrag(bool fling)
            {
                var wasDragging = _dragging;
                _dragArmed = false;
                _dragging = false;
                if (!wasDragging)
                {
                    return;
                }

                Mouse.OverrideCursor = null;
                if (ReferenceEquals(Mouse.Captured, _viewer))
                {
                    Mouse.Capture(null);
                }

                var fresh = Clock.Elapsed.TotalSeconds - _dragLastAt <= DragVelocityStaleSeconds;
                _velocity = fling && fresh
                    ? Math.Clamp(_dragVelocity, -MaxDragVelocity, MaxDragVelocity)
                    : 0;
                if (Math.Abs(_velocity) < StopVelocity)
                {
                    _velocity = 0;
                }

                _dragFling = _velocity != 0;

                // Отпущенное за краем — резинка сама вернёт его на место тем же циклом.
                if (IsSettled())
                {
                    _virtual = Clamp(_virtual, 0, GetMaxOffset());
                    ApplyVisual();
                    return;
                }

                EnsureTicking();
            }

            /// <summary>
            /// Нажатие пришлось в то, что тянет мышь само: текст, ползунок, полосу прокрутки,
            /// вложенный список со своей прокруткой или кнопку, сработавшую уже на нажатии.
            /// </summary>
            private bool StartsOwnGesture(DependencyObject? source)
            {
                for (var node = source; node is not null && !ReferenceEquals(node, _viewer); node = Up(node))
                {
                    switch (node)
                    {
                        case TextBoxBase or PasswordBox or Thumb or RangeBase or ComboBox:
                        case ButtonBase { ClickMode: ClickMode.Press }:
                        case ScrollViewer { ScrollableHeight: > 0 }:
                            return true;
                    }
                }

                return false;
            }

            private bool SameSource(RoutedEventArgs e) =>
                e.OriginalSource is not Visual visual ||
                ReferenceEquals(PresentationSource.FromVisual(visual), PresentationSource.FromVisual(_viewer));

            private void ApplyVisual()
            {
                var max = GetMaxOffset();
                double offset;
                double ty;
                if (_virtual < 0)
                {
                    offset = 0;
                    ty = Rubber(-_virtual);
                }
                else if (_virtual > max)
                {
                    offset = max;
                    ty = -Rubber(_virtual - max);
                }
                else
                {
                    offset = _virtual;
                    ty = 0;
                }

                if (Math.Abs(_viewer.VerticalOffset - offset) > 0.01)
                    _viewer.ScrollToVerticalOffset(offset);

                if (_translate is null)
                    BindTemplateParts();
                if (_translate is not null && _translate.Y != ty)
                    _translate.Y = ty;
            }

            /// <summary>Прокручивать в эту сторону нечего: содержимое влезло целиком или мы на краю.</summary>
            private bool IsStuckFor(int delta)
            {
                // Пока идёт инерция, краем это не считается: там ещё не отработала резинка, и
                // отдать колесо родителю посреди броска значило бы дёрнуть сразу оба списка.
                if (_ticking)
                {
                    return false;
                }

                var max = GetMaxOffset();
                if (max <= 0)
                {
                    return true;
                }

                var offset = _viewer.VerticalOffset;
                return delta > 0 ? offset <= 0.5 : offset >= max - 0.5;
            }

            /// <summary>
            /// Берёт позицию, от которой поедет бросок.
            /// </summary>
            /// <remarks>
            /// Своя позиция имеет смысл только пока идёт бросок: тогда она и точнее живой (в ней
            /// учтён разгон за кадр) и умеет заходить за край, где работает резинка. Как только
            /// цикл встал, хозяин смещения — сам <c>ScrollViewer</c>, и запомненное число живёт
            /// ровно до того мгновения, когда ленту подвинет кто-нибудь другой: лупа, автопрокрутка,
            /// достройка сообщений, <c>BringIntoView</c>. Раньше здесь стояла проверка только на
            /// «число уже своё», и после них первый же щелчок колеса возвращал ленту туда, где её
            /// застал прошлый бросок, — прыжок на пол-экрана, а дальше всё как ни в чём не бывало.
            /// </remarks>
            private void SeedVirtual()
            {
                if (_virtualValid && _ticking)
                    return;
                _virtual = _viewer.VerticalOffset;
                _virtualValid = true;
            }

            private bool IsSettled()
            {
                if (Math.Abs(_velocity) >= StopVelocity)
                    return false;
                var max = GetMaxOffset();
                return _virtual >= -0.4 && _virtual <= max + 0.4;
            }

            private double GetMaxOffset()
            {
                var max = _viewer.ExtentHeight - _viewer.ViewportHeight;
                return max < 0 ? 0 : max;
            }

            private void EnsureTicking()
            {
                if (_ticking)
                    return;
                _ticking = true;
                _lastTime = 0;
                CompositionTarget.Rendering += _onRendering;
            }

            private void StopTicking()
            {
                if (!_ticking)
                    return;
                _ticking = false;
                _dragFling = false;
                _lastTime = 0;
                CompositionTarget.Rendering -= _onRendering;
                if (IsSettled())
                    _virtualValid = false;
            }

            private static double Rubber(double overflow)
            {
                if (overflow <= 0)
                    return 0;
                var r = MaxOverscroll * (1.0 - Math.Exp(-overflow / MaxOverscroll));
                return r > MaxOverscroll ? MaxOverscroll : r;
            }

            private static double Clamp(double value, double min, double max)
            {
                if (value < min) return min;
                if (value > max) return max;
                return value;
            }

            private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
            {
                var count = VisualTreeHelper.GetChildrenCount(root);
                for (var i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i);
                    if (child is T match)
                        return match;
                    var nested = FindChild<T>(child);
                    if (nested is not null)
                        return nested;
                }

                return null;
            }

            private static ScrollBar? FindNamedScrollBar(DependencyObject root)
            {
                var count = VisualTreeHelper.GetChildrenCount(root);
                for (var i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i);
                    if (child is ScrollBar bar && bar.Name == "PART_VerticalScrollBar")
                        return bar;
                    var nested = FindNamedScrollBar(child);
                    if (nested is not null)
                        return nested;
                }

                return FindChild<ScrollBar>(root);
            }
        }
    }
}
