using System.Windows;
using System.Windows.Media;

namespace Amarin.UI
{
    /// <summary>
    /// Разовая настройка отрисовки окна: округление по пикселям, режим сглаживания текста
    /// и качество масштабирования картинок.
    /// </summary>
    internal sealed class PerformanceOptimizer
    {
        private readonly Window _window;

        public PerformanceOptimizer(Window window)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            Optimize();
        }

        private void Optimize()
        {
            _window.UseLayoutRounding = true;
            _window.SnapsToDevicePixels = true;

            RenderOptions.SetEdgeMode(_window, EdgeMode.Unspecified);

            // На программном рендеринге качественное масштабирование считает процессор, и на
            // окне целиком это заметно: там берём линейное.
            var hardware = (RenderCapability.Tier >> 16) >= 1;
            RenderOptions.SetBitmapScalingMode(_window, hardware
                ? BitmapScalingMode.HighQuality
                : BitmapScalingMode.Linear);

            _window.DpiChanged += OnDpiChanged;
        }

        /// <remarks>
        /// Только <c>InvalidateVisual</c>: <c>UpdateLayout</c> считал бы разметку всего окна
        /// синхронно, а <see cref="UiScale"/> шлёт поддельный <c>WM_DPICHANGED</c> на каждый шаг
        /// ползунка масштаба — то есть на каждое движение мыши по нему. WPF пересчитает сам,
        /// на ближайшем кадре.
        /// </remarks>
        private void OnDpiChanged(object sender, DpiChangedEventArgs e) => _window.InvalidateVisual();
    }
}