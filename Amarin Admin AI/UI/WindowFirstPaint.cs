using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Amarin.UI;

/// <summary>
/// Заливает окно цветом фона темы, пока WPF не нарисовал первый кадр.
/// </summary>
/// <remarks>
/// <para>
/// <c>Show()</c> выводит окно на экран сразу, а первый кадр WPF рисует только после обработчиков
/// <c>Loaded</c> и раскладки: на холодном запуске это полсекунды, под отладчиком — секунды. Всё
/// это время Windows показывала на месте окна белый прямоугольник: кисти фона у класса окна WPF
/// нет, а <c>WM_ERASEBKGND</c> сам WPF не красит. Здесь на него отвечает заливка цветом
/// <c>Bg.Window</c>: окно появляется цвета фона программы, и содержимое проступает поверх.
/// </para>
/// <para>
/// После первого кадра перехватчик снимается: дальше стирание фона закрашивало бы уже
/// нарисованное, пока WPF не перерисует. Ставится он через хэндл, как у
/// <see cref="WindowMaximizeFix"/>: на <c>SourceInitialized</c> дерево ещё не привязано к источнику.
/// </para>
/// </remarks>
internal sealed class WindowFirstPaint
{
    private const int WM_ERASEBKGND = 0x0014;

    private readonly Window _window;
    private HwndSource? _source;
    private bool _rendered;

    private WindowFirstPaint(Window window) => _window = window;

    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var paint = new WindowFirstPaint(window);
        window.ContentRendered += paint.OnContentRendered;
        if (!paint.TryHook())
        {
            window.SourceInitialized += (_, _) => paint.TryHook();
        }
    }

    private bool TryHook()
    {
        if (_source is not null || _rendered)
        {
            return true;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero || HwndSource.FromHwnd(handle) is not { } source)
        {
            return false;
        }

        source.AddHook(Hook);
        _source = source;
        return true;
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        _rendered = true;
        _window.ContentRendered -= OnContentRendered;
        _source?.RemoveHook(Hook);
        _source = null;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_ERASEBKGND || _rendered || wParam == IntPtr.Zero ||
            _window.TryFindResource("Bg.Window") is not SolidColorBrush brush ||
            !GetClientRect(hwnd, out var area))
        {
            return IntPtr.Zero;
        }

        var color = brush.Color;
        var fill = CreateSolidBrush((uint)(color.R | (color.G << 8) | (color.B << 16)));
        if (fill == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            _ = FillRect(wParam, ref area, fill);
        }
        finally
        {
            _ = DeleteObject(fill);
        }

        // Ответ 1 — «фон стёрт»: заливка остаётся на экране, пока WPF не нарисует поверх свой кадр.
        handled = true;
        return new IntPtr(1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
