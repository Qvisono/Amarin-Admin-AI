using System.Runtime.InteropServices;
using System.Windows;

namespace Amarin.UI;

/// <summary>
/// Где ставить карточки в правом нижнем углу: рабочая область монитора главного окна.
/// </summary>
/// <remarks>
/// Одно на карточку «ответ готов» (<see cref="NotificationToast"/>) и напоминания
/// (<see cref="ReminderToast"/>): монитор берётся у главного окна, а не основной — человек с двумя
/// экранами ждёт уведомление там, где работает с программой.
/// </remarks>
internal static class ToastScreen
{
    /// <summary>Рабочая область монитора окна, в пикселях устройства; null — основной монитор.</summary>
    public static Rect? WorkAreaOf(IntPtr ownerHandle)
    {
        if (ownerHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var monitor = MonitorFromWindow(ownerHandle, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return null;
            }

            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return null;
            }

            return new Rect(
                info.rcWork.left,
                info.rcWork.top,
                info.rcWork.right - info.rcWork.left,
                info.rcWork.bottom - info.rcWork.top);
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Рабочая область в единицах WPF для этого окна; если её не узнать —
    /// <see cref="SystemParameters.WorkArea"/> основного монитора.
    /// </summary>
    public static Rect InDips(Window window, Rect? device)
    {
        if (device is not { } area)
        {
            return SystemParameters.WorkArea;
        }

        var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice;
        if (transform is null)
        {
            return SystemParameters.WorkArea;
        }

        var topLeft = transform.Value.Transform(new Point(area.Left, area.Top));
        var bottomRight = transform.Value.Transform(new Point(area.Right, area.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>
    /// Стиль «инструмент» и «без активации»: окна нет в Alt+Tab, и оно не отнимает фокус у
    /// программы, в которой человек работает.
    /// </summary>
    /// <remarks>
    /// Владельца у карточек нет: подчинённое окно прячется со свёрнутым владельцем, а нужны они
    /// именно тогда. Поэтому из Alt+Tab их убирает стиль, а не владелец.
    /// </remarks>
    public static void MakeQuiet(IntPtr handle)
    {
        var ex = GetWindowLong(handle, GwlExStyle);
        _ = SetWindowLong(handle, GwlExStyle, ex | WsExToolWindow | WsExNoActivate);
    }

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }
}
