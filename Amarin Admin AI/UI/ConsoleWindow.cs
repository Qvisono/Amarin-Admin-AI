using System.Runtime.InteropServices;

namespace Amarin.UI;

/// <summary>
/// Win32 helpers for the host console window (position, focus).
/// </summary>
internal static class ConsoleWindow
{
    private const int SwpNosize = 0x0001;
    private const int SwpNozorder = 0x0004;
    private const int SwpShowwindow = 0x0040;
    private const int SwRestore = 9;

    public static void CenterOnScreen()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var hwnd = GetConsoleWindow();
            if (hwnd == IntPtr.Zero)
                return;

            ShowWindow(hwnd, SwRestore);

            if (!GetWindowRect(hwnd, out var rect))
                return;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
                return;

            var screenW = GetSystemMetrics(SmCxscreen);
            var screenH = GetSystemMetrics(SmCyscreen);
            if (screenW <= 0 || screenH <= 0)
                return;

            var x = Math.Max(0, (screenW - width) / 2);
            var y = Math.Max(0, (screenH - height) / 2);

            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNosize | SwpNozorder | SwpShowwindow);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // Non-fatal: console still works if positioning fails.
        }
    }

    private const int SmCxscreen = 0;
    private const int SmCyscreen = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
