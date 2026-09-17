using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Память о размере окна между запусками.
/// </summary>
/// <remarks>
/// <para>
/// Размер хранится в физических пикселях экрана, а не в <see cref="FrameworkElement.Width"/>.
/// Масштаб интерфейса здесь сделан поддельным <c>WM_DPICHANGED</c> (<see cref="UiScale"/>):
/// окно остаётся тех же размеров на экране, а логические единицы под ним меняются. Сохранив
/// логическую ширину, программа открывала бы окно в полтора раза шире только оттого, что
/// человек сменил масштаб или перетащил окно на монитор с другим DPI.
/// </para>
/// <para>
/// Позиция не сохраняется намеренно: окно по-прежнему открывается по центру экрана, на котором
/// человек работает сейчас, а не там, где стояло на другом мониторе, которого уже нет.
/// </para>
/// <para>
/// Перехватчиков оконных сообщений здесь нет и заводить их не надо: <c>WM_GETMINMAXINFO</c>
/// разбирает один только <see cref="WindowMaximizeFix"/>, и второй обработчик того же сообщения
/// уже однажды врал координатами на втором мониторе.
/// </para>
/// </remarks>
internal static class WindowGeometry
{
    private const int SwShowMaximized = 3;
    private const int SwShowMinimized = 2;

    /// <summary>Свёрнутое окно развернётся обратно во весь экран.</summary>
    private const int RestoreToMaximized = 0x0002;

    private const int MonitorDefaultToNearest = 2;

    /// <summary>Запоминает размер окна. Зовётся на закрытии, пока у окна ещё есть хэндл.</summary>
    public static void Capture(Window window, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.RememberWindowSize)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref placement))
        {
            return;
        }

        // rcNormalPosition — размер окна в обычном состоянии, даже когда оно развёрнуто или
        // свёрнуто. Именно к нему окно вернётся, и именно его человек считает своим размером.
        var width = placement.rcNormalPosition.right - placement.rcNormalPosition.left;
        var height = placement.rcNormalPosition.bottom - placement.rcNormalPosition.top;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        settings.WindowPixelWidth = width;
        settings.WindowPixelHeight = height;
        settings.WindowMaximized = placement.showCmd == SwShowMaximized ||
                                   (placement.showCmd == SwShowMinimized &&
                                    (placement.flags & RestoreToMaximized) != 0);
    }

    /// <summary>
    /// Возвращает окну прошлый размер.
    /// </summary>
    /// <remarks>
    /// Звать после <see cref="UiScale.Apply"/> и до <c>Show()</c>. Раньше нельзя: поддельный
    /// <c>WM_DPICHANGED</c> приходит с текущим прямоугольником окна и домножит выставленный
    /// размер на масштаб. Позже — тоже: окно успеет мигнуть на экране прежним размером.
    /// </remarks>
    public static void Restore(Window window, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.RememberWindowSize)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (settings.WindowPixelWidth > 0 && settings.WindowPixelHeight > 0 &&
            TryToLogical(hwnd, settings.WindowPixelWidth, settings.WindowPixelHeight, out var size))
        {
            // Минимумы окна плавают вместе с масштабом интерфейса (UiScale ставит их как
            // 850 / factor), поэтому зажимаем уже в единицах окна и по текущим значениям.
            window.Width = Math.Max(size.Width, window.MinWidth);
            window.Height = Math.Max(size.Height, window.MinHeight);
        }

        if (settings.WindowMaximized)
        {
            // Состоянием, а не прямоугольником: развёрнутому окну размер всё равно перепишет
            // WindowMaximizeFix, а UiScale возвращает Maximized после своего поддельного DPI.
            window.WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// Переводит сохранённые пиксели в единицы окна, попутно зажимая размер минимумом окна и
    /// рабочей областью монитора.
    /// </summary>
    /// <remarks>
    /// Пересчёт через <c>CompositionTarget.TransformFromDevice</c>, а не через реальный DPI
    /// монитора — по той же причине, по которой так считает
    /// <see cref="WindowMaximizeFix"/>: масштаб интерфейса это подделанный DPI, и произведение
    /// преобразования на плавающий минимум окна постоянно при любом масштабе.
    /// </remarks>
    private static bool TryToLogical(IntPtr hwnd, int pixelWidth, int pixelHeight, out Size size)
    {
        size = default;

        if (hwnd == IntPtr.Zero ||
            HwndSource.FromHwnd(hwnd) is not { CompositionTarget: { } target })
        {
            return false;
        }

        if (TryGetWorkArea(hwnd, out var workWidth, out var workHeight))
        {
            pixelWidth = Math.Min(pixelWidth, workWidth);
            pixelHeight = Math.Min(pixelHeight, workHeight);
        }

        var scale = target.TransformFromDevice;
        var width = pixelWidth * scale.M11;
        var height = pixelHeight * scale.M22;
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0)
        {
            return false;
        }

        size = new Size(width, height);
        return true;
    }

    private static bool TryGetWorkArea(IntPtr hwnd, out int width, out int height)
    {
        width = 0;
        height = 0;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        width = info.rcWork.right - info.rcWork.left;
        height = info.rcWork.bottom - info.rcWork.top;
        return width > 0 && height > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
