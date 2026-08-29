using System.Windows;
using System.Windows.Media;

namespace Amarin.UI
{
    internal class PerformanceOptimizer
    {
        private readonly Window _window;

        public PerformanceOptimizer(Window window)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            Optimize();
        }

        private void Optimize()
        {
            int renderTier = RenderCapability.Tier >> 16;
            bool hasHardwareAcceleration = renderTier >= 1;

            _window.UseLayoutRounding = true;
            _window.SnapsToDevicePixels = true;

            TextOptions.SetTextFormattingMode(_window, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(_window, TextRenderingMode.ClearType);
            TextOptions.SetTextHintingMode(_window, TextHintingMode.Fixed);

            RenderOptions.SetEdgeMode(_window, EdgeMode.Unspecified);

            RenderOptions.SetBitmapScalingMode(_window, hasHardwareAcceleration
                ? BitmapScalingMode.HighQuality
                : BitmapScalingMode.Linear);

            _window.DpiChanged += OnDpiChanged;
        }

        private void OnDpiChanged(object sender, DpiChangedEventArgs e)
        {
            _window.InvalidateVisual();
            _window.UpdateLayout();
        }
    }
}