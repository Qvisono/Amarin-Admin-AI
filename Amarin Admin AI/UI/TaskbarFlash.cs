using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Amarin.UI;

/// <summary>
/// Мигает кнопкой окна на панели задач, когда программа не впереди. Карточка в углу гаснет и
/// легко пропускается, а мигание длится до возвращения человека — этот знак точно заметят.
/// </summary>
internal static class TaskbarFlash
{
    private const uint FlashwStop = 0;
    private const uint FlashwTray = 0x00000002;
    private const uint FlashwTimerNoFg = 0x0000000C;

    /// <summary>Мигать кнопкой на панели задач, пока окно не выведут вперёд.</summary>
    public static void Flash(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Send(window, FlashwTray | FlashwTimerNoFg, uint.MaxValue);
    }

    /// <summary>Погасить мигание и вернуть кнопке обычный вид.</summary>
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
            // Не Windows или необычный хозяин окна — звуковой сигнал всё равно прозвучит.
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
