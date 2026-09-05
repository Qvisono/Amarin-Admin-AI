using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Amarin.UI;

/// <summary>
/// Flashes the window's taskbar button whenever the app is not the one in front. The corner
/// toast is transient and easy to miss; this keeps blinking until the user comes back, so it
/// is the cue that actually survives long enough to be noticed.
/// </summary>
internal static class TaskbarFlash
{
    private const uint FlashwStop = 0;
    private const uint FlashwTray = 0x00000002;
    private const uint FlashwTimerNoFg = 0x0000000C;

    /// <summary>Flash the taskbar button until the window is brought to the foreground.</summary>
    public static void Flash(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Send(window, FlashwTray | FlashwTimerNoFg, uint.MaxValue);
    }

    /// <summary>Clear the flash and restore the taskbar button to its resting state.</summary>
    public static void Stop(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Send(window, FlashwStop, 0);
    }

    private static void Send(Window window, uint flags, uint count)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = flags,
            uCount = count,
            dwTimeout = 0
        };

        try
        {
            FlashWindowEx(ref info);
        }
        catch (EntryPointNotFoundException)
        {
            // Not on Windows / unusual host — the sound cue still fires.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
}
