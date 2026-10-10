using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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

        /// <summary>
        /// Автопрокрутка средней кнопкой, как в браузере (1.33.0): нажатие ставит метку, и список
        /// едет в ту сторону, куда увели от неё мышь, — тем быстрее, чем дальше.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Третье умение того же хука и на его физике, а не своё: пока идёт автопрокрутка, хук
        /// держит <see cref="IsInMotionProperty"/> и считается анимацией (<see cref="IsAnimating"/>),
        /// поэтому всё, что уже уступает инерции колеса, — достройка ленты, автоследование за
        /// ответом, наведение на строки колонки чатов — уступает и ей, без единой правки там.
        /// </para>
        /// <para>
        /// Нажатие разбирает классовый обработчик окна, а не сам список: у ленты и её шаблона нет
        /// заливки, и средняя кнопка на пустом месте между сообщениями достаётся корню окна —
        /// предку ленты, а не потомку (то же, что у лупы, см. <c>ChatZoom</c>).
        /// </para>
        /// </remarks>
        public static readonly DependencyProperty PanScrollProperty =
            DependencyProperty.RegisterAttached(
                "PanScroll",
                typeof(bool),
                typeof(SmoothScroll),
                new PropertyMetadata(false, OnPanScrollChanged));

        private static readonly DependencyPropertyKey IsInMotionKey =
            DependencyProperty.RegisterAttachedReadOnly(
                "IsInMotion",
                typeof(bool),
                typeof(SmoothScroll),
                new PropertyMetadata(false, OnIsInMotionChanged));

        /// <summary>Список тронулся или встал (<see cref="IsInMotionProperty"/>).</summary>
        /// <remarks>
        /// Событием, а не подпиской на свойство: подписка через описатель свойства стоила бы
        /// конструктору окна отражения, а это событие — одного <c>AddHandler</c>.
        /// </remarks>
        public static readonly RoutedEvent MotionChangedEvent =
            EventManager.RegisterRoutedEvent("MotionChanged", RoutingStrategy.Direct, typeof(RoutedEventHandler), typeof(SmoothScroll));

        private static void OnIsInMotionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is UIElement element)
            {
                element.RaiseEvent(new RoutedEventArgs(MotionChangedEvent, element));
            }
        }

        /// <summary>
        /// Список едет сам — инерцией колеса, доездом к краю или средней кнопкой, — и под неподвижной мышью
        /// проезжают строки.
        /// </summary>
        /// <remarks>
        /// По нему список гасит на это время то, что строка делает при наведении: в колонке
        /// чатов каждая строка под мышью раскладывала заново заголовок и кнопку «⋯», и прокрутка
        /// над строками дёргалась, а над пустым местом у полосы шла гладко (1.32.0).
        /// </remarks>
        public static readonly DependencyProperty IsInMotionProperty = IsInMotionKey.DependencyProperty;

        public static bool GetIsInMotion(DependencyObject obj) =>
            (bool)obj.GetValue(IsInMotionProperty);

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

        public static bool GetPanScroll(DependencyObject obj) =>
            (bool)obj.GetValue(PanScrollProperty);

        public static void SetPanScroll(DependencyObject obj, bool value) =>
            obj.SetValue(PanScrollProperty, value);

        /// <summary>Идёт автопрокрутка средней кнопкой.</summary>
        public static bool IsPanScrolling(ScrollViewer viewer) =>
            ((Hook?)viewer.GetValue(HookProperty))?.IsPanScrolling ?? false;

        /// <summary>Начать автопрокрутку с меткой в этой точке — в обход событий, ради тестов.</summary>
        /// <remarks>Положение мыши в поднятом событии берётся у настоящего курсора — см. <see cref="ArmDrag"/>.</remarks>
        /// <param name="held">Зажата ли средняя кнопка: от этого зависит, кончит ли прокрутку её отпускание.</param>
        internal static bool BeginPanScroll(ScrollViewer viewer, Point origin, bool held = false) =>
            ((Hook?)viewer.GetValue(HookProperty))?.BeginPanScroll(origin, held) ?? false;

        /// <summary>Метка автопрокрутки на этом списке — для тестов и снимков.</summary>
        internal static FrameworkElement? PanScrollMarkerOf(ScrollViewer viewer) =>
            ((Hook?)viewer.GetValue(HookProperty))?.Marker;

        /// <summary>Один кадр автопрокрутки с мышью в этой точке, <paramref name="seconds"/> спустя прошлый.</summary>
        /// <inheritdoc cref="BeginPanScroll"/>
        internal static void PanScrollFrame(ScrollViewer viewer, Point pointer, double seconds) =>
            ((Hook?)viewer.GetValue(HookProperty))?.StepPan(pointer, seconds);

        /// <summary>Отпущена средняя кнопка — ради тестов, как и <see cref="BeginPanScroll"/>.</summary>
        internal static void ReleasePanScrollButton(ScrollViewer viewer) =>
            ((Hook?)viewer.GetValue(HookProperty))?.ReleasePanButton();

        /// <summary>Закончить автопрокрутку, где бы она ни шла.</summary>
        public static void EndPanScroll(ScrollViewer viewer) =>
            ((Hook?)viewer.GetValue(HookProperty))?.EndPanScroll();

        /// <summary>Пикселей в секунду и в какую сторону, когда мышь увели от метки на столько.</summary>
        /// <param name="length">Сколько всего можно пролистать (<c>ScrollableHeight</c>): от него — предельная скорость.</param>
        /// <remarks>
        /// <para>
        /// Мёртвая зона у метки — чтобы дрожь руки после щелчка не трогала список. Рядом с меткой
        /// скорость растёт чуть быстрее расстояния: в паре сантиметров читается строка за строкой.
        /// </para>
        /// <para>
        /// Дальше <see cref="PanRushStart"/> скорость разгоняется экспоненциально и к
        /// <see cref="PanFullAt"/> доходит до предельной (<see cref="PanTopSpeed"/>), с которой весь
        /// список пролетает за <see cref="PanCrossSeconds"/>, какой бы он ни был длины. До этого предел
        /// был постоянным, 50 000 точек в секунду, и чат на сотни тысяч точек листался до верха много
        /// секунд — человек просил: «увёл мышь высоко — и за секунду наверху».
        /// </para>
        /// </remarks>
        internal static double PanScrollSpeed(double offset, double length = 0)
        {
            var distance = Math.Abs(offset) - PanDeadZone;
            if (distance <= 0)
            {
                return 0;
            }

            if (distance <= PanRushStart)
            {
                return Math.Sign(offset) * PanGain * Math.Pow(distance, PanExponent);
            }

            var start = PanGain * Math.Pow(PanRushStart, PanExponent);
            var share = Math.Min(1, (distance - PanRushStart) / (PanFullAt - PanRushStart));
            return Math.Sign(offset) * start * Math.Pow(PanTopSpeed(length) / start, share);
        }

        /// <summary>Предельная скорость для списка такой длины: весь за <see cref="PanCrossSeconds"/>, но не медленнее <see cref="PanMaxSpeed"/>.</summary>
        internal static double PanTopSpeed(double length) => Math.Max(PanMaxSpeed, length / PanCrossSeconds);

        internal const double PanDeadZone = 12;
        private const double PanGain = 5;
        private const double PanExponent = 1.25;

        /// <summary>С этого расстояния (за мёртвой зоной) скорость разгоняется экспоненциально.</summary>
        internal const double PanRushStart = 250;

        /// <summary>
        /// С этого расстояния (за мёртвой зоной) скорость предельная. Не дальше: при масштабе 150 % это
        /// уже шестьсот точек экрана, и выше метки столько места бывает не всегда.
        /// </summary>
        internal const double PanFullAt = 400;

        /// <summary>За сколько секунд предельная скорость проходит весь список.</summary>
        internal const double PanCrossSeconds = 0.6;

        /// <summary>Предельная скорость короткого списка: на нём весь путь и так — доля секунды.</summary>
        internal const double PanMaxSpeed = 50_000;

        /// <summary>Хуки с автопрокруткой, чьи списки сейчас в дереве, — кандидаты для нажатия на пустом месте.</summary>
        private static readonly HashSet<Hook> PanHooks = [];

        /// <summary>Автопрокрутка, которая идёт сейчас: она одна на программу, как и мышь.</summary>
        private static Hook? _activePan;

        private static bool _routerRegistered;

        private static void OnPanScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ScrollViewer viewer)
            {
                return;
            }

            if (e.NewValue is true && !_routerRegistered)
            {
                // Классовые обработчики — одни на процесс: они видят нажатие, клавишу и колесо
                // раньше любого обработчика самого окна, а подписка на каждое окно потребовала бы
                // следить за окнами. Клавиши — именно так: окно своим PreviewKeyDown забирало Esc
                // (и останавливало им ответ) раньше, чем его видела автопрокрутка.
                _routerRegistered = true;
                EventManager.RegisterClassHandler(
                    typeof(Window),
                    UIElement.PreviewMouseDownEvent,
                    new MouseButtonEventHandler(OnWindowPreviewMouseDown));
                EventManager.RegisterClassHandler(
                    typeof(Window),
                    UIElement.PreviewMouseUpEvent,
                    new MouseButtonEventHandler(OnWindowPreviewMouseUp));
                EventManager.RegisterClassHandler(
                    typeof(Window),
                    UIElement.PreviewKeyDownEvent,
                    new KeyEventHandler(OnWindowPreviewKeyDown));
                EventManager.RegisterClassHandler(
                    typeof(Window),
                    UIElement.PreviewMouseWheelEvent,
                    new MouseWheelEventHandler(OnWindowPreviewMouseWheel));
            }

            if (viewer.GetValue(HookProperty) is Hook hook)
            {
                hook.PanEnabled = e.NewValue is true;
            }
        }

        /// <summary>
        /// Любое нажатие в окне: средняя кнопка начинает автопрокрутку, а пока она идёт, любая
        /// кнопка её заканчивает — и сама никуда не доходит, как в браузере.
        /// </summary>
        private static void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Отпускание, которое ждали, могло случиться за окном: новое нажатие его уже не ждёт.
            _swallowRelease = null;
            if (_activePan is { } active)
            {
                active.EndPanScroll();
                e.Handled = true;

                // Отпускание той же кнопки тоже гасится: правая иначе открыла бы на отпускании
                // контекстное меню того, что под курсором.
                _swallowRelease = e.ChangedButton;
                return;
            }

            // Пока зажата другая кнопка, идёт свой жест — перетаскивание чата, рамка, лупа: средняя
            // кнопка посреди него отняла бы у жеста мышь, и смещение тянули бы двое.
            if (e.ChangedButton != MouseButton.Middle ||
                e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed ||
                FindPanTarget(e) is not { } target)
            {
                return;
            }

            if (target.BeginPanScroll(e.GetPosition(target.Viewer)))
            {
                e.Handled = true;
            }
        }

        private static void OnWindowPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_swallowRelease is { } button && button == e.ChangedButton)
            {
                _swallowRelease = null;
                e.Handled = true;
            }
        }

        /// <summary>
        /// Любая клавиша заканчивает автопрокрутку. Esc этим и исчерпывается — иначе тот же Esc
        /// остановил бы ответ, закрыл настройки или сбросил лупу; остальные клавиши идут дальше
        /// (горячая клавиша срабатывает, а прокрутка уже не пишет в чужой чат).
        /// </summary>
        private static void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_activePan is not { } active)
            {
                return;
            }

            active.EndPanScroll();
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
            }
        }

        /// <summary>
        /// Колесо, в том числе с Ctrl, заканчивает автопрокрутку и дальше идёт своим путём: лупа
        /// ловит Ctrl+колесо на окне, и до списка оно не доходило.
        /// </summary>
        private static void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
            _activePan?.EndPanScroll();

        /// <summary>Кнопка, чьё отпускание надо погасить: ею закончили автопрокрутку.</summary>
        private static MouseButton? _swallowRelease;

        /// <summary>
        /// Чей список листать: ближайший предок нажатия с автопрокруткой, которому есть что
        /// листать, — а если нажали на пустом месте, то список, в чьи границы попала точка и чьим
        /// предком является то, на что нажали.
        /// </summary>
        /// <remarks>
        /// Второе правило ловит пустые места без заливки (лента, страница настроек): нажатие там
        /// достаётся предку списка. Требование «источник — предок списка» отсекает слои поверх:
        /// рамка, заслоняющая чат, или подложка настроек — соседи ленты, а не её предки, и
        /// нажатие на них ленту не листает.
        /// </remarks>
        private static Hook? FindPanTarget(MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source)
            {
                return null;
            }

            for (var node = source; node is not null; node = Hook.Up(node))
            {
                if (node is ScrollViewer viewer && viewer.GetValue(HookProperty) is Hook { PanEnabled: true } own && own.CanPanScroll)
                {
                    return own;
                }
            }

            if (source is not Visual visual)
            {
                return null;
            }

            var sourceRoot = PresentationSource.FromVisual(visual);
            Hook? best = null;
            var bestArea = double.MaxValue;
            foreach (var hook in PanHooks)
            {
                var viewer = hook.Viewer;
                if (!hook.CanPanScroll || !viewer.IsVisible || !ReferenceEquals(PresentationSource.FromVisual(viewer), sourceRoot) ||
                    !visual.IsAncestorOf(viewer))
                {
                    continue;
                }

                var point = e.GetPosition(viewer);
                if (point.X < 0 || point.Y < 0 || point.X > viewer.ActualWidth || point.Y > viewer.ActualHeight)
                {
                    continue;
                }

                // Самый тесный из подходящих — самый внутренний.
                var area = viewer.ActualWidth * viewer.ActualHeight;
                if (area < bestArea)
                {
                    best = hook;
                    bestArea = area;
                }
            }

            return best;
        }

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
            private const double MaxPanDt = 0.25;

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

            private bool _panEnabled;
            private bool _pan;
            private bool _autoCapturing;
            private Point _autoOrigin;

            /// <summary>Средняя кнопка ещё зажата с того нажатия, что начало автопрокрутку.</summary>
            private bool _autoHeld;

            /// <summary>Пока кнопку держали, мышь ушла из мёртвой зоны: отпускание заканчивает прокрутку.</summary>
            private bool _autoDragged;

            /// <summary>Куда показывает метка: -1 вверх, 1 вниз, 0 — стоим.</summary>
            private int _autoDirection;

            private PanScrollMarker? _panMarker;
            private Window? _panWindow;

            /// <summary>Курсор, который стоял до автопрокрутки, — его она и возвращает.</summary>
            private Cursor? _cursorBefore;

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

            public ScrollViewer Viewer => _viewer;

            public bool IsPanScrolling => _pan;

            public FrameworkElement? Marker => _panMarker;

            /// <summary>Автопрокрутка включена у этого списка; список в дереве — значит, он кандидат.</summary>
            public bool PanEnabled
            {
                get => _panEnabled;
                set
                {
                    _panEnabled = value;
                    if (!value)
                    {
                        EndPanScroll();
                    }

                    RegisterPan();
                }
            }

            /// <summary>Есть что листать: автопрокрутка над коротким списком только мешала бы.</summary>
            public bool CanPanScroll => _attached && _viewer.IsLoaded && GetMaxOffset() > 0;

            /// <param name="loaded">В дереве ли список сейчас; null — спросить у него самого.</param>
            /// <remarks>
            /// Из <c>Loaded</c> и <c>Unloaded</c> ответ передаётся явно: что успел показать
            /// <see cref="FrameworkElement.IsLoaded"/> в миг самого события, зависит от порядка внутри WPF.
            /// </remarks>
            private void RegisterPan(bool? loaded = null)
            {
                if (_attached && _panEnabled && (loaded ?? _viewer.IsLoaded))
                {
                    PanHooks.Add(this);
                }
                else
                {
                    PanHooks.Remove(this);
                }
            }

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

                _viewer.PreviewMouseUp += OnPanMouseUp;
                _viewer.LostMouseCapture += OnPanLostCapture;
                _panEnabled = GetPanScroll(_viewer);

                if (_viewer.IsLoaded)
                    BindTemplateParts();

                RegisterPan();
            }

            public void Detach()
            {
                if (!_attached)
                    return;
                _attached = false;

                EndPanScroll();
                RegisterPan();
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
                _viewer.PreviewMouseUp -= OnPanMouseUp;
                _viewer.LostMouseCapture -= OnPanLostCapture;

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

            private void OnLoaded(object sender, RoutedEventArgs e)
            {
                BindTemplateParts();
                RegisterPan(loaded: true);
            }

            private void OnUnloaded(object sender, RoutedEventArgs e)
            {
                // Popup unloads the viewer every close — keep the hook, just stop the loop.
                EndPanScroll();
                RegisterPan(loaded: false);
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
                // Колесо посреди автопрокрутки — человек передумал, как и в браузере: метка
                // уходит, а колесо дальше крутит обычным порядком.
                EndPanScroll();

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

            internal static DependencyObject? Up(DependencyObject node) =>
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
                if (_pan)
                {
                    var frame = e is RenderingEventArgs rendering ? rendering.RenderingTime.TotalSeconds : 0;
                    var seconds = _lastTime <= 0 || frame <= _lastTime ? 1.0 / 120.0 : frame - _lastTime;
                    _lastTime = frame;
                    StepPan(Mouse.GetPosition(_viewer), seconds);
                    return;
                }

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

            // ───────────────────────── автопрокрутка ─────────────────────────

            /// <summary>Начинает автопрокрутку с меткой в этой точке (координаты списка).</summary>
            /// <returns><c>false</c> — листать нечего, и нажатие остаётся тому, кому шло.</returns>
            /// <param name="held">Зажата ли средняя кнопка; null — спросить у мыши. Тестам — своё значение.</param>
            public bool BeginPanScroll(Point origin, bool? held = null)
            {
                if (!_panEnabled || !CanPanScroll)
                {
                    return false;
                }

                // Чужая автопрокрутка в другом списке (вторая средняя кнопка, пока шла первая,
                // сюда не доходит — её гасит обработчик окна) и свои жесты кончаются здесь.
                _activePan?.EndPanScroll();
                EndDrag(fling: false);
                FinishGlide();
                CancelInertia(snap: true);

                _pan = true;
                _activePan = this;
                _autoOrigin = origin;
                _autoHeld = held ?? Mouse.MiddleButton == MouseButtonState.Pressed;
                _autoDirection = 0;
                _autoDragged = false;
                _velocity = 0;
                _virtual = Clamp(_viewer.VerticalOffset, 0, GetMaxOffset());
                _virtualValid = true;

                if (AdornerLayer.GetAdornerLayer(_viewer) is { } layer)
                {
                    _panMarker = new PanScrollMarker(_viewer, origin);
                    layer.Add(_panMarker);
                }

                // Клавиши разбирает классовый обработчик окна (OnWindowPreviewKeyDown): подписка
                // здесь шла бы после обработчиков самого окна, и Esc раньше доходил бы до них.
                _panWindow = Window.GetWindow(_viewer);
                if (_panWindow is not null)
                {
                    _panWindow.Deactivated += OnPanDeactivated;
                }

                // Мышь — себе: кнопка, нажатая под курсором после метки, не должна сработать, а
                // движение за краем списка должно доходить. Её же «потерю» ловит OnPanLostCapture.
                _autoCapturing = true;
                try
                {
                    Mouse.Capture(_viewer, CaptureMode.Element);
                }
                finally
                {
                    _autoCapturing = false;
                }

                _cursorBefore = Mouse.OverrideCursor;
                Mouse.OverrideCursor = Cursors.ScrollNS;
                EnsureTicking();
                return true;
            }

            /// <summary>
            /// Один кадр: скорость — от того, насколько мышь ушла от метки по вертикали.
            /// </summary>
            /// <remarks>
            /// Без резинки и трения: край — это край, как в браузере. Отдельный путь в кадре, а
            /// не общая инерция, потому что скорость здесь задаёт рука каждое мгновение, а не
            /// разгон, который затухает.
            /// </remarks>
            public void StepPan(Point pointer, double seconds)
            {
                if (!_pan)
                {
                    return;
                }

                // Шаг — настоящее время кадра, до четверти секунды: на тяжёлом чате, пролетающем
                // экраны за кадр, кадры бывают и по 5 в секунду, и потолок короче сам срезал бы
                // скорость ровно там, где она нужнее всего. Длиннее — уже не кадр, а зависание.
                var dt = Math.Clamp(seconds, MinDt, MaxPanDt);
                var offset = pointer.Y - _autoOrigin.Y;
                if (_autoHeld && Math.Abs(offset) > PanDeadZone)
                {
                    _autoDragged = true;
                }

                var speed = PanScrollSpeed(offset, GetMaxOffset());
                _virtual = Clamp(_virtual + (speed * dt), 0, GetMaxOffset());
                _velocity = 0;
                ApplyVisual();

                // «В движении» — только пока список и правда едет: метка, которая стоит в мёртвой
                // зоне, не должна держать достройку ленты и следование за ответом.
                _viewer.SetValue(IsInMotionKey, speed != 0);

                var direction = Math.Sign(speed);
                if (direction == _autoDirection)
                {
                    return;
                }

                _autoDirection = direction;
                _panMarker?.PointTo(direction);
                Mouse.OverrideCursor = direction switch
                {
                    < 0 => Cursors.ScrollN,
                    > 0 => Cursors.ScrollS,
                    _ => Cursors.ScrollNS
                };
            }

            /// <summary>
            /// Отпустили среднюю кнопку. Держали и вели — это был жест, и он кончился; просто
            /// щёлкнули — метка остаётся до следующего щелчка, как в браузере.
            /// </summary>
            public void ReleasePanButton()
            {
                if (!_pan || !_autoHeld)
                {
                    return;
                }

                _autoHeld = false;
                if (_autoDragged)
                {
                    EndPanScroll();
                }
            }

            public void EndPanScroll()
            {
                if (!_pan)
                {
                    return;
                }

                _pan = false;
                _autoHeld = false;
                if (ReferenceEquals(_activePan, this))
                {
                    _activePan = null;
                }

                if (_panMarker is { } marker)
                {
                    AdornerLayer.GetAdornerLayer(_viewer)?.Remove(marker);
                    _panMarker = null;
                }

                if (_panWindow is { } window)
                {
                    window.Deactivated -= OnPanDeactivated;
                    _panWindow = null;
                }

                // Свой курсор снимается возвратом прежнего, а не сбросом: чужой «песочные часы»
                // под автопрокруткой не должны пропасть вместе с ней.
                Mouse.OverrideCursor = _cursorBefore;
                _cursorBefore = null;
                if (ReferenceEquals(Mouse.Captured, _viewer))
                {
                    _autoCapturing = true;
                    try
                    {
                        Mouse.Capture(null);
                    }
                    finally
                    {
                        _autoCapturing = false;
                    }
                }

                _velocity = 0;
                _virtual = Clamp(_virtual, 0, GetMaxOffset());
                StopTicking();
            }

            private void OnPanMouseUp(object sender, MouseButtonEventArgs e)
            {
                if (!_pan || e.ChangedButton != MouseButton.Middle)
                {
                    return;
                }

                ReleasePanButton();
                e.Handled = true;
            }

            private void OnPanLostCapture(object sender, MouseEventArgs e)
            {
                // Только свой захват — см. OnDragLostCapture: всплывающая потеря чужого захвата
                // оборвала бы прокрутку в мгновение её начала.
                if (_pan && !_autoCapturing && ReferenceEquals(e.OriginalSource, _viewer))
                {
                    EndPanScroll();
                }
            }

            private void OnPanDeactivated(object? sender, EventArgs e) => EndPanScroll();

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
                _viewer.SetValue(IsInMotionKey, true);
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
                _viewer.SetValue(IsInMotionKey, false);
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

        /// <summary>
        /// Метка автопрокрутки: кружок на месте нажатия и стрелки вверх и вниз. Стрелка той
        /// стороны, куда едет список, горит акцентом.
        /// </summary>
        /// <remarks>
        /// Слоем украшений над списком, а не элементом в нём: метка стоит на месте, пока под ней
        /// едет содержимое, и не участвует ни в раскладке, ни в попадании мыши. Кисти — из темы на
        /// момент нажатия: тема посреди жеста не меняется, а следить за ней ради кружка, который
        /// живёт секунды, незачем.
        /// </remarks>
        private sealed class PanScrollMarker : Adorner
        {
            private const double Radius = 15;

            private readonly Point _origin;
            private readonly Brush _fill;
            private readonly Pen _ring;
            private readonly Brush _dot;
            private readonly Pen _idle;
            private readonly Pen _faint;
            private readonly Pen _active;
            private int _direction;

            public PanScrollMarker(FrameworkElement adorned, Point origin)
                : base(adorned)
            {
                _origin = origin;
                IsHitTestVisible = false;
                Focusable = false;

                _fill = Themed(adorned, "Bg.Panel", Color.FromRgb(0x24, 0x24, 0x24));
                _ring = Line(Themed(adorned, "Border.Default", Color.FromRgb(0x44, 0x44, 0x44)), 1);
                _dot = Themed(adorned, "Text.Dim", Color.FromRgb(0x9A, 0x9A, 0x9A));
                _idle = Line(_dot, 1.7);
                _faint = Line(Themed(adorned, "Text.Faint", Color.FromRgb(0x6A, 0x6A, 0x6A)), 1.7);
                _active = Line(Themed(adorned, "Accent.Fill", Color.FromRgb(0x4C, 0x8D, 0xF6)), 1.9);

                // Тень — та же мягкая, что у карточек и попапов: метка висит над содержимым.
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 10,
                    ShadowDepth = 1.5,
                    Direction = 270,
                    Opacity = 0.3,
                    Color = Colors.Black
                };
            }

            /// <summary>Куда смотрит список: -1 вверх, 1 вниз, 0 — стоим в мёртвой зоне.</summary>
            public void PointTo(int direction)
            {
                if (direction == _direction)
                {
                    return;
                }

                _direction = direction;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext drawing)
            {
                var c = _origin;
                drawing.DrawEllipse(_fill, _ring, c, Radius, Radius);
                drawing.DrawEllipse(_dot, null, c, 1.7, 1.7);
                Chevron(drawing, c, up: true, _direction < 0 ? _active : _direction > 0 ? _faint : _idle);
                Chevron(drawing, c, up: false, _direction > 0 ? _active : _direction < 0 ? _faint : _idle);
            }

            private static void Chevron(DrawingContext drawing, Point center, bool up, Pen pen)
            {
                var sign = up ? -1 : 1;
                var tip = new Point(center.X, center.Y + (sign * 9));
                drawing.DrawLine(pen, new Point(center.X - 4.2, center.Y + (sign * 4.8)), tip);
                drawing.DrawLine(pen, tip, new Point(center.X + 4.2, center.Y + (sign * 4.8)));
            }

            private static Brush Themed(FrameworkElement host, string key, Color fallback) =>
                host.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

            private static Pen Line(Brush brush, double thickness)
            {
                var pen = new Pen(brush, thickness)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                return pen;
            }
        }
    }
}
