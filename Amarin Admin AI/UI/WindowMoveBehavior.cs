using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
namespace Amarin.UI
{
    /// <summary>
    /// Перетаскивание окна без рамки: нажатие в шапке превращается в системное перетаскивание.
    /// </summary>
    /// <remarks>
    /// Здесь был ещё и собственный обработчик WM_GETMINMAXINFO для разворота окна, но его
    /// координаты врали на втором мониторе и он дрался за то же сообщение с
    /// <see cref="WindowMaximizeFix"/>. Разворотом занимается только он.
    /// </remarks>
    public static class WindowMoveBehavior
    {
        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        public static void HandleMouseLeftButtonDownForMove(Window window, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                IntPtr windowHandle = new WindowInteropHelper(window).Handle;
                SendMessage(windowHandle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
        }
    }
}
