using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Amarin.UI
{
    /// <summary>
    /// Лупа над лентой чата: Ctrl с колесом приближает, левая кнопка таскает приближённое.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Увеличивает <see cref="ChatZoomHost"/>, а этот класс — только жест: что считать его началом,
    /// куда съехать, чтобы точка под курсором осталась на месте, и когда остановиться. Арифметика
    /// вынесена в <see cref="ChatZoomMath"/> и проверяется без окна.
    /// </para>
    /// <para>
    /// Свои <c>_scale</c>, <c>_offsetY</c> и <c>_panX</c> — не копия того, что знает WPF, а
    /// источник истины. <c>ScrollToVerticalOffset</c> откладывается, а <c>ExtentHeight</c> и
    /// <c>VerticalOffset</c> публикуются только в конце прохода раскладки: перечитывая их внутри
    /// кадра, в котором масштаб уже поменялся, мы читали бы позапрошлые числа, и точка под
    /// курсором уползала бы. Ровно поэтому же держит свой <c>_virtual</c>
    /// и <see cref="SmoothScroll"/>. Границу вниз считаем по
    /// <see cref="ChatZoomHost.DocumentHeight"/>, а не по <c>ExtentHeight</c>, по той же причине.
    /// </para>
    /// <para>
    /// Обработчики висят на окне, а не на самой ленте, и «это лента» решается попаданием курсора
    /// в её прямоугольник. Иначе никак: ни у прокрутки чата, ни у её шаблона, ни у
    /// <c>MessagesPanel</c> нет заливки, поэтому нажатие в поле справа, в промежутке между
    /// сообщениями или ниже последнего достаётся корневому <c>Grid</c> окна — предку ленты, а не
    /// потомку. Маршрут туннелирования в ленту при этом не заходит вовсе, и обработчик на ней
    /// молчал бы ровно там, где пустого места больше всего.
    /// </para>
    /// </remarks>
    internal sealed class ChatZoom
    {
        /// <summary>Сколько держать плашку масштаба после последнего движения.</summary>
        private static readonly TimeSpan BadgeLinger = TimeSpan.FromMilliseconds(900);

        /// <summary>
        /// Старее этого скорость для броска не годится: рука остановилась прежде, чем отпустить.
        /// </summary>
        private const double VelocityStaleSeconds = 0.09;

        private readonly ScrollViewer _viewer;
        private readonly ChatZoomHost _host;
        private readonly UIElement _badge;
        private readonly TextBlock _badgeText;
        private readonly Func<bool> _blocked;
        private readonly Action _settled;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly DispatcherTimer _badgeTimer;
        private readonly EventHandler _onRendering;

        private double _scale = 1.0;
        private double _targetScale = 1.0;
        private double _offsetY;
        private double _panX;

        private double _anchorDocX;
        private double _anchorDocY;
        private Point _anchorCursor;

        private bool _ticking;
        private double _lastFrame;

        private bool _armed;
        private bool _dragging;
        private bool _capturing;
        private Point _pressPoint;
        private Point _dragOrigin;
        private double _dragBaseOffsetY;
        private double _dragBasePanX;
        private Point _lastMove;
        private double _lastMoveAt;
        private double _velocityX;
        private double _velocityY;

        public ChatZoom(
            Window window,
            ScrollViewer viewer,
            ChatZoomHost host,
            UIElement badge,
            TextBlock badgeText,
            Func<bool> blocked,
            Action settled)
        {
            _viewer = viewer;
            _host = host;
            _badge = badge;
            _badgeText = badgeText;
            _blocked = blocked;
            _settled = settled;
            _onRendering = OnRendering;

            _badgeTimer = new DispatcherTimer { Interval = BadgeLinger };
            _badgeTimer.Tick += (_, _) =>
            {
                _badgeTimer.Stop();
                FadeBadge(0, 240);
            };

            // На окне и туннелем: см. remarks — с пустого места события до ленты не доходят.
            // handledEventsToo не нужен: раньше нас на этом маршруте никого нет.
            window.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));
            window.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnMouseDown));
            window.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnMouseMove));
            window.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMouseUp));

            // Жест кончается вместе с кнопкой, а не вместе с перехватом мыши. На перехвате он
            // висел сперва — и не работал вовсе, по двум причинам сразу. LostMouseCapture
            // пузырьковый: нажатие достаётся RichTextBox'у сообщения, тот забирает мышь себе под
            // выделение, мы её отбираем — и его «мышь потеряна» всплывает через ленту, отменяя
            // жест ровно в то мгновение, когда он начинался. А ещё перехват можно потерять и
            // просто так, ничего не нажимая. Кнопку же видно и в PreviewMouseMove, и в
            // PreviewMouseLeftButtonUp, и врать она не умеет.
            window.Deactivated += (_, _) => EndDrag(fling: false);
            _viewer.SizeChanged += (_, _) => ClampToBounds();
        }

        /// <summary>Во сколько раз лента сейчас увеличена.</summary>
        public double Scale => _scale;

        /// <summary>Лента приближена: с этого мгновения левая кнопка таскает полотно.</summary>
        public bool IsZoomed => _scale > 1.0 + ChatZoomMath.SettleScale;

        /// <summary>
        /// Жест идёт: масштаб едет, полотно тащат или доезжает бросок.
        /// </summary>
        /// <remarks>
        /// На это смотрят автопрокрутка и ленивая достройка сообщений. Первая иначе при
        /// приближении у нижнего края швыряла бы ленту в конец на каждом кадре — во время наезда
        /// <c>ExtentHeight</c> меняется постоянно. Вторая столько же раз проходила бы по всем
        /// сообщениям с пересчётом координат. Обеих зовём один раз, когда жест кончился.
        /// </remarks>
        public bool IsBusy => _ticking || _dragging;

        /// <summary>
        /// Перетаскивание ленты забирает нажатие у перетаскивания окна.
        /// </summary>
        /// <remarks>
        /// <c>WM_NCLBUTTONDOWN</c> уводит мышь в модальный цикл системы: после него WPF сообщений
        /// мыши больше не видит, и панорамирование уже не начнётся. Значит, решать надо на самом
        /// нажатии, а не когда рука поехала.
        /// </remarks>
        public bool SuppressesWindowDrag() => _armed || _dragging;

        /// <summary>Возвращает обычный вид немедленно — для смены чата и ухода в настройки.</summary>
        public void Reset()
        {
            EndDrag(fling: false);
            StopTicking();

            _targetScale = 1.0;
            _scale = 1.0;
            _offsetY = _viewer.VerticalOffset;
            _panX = 0;
            _velocityX = 0;
            _velocityY = 0;

            _host.Scale = 1.0;
            _host.PanX = 0;

            _badgeTimer.Stop();
            FadeBadge(0, 0);
        }

        /// <summary>Возвращает обычный вид с той же плавностью, что и приближение.</summary>
        public void ResetSmoothly()
        {
            if (!IsZoomed)
            {
                return;
            }

            // Якорь — середина видимой области: отъезд «к себе» читается спокойнее, чем к углу.
            AdoptLiveOffset();
            CaptureAnchor(new Point(_viewer.ViewportWidth / 2, _viewer.ViewportHeight / 2));
            _targetScale = 1.0;
            ShowBadge();
            EnsureTicking();
        }

        /// <summary>Esc возвращает обычный вид, если лента приближена.</summary>
        public bool TryHandleEscape()
        {
            if (!IsZoomed)
            {
                return false;
            }

            ResetSmoothly();
            return true;
        }

        // ───────────────────────── колесо ─────────────────────────

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            var cursor = e.GetPosition(_viewer);
            if (!ChatZoomMath.ClaimsWheel(
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
                    _blocked(),
                    SameSource(e),
                    cursor,
                    _viewer.RenderSize))
            {
                return;
            }

            // Гасит колесо для SmoothScroll: тот подписан обычным +=, то есть мимо уже
            // обработанных событий. Трогать его код ради лупы не приходится.
            e.Handled = true;
            ZoomBy(e.Delta, cursor);
        }

        /// <summary>
        /// Шаг приближения от колеса, уже без вопроса, наш ли это жест.
        /// </summary>
        /// <remarks>
        /// Отдельно от обработчика ради тестов: <c>RaiseEvent</c> не ставит
        /// <c>Keyboard.Modifiers</c>, и поднятое в тесте колесо никогда не выглядит нажатым
        /// с Ctrl. Само условие проверяется своими тестами — см. <c>ChatZoomMath.ClaimsWheel</c>.
        /// </remarks>
        internal void ZoomBy(int delta, Point cursor)
        {
            var next = ChatZoomMath.StepScale(_targetScale, delta);
            if (Math.Abs(next - _targetScale) < ChatZoomMath.SettleScale)
            {
                // Уже на упоре. Плашку всё равно показываем: без неё жест выглядит сломанным.
                ShowBadge();
                return;
            }

            // Гасим чужую инерцию: иначе кадр за кадром вертикаль будет тянуть в свою сторону
            // и она, и мы — с тем же раздвоением, что и у брошенной ленты. Заодно снимается
            // резинка, на длину которой иначе съехала бы точка под курсором.
            SmoothScroll.Cancel(_viewer);
            AdoptLiveOffset();
            CaptureAnchor(cursor);
            _targetScale = next;
            ShowBadge();
            EnsureTicking();
        }

        // ───────────────────────── перетаскивание ─────────────────────────

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsZoomed || _blocked() || !SameSource(e))
            {
                return;
            }

            var p = e.GetPosition(_viewer);
            if (!ChatZoomMath.Inside(p, _viewer.RenderSize))
            {
                return;
            }

            // Двойной клик сбрасывает лупу только на пустом месте. Два быстрых нажатия на
            // кнопку сообщения или двойной клик по слову — это действие над ними, а не просьба
            // вернуть обычный вид; раньше лупа слетала и от них, а слово не выделялось вовсе.
            if (e.ClickCount == 2 && !IsInteractive(e.OriginalSource as DependencyObject))
            {
                ResetSmoothly();
                e.Handled = true;
                return;
            }

            // Нажатие не забираем: оно должно нормально достаться тексту, ссылке или кнопке.
            // Перетаскиванием это станет только за порогом — там и заберём.
            ArmPan(p);
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            // _capturing: наш собственный Mouse.Capture чуть ниже синхронно синтезирует движение
            // мыши и приводит его сюда же. Состояние кнопок в нём настоящее, и в тот единственный
            // кадр, когда рука уже отпустила, а жест ещё начинается, оно обрывало бы жест сразу
            // после старта. Своё движение — не ввод.
            if (!_armed || _capturing)
            {
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndDrag(fling: false);
                return;
            }

            PanTo(e.GetPosition(_viewer));
        }

        /// <summary>
        /// Нажали — но тащить ещё не начали.
        /// </summary>
        /// <remarks>
        /// Отдельно от обработчика ради тестов, как и <see cref="ZoomBy"/>: положение мыши в
        /// поднятом <c>RaiseEvent</c> событии берётся у настоящего курсора, подставить его нельзя,
        /// и проверить перетаскивание через события не получится.
        /// </remarks>
        internal void ArmPan(Point start)
        {
            _armed = true;
            _pressPoint = start;
            _lastMove = start;
            _lastMoveAt = Now;
            _velocityX = 0;
            _velocityY = 0;
        }

        /// <summary>Полотно тащат прямо сейчас.</summary>
        internal bool IsPanning => _dragging;

        /// <summary>Отпустили: перетаскивание кончилось, дальше — бросок, если он был.</summary>
        /// <inheritdoc cref="ArmPan"/>
        internal void EndPan(bool fling) => EndDrag(fling);

        /// <summary>Тянет полотно за курсором. Отдаёт <c>true</c>, если перетаскивание вправду идёт.</summary>
        /// <inheritdoc cref="ArmPan"/>
        internal bool PanTo(Point p)
        {
            if (!_armed)
            {
                return false;
            }

            if (!_dragging && !BeginDrag(p))
            {
                return false;
            }

            var now = Now;
            var dt = now - _lastMoveAt;
            if (dt > 0.001)
            {
                _velocityX = (p.X - _lastMove.X) / dt;

                // Мышь вниз — лента вниз — смещение прокрутки уменьшается, отсюда минус.
                _velocityY = -(p.Y - _lastMove.Y) / dt;
                _lastMove = p;
                _lastMoveAt = now;
            }

            _panX = ChatZoomMath.ClampPanX(_dragBasePanX + (p.X - _dragOrigin.X), _scale, _viewer.ViewportWidth);
            _offsetY = Math.Clamp(
                _dragBaseOffsetY - (p.Y - _dragOrigin.Y),
                0,
                ChatZoomMath.MaxOffsetY(_host.DocumentHeight, _scale, _viewer.ViewportHeight));

            Push(vertical: true);
            return true;
        }

        private bool BeginDrag(Point p)
        {
            if (Math.Abs(p.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return false;
            }

            // Снять живое смещение надо прежде, чем объявить себя тащащими: AdoptLiveOffset
            // молчит во время жеста, и после _dragging = true он бы уже ничего не взял. С тем,
            // что осталось от прошлого раза, лента на первом же движении прыгала бы туда.
            SmoothScroll.Cancel(_viewer);
            AdoptLiveOffset();
            _dragging = true;

            // Перехвата довольно, чтобы RichTextBox перестал тянуть выделение: он получит
            // LostMouseCapture. Событие при этом не забираем — иначе перестал бы разворачиваться
            // композер, он слушает тот же PreviewMouseMove окна.
            _capturing = true;
            try
            {
                Mouse.Capture(_viewer);
            }
            finally
            {
                _capturing = false;
            }

            Mouse.OverrideCursor = Cursors.ScrollAll;

            _dragOrigin = p;
            _dragBaseOffsetY = _offsetY;
            _dragBasePanX = _panX;
            _lastMove = p;
            _lastMoveAt = Now;
            return true;
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            var dragged = _dragging;
            EndDrag(fling: true);

            // Hyperlink срабатывает на отпускании: без этого перетаскивание, брошенное над
            // ссылкой, открывало бы её.
            if (dragged)
            {
                e.Handled = true;
            }
        }

        private void EndDrag(bool fling)
        {
            var wasDragging = _dragging;
            _armed = false;
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

            if (!fling || Now - _lastMoveAt > VelocityStaleSeconds)
            {
                // Рука успела остановиться — бросок был бы выдумкой.
                _velocityX = 0;
                _velocityY = 0;
            }

            if (Math.Abs(_velocityY) >= ChatZoomMath.StopVelocity)
            {
                SmoothScroll.Fling(_viewer, _velocityY);
            }

            if (Math.Abs(_velocityX) >= ChatZoomMath.StopVelocity)
            {
                EnsureTicking();
            }
            else
            {
                _velocityX = 0;
                _settled();
            }
        }

        // ───────────────────────── кадр ─────────────────────────

        private void OnRendering(object? sender, EventArgs e)
        {
            var now = e is RenderingEventArgs frame ? frame.RenderingTime.TotalSeconds : Now;
            var dt = ChatZoomMath.StepTime(_lastFrame <= 0 || now <= _lastFrame ? 1.0 / 120.0 : now - _lastFrame);
            _lastFrame = now;

            var zooming = false;

            if (Math.Abs(_targetScale - _scale) > ChatZoomMath.SettleScale)
            {
                _scale = ChatZoomMath.FollowScale(_scale, _targetScale, dt);
                zooming = true;
            }
            else if (_scale != _targetScale)
            {
                _scale = _targetScale;
                zooming = true;
            }

            if (zooming)
            {
                _host.Scale = _scale;

                // Считаем от запомненной точки, а не от прошлого кадра: у края смещение упирается
                // в границу, и накопленный зажим уводил бы ленту из-под курсора.
                _offsetY = ChatZoomMath.OffsetForDocumentY(
                    _anchorDocY, _anchorCursor.Y, _scale, _host.DocumentHeight, _viewer.ViewportHeight);
                _panX = ChatZoomMath.PanForDocumentX(
                    _anchorDocX, _anchorCursor.X, _scale, _viewer.ViewportWidth);

                _badgeText.Text = FormatScale(_scale);
            }

            var gliding = false;
            if (!_dragging && Math.Abs(_velocityX) >= ChatZoomMath.StopVelocity)
            {
                _panX = ChatZoomMath.ClampPanX(_panX + (_velocityX * dt), _scale, _viewer.ViewportWidth);
                _velocityX = ChatZoomMath.FollowVelocity(_velocityX, dt);
                gliding = true;
            }
            else
            {
                _velocityX = 0;
            }

            // Вертикаль наша, только пока едет масштаб. Горизонтальный бросок до неё не касается:
            // ею в это время правит инерция SmoothScroll.
            Push(vertical: zooming);

            if (!zooming && !gliding)
            {
                StopTicking();
                _settled();
            }
        }

        /// <summary>
        /// Отдаёт посчитанное окну: масштаб, сдвиг вбок и — если вертикаль наша — смещение прокрутки.
        /// </summary>
        /// <remarks>
        /// Про <paramref name="vertical"/>. У смещения прокрутки хозяин в каждый момент один.
        /// Пока доезжает брошенная лента, им распоряжается <see cref="SmoothScroll"/>, а наш цикл
        /// может быть ещё жив — им доезжает горизонталь. Если писать смещение и оттуда, два
        /// хозяина начинают спорить через кадр: один ведёт ленту дальше, другой возвращает её
        /// туда, где её отпустила рука. На экране это выглядит как два текста сразу — один едет,
        /// другой стоит, — и стоящий пропадает, едва инерция кончится. Поэтому на чужих кадрах мы
        /// не пишем, а читаем: иначе к своему следующему жесту пришли бы с числом из позапрошлого.
        /// </remarks>
        private void Push(bool vertical)
        {
            _host.Scale = _scale;
            _host.PanX = _panX;

            if (!vertical)
            {
                _offsetY = _viewer.VerticalOffset;
                return;
            }

            if (Math.Abs(_viewer.VerticalOffset - _offsetY) > 0.01)
            {
                _viewer.ScrollToVerticalOffset(_offsetY);
            }
        }

        /// <summary>
        /// Берёт за своё то смещение, на котором лента стоит сейчас.
        /// </summary>
        /// <remarks>
        /// Между жестами лентой распоряжаются другие — колесо, автопрокрутка, достройка сообщений.
        /// Начав новый жест со старым числом, мы дёрнули бы ленту туда, где её оставили в прошлый
        /// раз.
        /// </remarks>
        private void AdoptLiveOffset()
        {
            if (!_ticking && !_dragging)
            {
                _offsetY = _viewer.VerticalOffset;
            }
        }

        private void CaptureAnchor(Point cursor)
        {
            _anchorCursor = cursor;
            _anchorDocY = ChatZoomMath.DocumentY(_offsetY, cursor.Y, _scale);
            _anchorDocX = ChatZoomMath.DocumentX(_panX, cursor.X, _scale);
        }

        private void ClampToBounds()
        {
            _offsetY = Math.Clamp(
                _offsetY, 0, ChatZoomMath.MaxOffsetY(_host.DocumentHeight, _scale, _viewer.ViewportHeight));
            _panX = ChatZoomMath.ClampPanX(_panX, _scale, _viewer.ViewportWidth);
            _host.PanX = _panX;
        }

        /// <summary>
        /// Событие пришло из этого же окна.
        /// </summary>
        /// <remarks>
        /// Выпадашка модели геометрически лежит над лентой, но живёт отдельным окном, и колесо в
        /// ней — не наше дело. Тем же сравнением её ловит <c>SmoothScroll.AimedDeeper</c>.
        /// </remarks>
        private bool SameSource(RoutedEventArgs e) =>
            e.OriginalSource is not Visual visual ||
            ReferenceEquals(
                PresentationSource.FromVisual(visual),
                PresentationSource.FromVisual(_viewer));

        /// <summary>Нажатие пришлось в кнопку, ссылку, текст или полосу прокрутки ленты.</summary>
        private bool IsInteractive(DependencyObject? source)
        {
            for (var node = source; node is not null && !ReferenceEquals(node, _viewer); node = Parent(node))
            {
                if (node is System.Windows.Controls.Primitives.ButtonBase or
                    System.Windows.Controls.Primitives.TextBoxBase or
                    System.Windows.Controls.Primitives.ScrollBar or
                    System.Windows.Documents.Hyperlink)
                {
                    return true;
                }
            }

            return false;
        }

        private static DependencyObject? Parent(DependencyObject node) =>
            node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);

        private double Now => _clock.Elapsed.TotalSeconds;

        private static string FormatScale(double scale) =>
            Math.Round(scale * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

        private void EnsureTicking()
        {
            if (_ticking)
            {
                return;
            }

            _ticking = true;
            _lastFrame = 0;
            CompositionTarget.Rendering += _onRendering;
        }

        private void StopTicking()
        {
            if (!_ticking)
            {
                return;
            }

            _ticking = false;
            _lastFrame = 0;
            CompositionTarget.Rendering -= _onRendering;
        }

        // ───────────────────────── плашка масштаба ─────────────────────────

        private void ShowBadge()
        {
            _badgeText.Text = FormatScale(_targetScale);
            FadeBadge(1, 90);
            _badgeTimer.Stop();
            _badgeTimer.Start();
        }

        private void FadeBadge(double to, double milliseconds)
        {
            if (milliseconds <= 0)
            {
                _badge.BeginAnimation(UIElement.OpacityProperty, null);
                _badge.Opacity = to;
                return;
            }

            _badge.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
                {
                    FillBehavior = FillBehavior.HoldEnd
                });
        }
    }
}
