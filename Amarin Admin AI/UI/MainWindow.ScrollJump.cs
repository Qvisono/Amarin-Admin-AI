using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media.Animation;

namespace Amarin.UI
{
    /// <summary>
    /// Капсула «в начало / в конец» над лентой чата.
    /// </summary>
    /// <remarks>
    /// Доезд ведёт <see cref="SmoothScroll.GlideTo"/> — той же физикой, что и колесо, поэтому
    /// колесо посреди доезда просто перехватывает его. Пока лента едет, достройка сообщений
    /// ждёт (см. <c>ChatScrollViewer_ScrollChanged</c>): по кадру она строила бы всё, что
    /// мелькнуло, — и в конце строит только то, что осталось на экране.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public partial class MainWindow
    {
        /// <summary>Меньше этого листать нечего — капсула не показывается.</summary>
        private const double ScrollJumpMinRange = 40;

        /// <summary>Сколько не доехать до края, чтобы кнопка к нему ещё считалась нужной.</summary>
        private const double ScrollJumpEdge = 4;

        private bool _scrollJumpShown;

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

        private void ScrollBottomButton_Click(object sender, RoutedEventArgs e) =>
            SmoothScroll.GlideTo(ChatScrollViewer, ScrollEdge.Bottom, () =>
            {
                // Доехали до конца — значит, человек снова следит за ответом.
                _stickToBottom = true;
                OnChatGlideArrived();
            });

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
        /// Показывает капсулу, когда ленту есть куда листать, и гасит кнопку того края, у
        /// которого лента уже стоит.
        /// </summary>
        /// <remarks>
        /// Зовётся на каждое движение прокрутки, поэтому пишет свойства, только когда они
        /// вправду меняются. Смещения — в увеличенных лупой пикселях, как у самой прокрутки.
        /// </remarks>
        private void UpdateScrollJump()
        {
            var viewer = ChatScrollViewer;
            var range = viewer.ScrollableHeight;
            var show = range > ScrollJumpMinRange;

            if (show != _scrollJumpShown)
            {
                _scrollJumpShown = show;
                ScrollJumpPill.IsHitTestVisible = show;
                ScrollJumpPill.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 160 : 120))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
            }

            if (!show)
            {
                return;
            }

            var offset = viewer.VerticalOffset;
            var canUp = offset > ScrollJumpEdge;
            var canDown = offset < range - ScrollJumpEdge;
            if (ScrollTopButton.IsEnabled != canUp)
            {
                ScrollTopButton.IsEnabled = canUp;
            }

            if (ScrollBottomButton.IsEnabled != canDown)
            {
                ScrollBottomButton.IsEnabled = canDown;
            }
        }
    }
}
