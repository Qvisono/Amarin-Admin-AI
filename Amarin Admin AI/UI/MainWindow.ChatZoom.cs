using System.Runtime.Versioning;
using System.Windows;

namespace Amarin.UI
{
    /// <summary>
    /// Привязка лупы над лентой чата к окну: сам жест живёт в <see cref="ChatZoom"/>.
    /// </summary>
    /// <remarks>
    /// Здесь только то, что лупе нужно знать об окне и окну о лупе: закрыт ли чат оверлеем, куда
    /// сообщить об окончании жеста и где его сбросить.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public partial class MainWindow
    {
        private ChatZoom? _chatZoom;

        /// <summary>Жест лупы идёт прямо сейчас: масштаб едет, ленту тащат или доезжает бросок.</summary>
        private bool ChatZoomBusy => _chatZoom?.IsBusy == true;

        private void InitializeChatZoom() =>
            _chatZoom = new ChatZoom(
                this,
                ChatScrollViewer,
                ChatZoomLayer,
                ChatZoomBadge,
                ChatZoomBadgeText,
                ChatCoveredByOverlay,
                MaterializeAroundViewport);

        /// <summary>
        /// Поверх чата открыт оверлей — жест не наш.
        /// </summary>
        /// <remarks>
        /// Обработчики лупы висят на окне и решают по координатам курсора, а оверлеи лежат тем же
        /// окном поверх ленты: без этой проверки колесо над открытыми настройками или над
        /// просмотром картинки приближало бы ленту за ними. У просмотра картинки вдобавок своё
        /// колесо и своё перетаскивание — их перебивать нельзя.
        /// </remarks>
        private bool ChatCoveredByOverlay() =>
            SettingsOverlay.Visibility == Visibility.Visible ||
            ImageViewerOverlay.Visibility == Visibility.Visible ||
            JournalOverlay.Visibility == Visibility.Visible ||
            ConfirmationOverlay.Visibility == Visibility.Visible ||
            DomainOverlay.Visibility == Visibility.Visible;

        /// <summary>Возвращает ленте обычный вид.</summary>
        private void ResetChatZoom() => _chatZoom?.Reset();
    }
}
