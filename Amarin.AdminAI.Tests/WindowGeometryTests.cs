using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Память о размере окна. Меряется не на глаз и не по <c>Window.Width</c>, а
/// <c>GetWindowRect</c>: сохранённая величина и есть физические пиксели экрана, и весь смысл
/// правки в том, что они не зависят от масштаба интерфейса.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class WindowGeometryTests
{
    private readonly WpfFixture _wpf;

    public WindowGeometryTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void A_saved_size_comes_back_to_the_pixel()
    {
        var (asked, got) = Measure(new AppSettings
        {
            RememberWindowSize = true,
            WindowPixelWidth = 1000,
            WindowPixelHeight = 700
        });

        // Допуск в пиксель: перевод в единицы окна и обратно округляет с обеих сторон.
        Assert.InRange(got.Width, asked.Width - 2, asked.Width + 2);
        Assert.InRange(got.Height, asked.Height - 2, asked.Height + 2);
    }

    [Fact]
    public void The_same_saved_size_fills_the_same_pixels_at_every_ui_scale()
    {
        // Ради этого размер и хранится в пикселях. Масштаб интерфейса здесь — подделанный DPI,
        // и сохранённый Window.Width означал бы при 150 % окно в полтора раза шире.
        var settings = new AppSettings
        {
            RememberWindowSize = true,
            WindowPixelWidth = 1000,
            WindowPixelHeight = 700
        };

        var (_, hundred) = Measure(settings, scalePercent: 100);
        var (_, hundredFifty) = Measure(settings, scalePercent: 150);

        Assert.InRange(hundredFifty.Width, hundred.Width - 2, hundred.Width + 2);
        Assert.InRange(hundredFifty.Height, hundred.Height - 2, hundred.Height + 2);
    }

    [Fact]
    public void A_size_below_the_minimum_is_raised_to_it()
    {
        // Файл настроек можно и поправить руками, а окно в двести пикселей шириной — это окно,
        // в котором ничего не видно.
        var (_, got) = Measure(new AppSettings
        {
            RememberWindowSize = true,
            WindowPixelWidth = 200,
            WindowPixelHeight = 150
        });

        Assert.True(got.Width >= 800, $"ширина {got.Width}");
        Assert.True(got.Height >= 500, $"высота {got.Height}");
    }

    [Fact]
    public void Without_the_setting_the_window_opens_at_its_usual_size()
    {
        var (_, remembered) = Measure(new AppSettings
        {
            RememberWindowSize = false,
            WindowPixelWidth = 1000,
            WindowPixelHeight = 700
        });

        var (_, plain) = Measure(new AppSettings { RememberWindowSize = false });

        Assert.InRange(remembered.Width, plain.Width - 2, plain.Width + 2);
        Assert.InRange(remembered.Height, plain.Height - 2, plain.Height + 2);
    }

    [Fact]
    public void Closing_the_window_writes_its_size_back()
    {
        var settings = new AppSettings { RememberWindowSize = true };

        _wpf.Ui.Invoke(() =>
        {
            var window = NewWindow();
            try
            {
                window.Width = 1000;
                window.Height = 680;
                window.Show();
                WindowGeometry.Capture(window, settings);
            }
            finally
            {
                window.Close();
            }

            return true;
        });

        Assert.True(settings.WindowPixelWidth > 0, "ширина не сохранилась");
        Assert.True(settings.WindowPixelHeight > 0, "высота не сохранилась");
        Assert.False(settings.WindowMaximized);
    }

    [Fact]
    public void A_maximized_window_opens_maximized_and_is_remembered_so()
    {
        var settings = new AppSettings { RememberWindowSize = true };

        var opened = _wpf.Ui.Invoke(() =>
        {
            var window = NewWindow();
            try
            {
                window.Show();
                window.WindowState = WindowState.Maximized;
                WindowGeometry.Capture(window, settings);
            }
            finally
            {
                window.Close();
            }

            var next = NewWindow();
            try
            {
                WindowGeometry.Restore(next, settings);
                next.Show();
                return next.WindowState;
            }
            finally
            {
                next.Close();
            }
        });

        Assert.True(settings.WindowMaximized, "развёрнутость не сохранилась");
        Assert.Equal(WindowState.Maximized, opened);

        // Размер обычного состояния при этом остаётся: к нему окно и вернётся.
        Assert.True(settings.WindowPixelWidth > 0);
    }

    /// <summary>
    /// Поднимает окно тем же порядком, что и запуск программы: хэндл, масштаб, восстановление,
    /// показ. Возвращает, что просили, и что вышло на экране.
    /// </summary>
    private ((int Width, int Height) Asked, (int Width, int Height) Got) Measure(
        AppSettings settings,
        int scalePercent = 100)
    {
        var got = _wpf.Ui.Invoke(() =>
        {
            var window = NewWindow();
            var root = (FrameworkElement)window.FindName("ScaledRoot");
            try
            {
                UiScale.Apply(window, root, scalePercent);
                WindowGeometry.Restore(window, settings);
                window.Show();

                var handle = new WindowInteropHelper(window).Handle;
                GetWindowRect(handle, out var rect);
                return (rect.right - rect.left, rect.bottom - rect.top);
            }
            finally
            {
                // Масштаб общий на программу: оставленные 150 % развалили бы соседний тест.
                UiScale.Apply(window, root, 100);
                window.Close();
            }
        });

        return ((settings.WindowPixelWidth, settings.WindowPixelHeight), got);
    }

    private static MainWindow NewWindow()
    {
        var window = new MainWindow();

        // Тот же порядок, что в Program: хэндл рождается до Show, и без него ни снять
        // геометрию, ни вернуть её нельзя.
        new WindowInteropHelper(window).EnsureHandle();
        return window;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
