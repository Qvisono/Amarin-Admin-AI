using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
namespace Amarin.UI
{
    public static class WindowMoveBehavior
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);
        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;
        private const int WM_GETMINMAXINFO = 0x0024;
        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
        public static void EnableNativeWindowMoveBehavior(Window window, int topMargin = 30)
        {
            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }
            window.SourceInitialized += (sender, e) =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                var margins = new MARGINS()
                {
                    cxLeftWidth = 0,
                    cxRightWidth = 0,
                    cyTopHeight = topMargin,
                    cyBottomHeight = 0
                };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);
                HwndSource source = HwndSource.FromHwnd(hwnd);
                source.AddHook(WindowProc);
            };
        }
        public static void HandleMouseLeftButtonDownForMove(Window window, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                IntPtr windowHandle = new WindowInteropHelper(window).Handle;
                SendMessage(windowHandle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
        }
        private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_GETMINMAXINFO:
                    if (lParam != IntPtr.Zero)
                    {
                        MINMAXINFO mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                        IntPtr monitor = MonitorFromWindow(hwnd, 2);
                        if (monitor != IntPtr.Zero)
                        {
                            MONITORINFO monitorInfo = new MONITORINFO();
                            monitorInfo.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                            GetMonitorInfo(monitor, ref monitorInfo);
                            mmi.ptMaxPosition.x = monitorInfo.rcWork.Left;
                            mmi.ptMaxPosition.y = monitorInfo.rcWork.Top;
                            mmi.ptMaxSize.x = monitorInfo.rcWork.Right - monitorInfo.rcWork.Left;
                            mmi.ptMaxSize.y = monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top;
                        }
                        Marshal.StructureToPtr(mmi, lParam, true);
                        handled = true;
                    }
                    break;
            }
            return IntPtr.Zero;
        }
    }
}