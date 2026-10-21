using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Amarin.Computer;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Рамка «Показать»: где человеку нажать. Сквозная для мыши, не активируется и не попадает в
/// снимки — нажимает сквозь неё сам человек, а агент видит экран без неё.
/// </summary>
/// <remarks>
/// Подпись к рамке — на плашке, а не рядом с рамкой: окно рамки ставится в физических пикселях
/// на мониторе элемента, а у текста в нём размер зависел бы от масштаба этого монитора.
/// </remarks>
internal sealed class ComputerHighlight : Window
{
    /// <summary>Насколько рамка шире элемента: обводит, а не закрывает его края.</summary>
    private const int Gap = 6;

    public ComputerHighlight()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "Amarin";
        var frame = new Border
        {
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(6)
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "Accent.Fill");
        Content = frame;
    }

    /// <summary>Обводит область экрана (физические пиксели).</summary>
    internal void Frame(ScreenRect area)
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        _ = ComputerNative.SetWindowPos(handle, ComputerNative.HwndTopmost, area.Left - Gap, area.Top - Gap,
            area.Width + 2 * Gap, area.Height + 2 * Gap, ComputerNative.SwpNoActivate | ComputerNative.SwpShowWindow);
        Show();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ComputerNative.Overlay(new WindowInteropHelper(this).Handle, passThrough: true);
    }
}
