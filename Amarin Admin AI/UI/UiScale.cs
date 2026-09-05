using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Amarin.UI;

internal static class UiScale
{
    public static readonly int[] Percents = [80, 90, 100, 110, 125, 150, 175, 200, 225, 250];

    private const int WmDpiChanged = 0x02E0;
    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    private static int _percent = 100;
    private static bool _popupHooked;
    private static bool _wndHooked;
    private static bool _pushingDpi;
    private static bool _designCaptured;
    private static double _designMinWidth = 850;
    private static double _designMinHeight = 535;

    public static int Normalize(int percent)
    {
        foreach (var allowed in Percents)
        {
            if (allowed == percent)
            {
                return percent;
            }
        }

        return 100;
    }

    public static CustomPopupPlacement[] PlaceBelowCenter(Size popupSize, Size targetSize, Point offset)
    {
        var x = (targetSize.Width - popupSize.Width) / 2.0 + offset.X;
        var y = targetSize.Height + offset.Y;
        return [new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.Horizontal)];
    }

    public static void AttachCenteredBelowTooltip(FrameworkElement target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ToolTipService.SetPlacement(target, PlacementMode.Custom);
        ToolTipService.SetHorizontalOffset(target, 0);
        if (target.ToolTip is ToolTip tooltip)
        {
            tooltip.Placement = PlacementMode.Custom;
            tooltip.CustomPopupPlacementCallback = PlaceBelowCenter;
        }
    }

    public static void Apply(Window window, FrameworkElement? scaledRoot, int percent)
    {
        ArgumentNullException.ThrowIfNull(window);
        _percent = Normalize(percent);
        if (scaledRoot is not null)
        {
            scaledRoot.LayoutTransform = Transform.Identity;
        }

        CaptureDesignSize(window);
        var factor = _percent / 100.0;
        window.MinWidth = _designMinWidth / factor;
        window.MinHeight = _designMinHeight / factor;

        EnsureWindowHook(window);
        EnsurePopupHook();
        PushWindowDpi(window);
    }

    private static void CaptureDesignSize(Window window)
    {
        if (_designCaptured)
        {
            return;
        }

        if (window.MinWidth > 0)
        {
            _designMinWidth = window.MinWidth;
        }

        if (window.MinHeight > 0)
        {
            _designMinHeight = window.MinHeight;
        }

        _designCaptured = true;
    }

    private static void EnsureWindowHook(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero)
        {
            window.SourceInitialized -= OnSourceInitialized;
            window.SourceInitialized += OnSourceInitialized;
            return;
        }

        AddWndHook(hwnd);
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        AddWndHook(hwnd);
        PushWindowDpi(window);
    }

    private static void AddWndHook(IntPtr hwnd)
    {
        if (_wndHooked || hwnd == IntPtr.Zero)
        {
            return;
        }

        var source = HwndSource.FromHwnd(hwnd);
        if (source is null)
        {
            return;
        }

        source.AddHook(WndProc);
        _wndHooked = true;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmDpiChanged || _pushingDpi || _percent == 100)
        {
            return IntPtr.Zero;
        }

        var osDpi = (uint)(wParam.ToInt64() & 0xFFFF);
        var wanted = EffectiveDpiFromMonitor(osDpi);
        if (wanted == osDpi)
        {
            return IntPtr.Zero;
        }

        if (lParam == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        handled = true;
        SendDpiChanged(hwnd, wanted, CopyRect(lParam));
        return IntPtr.Zero;
    }

    private static void PushWindowDpi(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var wanted = EffectiveDpi(hwnd);
        var current = (uint)Math.Round(VisualTreeHelper.GetDpi(window).PixelsPerInchX);
        if (current == wanted)
        {
            return;
        }

        Native.GetWindowRect(hwnd, out var rect);
        if (rect.Right - rect.Left < 50 || rect.Bottom - rect.Top < 50)
        {
            return;
        }

        var state = window.WindowState;
        SendDpiChanged(hwnd, wanted, rect);
        if (state == WindowState.Maximized && window.WindowState != WindowState.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    private static void EnsurePopupHook()
    {
        if (_popupHooked)
        {
            return;
        }

        _popupHooked = true;
        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnElementLoaded));
        EventManager.RegisterClassHandler(
            typeof(ToolTip),
            ToolTip.OpenedEvent,
            new RoutedEventHandler(OnToolTipOpened));
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        switch (sender)
        {
            case Popup popup:
                HookPopup(popup);
                break;
            case FrameworkElement element when element.Parent is Popup parent:
                HookPopup(parent);
                ApplyPopupDpi(element);
                break;
            case ToolTip tooltip:
                ApplyPopupDpi(tooltip);
                break;
            case ContextMenu menu:
                ApplyPopupDpi(menu);
                break;
        }
    }

    private static void HookPopup(Popup popup)
    {
        popup.Opened -= OnPopupOpened;
        popup.Opened += OnPopupOpened;
        if (popup.IsOpen && popup.Child is FrameworkElement child)
        {
            ApplyPopupDpi(child);
        }
    }

    private static void OnPopupOpened(object? sender, EventArgs e)
    {
        if (sender is Popup { Child: FrameworkElement child })
        {
            ApplyPopupDpi(child);
        }
    }

    private static void OnToolTipOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ToolTip tooltip)
        {
            ApplyPopupDpi(tooltip);
        }
    }

    private static void ApplyPopupDpi(FrameworkElement element)
    {
        element.LayoutTransform = Transform.Identity;
        if (PresentationSource.FromVisual(element) is not HwndSource { Handle: var hwnd } ||
            hwnd == IntPtr.Zero)
        {
            return;
        }

        var wanted = EffectiveDpi(hwnd);
        var current = (uint)Math.Round(VisualTreeHelper.GetDpi(element).PixelsPerInchX);
        if (current == wanted)
        {
            return;
        }

        Native.GetWindowRect(hwnd, out var rect);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 1 || height < 1)
        {
            return;
        }

        var scale = wanted / (double)Math.Max(current, 1u);
        rect.Right = rect.Left + Math.Max(1, (int)Math.Round(width * scale));
        rect.Bottom = rect.Top + Math.Max(1, (int)Math.Round(height * scale));
        SendDpiChanged(hwnd, wanted, rect);
        RepositionAfterDpi(element);
    }

    private static void RepositionAfterDpi(FrameworkElement element)
    {
        element.Dispatcher.BeginInvoke(() =>
        {
            if (element is ToolTip tooltip)
            {
                var placement = tooltip.Placement;
                tooltip.Placement = placement == PlacementMode.Custom
                    ? PlacementMode.Bottom
                    : PlacementMode.Custom;
                tooltip.Placement = placement;
                return;
            }

            if (element.Parent is Popup popup)
            {
                var offset = popup.HorizontalOffset;
                popup.HorizontalOffset = offset + 0.01;
                popup.HorizontalOffset = offset;
            }
        }, DispatcherPriority.Loaded);
    }

    private static uint EffectiveDpi(IntPtr hwnd)
    {
        var monitorDpi = GetMonitorDpi(hwnd);
        return EffectiveDpiFromMonitor(monitorDpi);
    }

    private static uint EffectiveDpiFromMonitor(uint monitorDpi)
    {
        if (monitorDpi == 0)
        {
            monitorDpi = 96;
        }

        var dpi = (uint)Math.Clamp((int)Math.Round(monitorDpi * (_percent / 100.0)), 64, 960);
        return dpi;
    }

    private static uint GetMonitorDpi(IntPtr hwnd)
    {
        var monitor = Native.MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero ||
            Native.GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) != 0 ||
            dpiX == 0)
        {
            return 96;
        }

        return dpiX;
    }

    private static void SendDpiChanged(IntPtr hwnd, uint dpi, RECT rect)
    {
        var packed = (IntPtr)((dpi << 16) | (dpi & 0xFFFF));
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
        try
        {
            Marshal.StructureToPtr(rect, ptr, false);
            _pushingDpi = true;
            Native.SendMessage(hwnd, WmDpiChanged, packed, ptr);
        }
        finally
        {
            _pushingDpi = false;
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static RECT CopyRect(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero)
        {
            return default;
        }

        return Marshal.PtrToStructure<RECT>(lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static class Native
    {
        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("Shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    }
}
