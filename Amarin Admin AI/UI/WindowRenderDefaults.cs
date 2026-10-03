using System.Windows;
using System.Windows.Media;

namespace Amarin.UI;

/// <summary>Настройки отрисовки, которые окно задаёт один раз для всего своего дерева.</summary>
internal static class WindowRenderDefaults
{
    public static void Apply(Window window)
    {
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;

        // На программном рендеринге качественное масштабирование картинок считает процессор, и
        // на окне целиком это заметно — там берём линейное.
        var hardware = (RenderCapability.Tier >> 16) >= 1;
        RenderOptions.SetBitmapScalingMode(window, hardware ? BitmapScalingMode.HighQuality : BitmapScalingMode.Linear);
    }
}
