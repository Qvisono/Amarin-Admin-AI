using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Amarin.UI
{
    /// <summary>
    /// Кнопки «в начало» и «в конец» над лентой чата.
    /// </summary>
    /// <remarks>
    /// Доезд ведёт <see cref="SmoothScroll.GlideTo"/> — той же физикой, что и колесо, поэтому
    /// колесо посреди доезда просто перехватывает его. Пока лента едет, достройка сообщений
    /// ждёт (см. <c>ChatScrollViewer_ScrollChanged</c>): по кадру она строила бы всё, что
    /// мелькнуло, — и в конце строит только то, что осталось на экране.
    /// <para>
    /// Каждая кнопка стоит у того края, куда ведёт, и видна, только пока этот край далеко: у
    /// низа чата — там, где человек проводит почти всё время, — «в конец» не нужна, и ленту
    /// ничто не загораживает. Первая версия держала обе стрелки капсулой справа внизу постоянно,
    /// поверх текста, — и выглядело это как помеха, а не помощь.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public partial class MainWindow
    {
        /// <summary>
        /// Край считается далёким, когда до него больше этой доли экрана — но не меньше
        /// <see cref="ScrollJumpMinDistance"/>: иначе кнопка мигала бы от пары строк прокрутки.
        /// </summary>
        private const double ScrollJumpViewportShare = 0.35;

        private const double ScrollJumpMinDistance = 120;

        private bool _scrollTopShown;
        private bool _scrollBottomShown;

        /// <summary>Задержка перед подсказкой «в конец» — та же, что у всех подсказок (<see cref="ToolTipDefaults"/>).</summary>
        private static readonly TimeSpan ScrollBottomHintDelay = TimeSpan.FromMilliseconds(220);

        private DispatcherTimer? _scrollBottomHintTimer;
        private bool _scrollBottomHintShown;

        /// <summary>Заводит задержку подсказки «в конец».</summary>
        private void InitializeScrollJump()
        {
            _scrollBottomHintTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
            {
                Interval = ScrollBottomHintDelay
            };
            _scrollBottomHintTimer.Tick += (_, _) =>
            {
                _scrollBottomHintTimer.Stop();
                if (ScrollBottomButton.IsMouseOver && _scrollBottomShown)
                {
                    ShowScrollBottomHint(true);
                }
            };
        }

        private void ScrollBottomButton_MouseEnter(object sender, MouseEventArgs e) =>
            _scrollBottomHintTimer?.Start();

        private void ScrollBottomButton_MouseLeave(object sender, MouseEventArgs e)
        {
            _scrollBottomHintTimer?.Stop();
            ShowScrollBottomHint(false);
        }

        /// <summary>Проявляет или гасит подсказку «в конец». Отдельно ради тестов.</summary>
        internal void ShowScrollBottomHint(bool show)
        {
            if (show == _scrollBottomHintShown)
            {
                return;
            }

            _scrollBottomHintShown = show;
            ScrollBottomHint.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 120 : 90)));
        }

        private void ScrollTopButton_Click(object sender, RoutedEventArgs e)
        {
            _stickToBottom = false;

            // Сначала гасим бросок: пока он идёт, якорная достройка не держит вид на месте.
            SmoothScroll.Cancel(ChatScrollViewer);

            // Голова переписки строится заранее, пока лента стоит на месте: иначе на подъезде
            // были бы видны пустые места под ещё не построенные сообщения.
            MaterializeHead();
            SmoothScroll.GlideTo(ChatScrollViewer, ScrollEdge.Top, OnChatGlideArrived);
        }

        private void ScrollBottomButton_Click(object sender, RoutedEventArgs e)
        {
            _scrollBottomHintTimer?.Stop();
            ShowScrollBottomHint(false);
            SmoothScroll.GlideTo(ChatScrollViewer, ScrollEdge.Bottom, () =>
            {
                // Доехали до конца — значит, человек снова следит за ответом.
                _stickToBottom = true;
                OnChatGlideArrived();
            });
        }

        private void OnChatGlideArrived()
        {
            MaterializeAroundViewport();
            UpdateScrollJump();
        }

        /// <summary>Строит начало переписки на экран с запасом, не сдвигая того, что видно сейчас.</summary>
        private void MaterializeHead()
        {
            if (_unbuiltMessages == 0)
            {
                return;
            }

            var budget = DocViewportHeight() + MaterializeLead;
            MaterializeAnchored(() =>
            {
                var built = 0;
                var filled = 0.0;
                for (var i = 0; i < _messageHosts.Count && filled <= budget; i++)
                {
                    var host = _messageHosts[i];
                    filled += host.IsMaterialized ? host.ActualHeight : host.Reserved;
                    if (!host.IsMaterialized)
                    {
                        MaterializeHost(host);
                        built++;
                    }
                }

                return built;
            });
        }

        /// <summary>
        /// Показывает кнопку края, пока тот далеко, и прячет, когда он рядом.
        /// </summary>
        /// <remarks>
        /// Зовётся на каждое движение прокрутки, поэтому анимацию заводит, только когда видимость
        /// вправду меняется. Смещения — в увеличенных лупой пикселях, как у самой прокрутки, и
        /// порог от высоты экрана считается в них же.
        /// </remarks>
        private void UpdateScrollJump()
        {
            var viewer = ChatScrollViewer;
            var far = Math.Max(ScrollJumpMinDistance, viewer.ViewportHeight * ScrollJumpViewportShare);
            var offset = viewer.VerticalOffset;

            var showTop = offset > far;
            var showBottom = viewer.ScrollableHeight - offset > far;

            if (showTop != _scrollTopShown)
            {
                _scrollTopShown = showTop;
                Reveal(ScrollTopButton, showTop, shift: null);
            }

            if (showBottom != _scrollBottomShown)
            {
                _scrollBottomShown = showBottom;
                Reveal(ScrollBottomButton, showBottom, ScrollBottomShift);
                if (!showBottom)
                {
                    ShowScrollBottomHint(false);
                }
            }
        }

        /// <summary>Проявляет или гасит кнопку; у круглой ещё и приподнимает её снизу.</summary>
        private static void Reveal(UIElement button, bool show, TranslateTransform? shift)
        {
            button.IsHitTestVisible = show;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(show ? 180 : 130);

            button.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(show ? 1 : 0, duration) { EasingFunction = ease });

            shift?.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(show ? 0 : 8, duration) { EasingFunction = ease });
        }
    }
}
