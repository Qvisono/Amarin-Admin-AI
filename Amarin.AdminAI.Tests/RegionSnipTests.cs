using System.Windows;
using Amarin.Core;
using Amarin.UI;
using Drawing = System.Drawing;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Рамка, выделенная в окне монитора, попадает в те же пиксели снимка рабочего стола при любом
/// масштабе монитора и с любым его местом; сочетание для неё есть и по умолчанию не занято.
/// </summary>
public sealed class RegionSnipTests
{
    [Fact]
    public void A_frame_on_a_scaled_second_monitor_maps_to_desktop_pixels()
    {
        // Второй монитор 2560×1440 на 150 % слева от главного: окно на нём — 1706,67×960 точек.
        var monitor = new ScreenRect(-2560, 0, 2560, 1440);
        var desktop = new Drawing.Rectangle(-2560, 0, 2560 + 1920, 1440);

        var pixels = RegionSnip.ToPixels(new Rect(100, 50, 200, 100), new Size(2560 / 1.5, 1440 / 1.5), monitor, desktop);

        Assert.Equal(new Drawing.Rectangle(150, 75, 300, 150), pixels);
    }

    [Fact]
    public void A_frame_on_the_primary_monitor_lands_right_of_a_left_monitor()
    {
        var monitor = new ScreenRect(0, 0, 1920, 1080);
        var desktop = new Drawing.Rectangle(-2560, 0, 2560 + 1920, 1440);

        var pixels = RegionSnip.ToPixels(new Rect(10, 20, 30, 40), new Size(1920, 1080), monitor, desktop);

        Assert.Equal(new Drawing.Rectangle(2570, 20, 30, 40), pixels);
    }

    [Fact]
    public void The_region_hotkey_exists_and_takes_no_key_by_default()
    {
        Assert.Contains(GlobalHotkeys.RegionScreenshot, GlobalHotkeys.All);
        Assert.Null(GlobalHotkeys.Default(GlobalHotkeys.RegionScreenshot));
        Assert.Null(GlobalHotkeys.Effective(new WindowsIntegrationSettings(), GlobalHotkeys.RegionScreenshot));
    }
}
