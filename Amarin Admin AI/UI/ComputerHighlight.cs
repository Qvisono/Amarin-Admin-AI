using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Рамка «Показать»: где человеку нажать. Сквозная для мыши, не активируется и не попадает в
/// снимки — нажимает сквозь неё сам человек, а оператор видит экран без неё.
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
        _ = SetWindowPos(handle, new IntPtr(-1), area.Left - Gap, area.Top - Gap, area.Width + 2 * Gap,
            area.Height + 2 * Gap, SwpNoActivate | SwpShowWindow);
        Show();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(handle, GwlExStyle);
        _ = SetWindowLong(handle, GwlExStyle, ex | WsExTransparent | WsExLayered | WsExToolWindow | WsExNoActivate);
        _ = ComputerBar.SetWindowDisplayAffinity(handle, 0x11);
    }

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
