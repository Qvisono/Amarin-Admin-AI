using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Запуск до первого кадра. <c>Show()</c> выводит окно на экран сразу, а рисует его WPF только
/// после <c>Loaded</c> и раскладки — до 1.30.0 человек всё это время смотрел на белый
/// прямоугольник (под отладчиком — секунды), и дольше всего его держала раскладка длинного списка
/// чатов, которого на первом кадре почти не видно.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class StartupFirstFrameTests
{
    private const int WmEraseBkgnd = 0x0014;
    private const uint ClrInvalid = 0xFFFFFFFF;

    private readonly WpfFixture _wpf;

    public StartupFirstFrameTests(WpfFixture wpf) => _wpf = wpf;

    /// <summary>
    /// До первого кадра окно стирает фон цветом фона темы, а после — не трогает: иначе заливка
    /// закрашивала бы уже нарисованное.
    /// </summary>
    [Fact]
    public async Task Until_the_first_frame_the_window_erases_to_the_theme_background()
    {
        var (theme, before, after) = await _wpf.Ui.Invoke(async () =>
        {
            var window = new MainWindow
            {
                Left = -32000,
                Top = 0,
                Width = 900,
                Height = 600,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            try
            {
                var handle = new WindowInteropHelper(window).EnsureHandle();
                var expected = ((SolidColorBrush)window.FindResource("Bg.Window")).Color;
                var early = EraseInto(handle);

                var rendered = new TaskCompletionSource();
                window.ContentRendered += (_, _) => rendered.TrySetResult();
                window.Show();
                await rendered.Task;
                return (ColorRef(expected), early, EraseInto(handle));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(theme.ToString("X6", CultureInfo.InvariantCulture), before.ToString("X6", CultureInfo.InvariantCulture));
        Assert.Equal("FFFFFF", after.ToString("X6", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// То, что видит человек: окно показано, поток окна занят (как обработчиками <c>Loaded</c> на
    /// запуске), кадра ещё нет — а на экране на месте окна фон темы, не белое. Читается сам экран.
    /// </summary>
    /// <remarks>
    /// Окно стоит поверх всех две секунды. При погашенном мониторе DWM составляет кадр раз в
    /// секунду, поэтому сверяются только последние 400 мс: их кадр составлен не раньше 600 мс после
    /// показа, когда анимация появления окна уже закончилась. Заблокированный экран не читается
    /// вовсе (GetPixel отдаёт CLR_INVALID) — тогда проверять нечего.
    /// </remarks>
    [Fact]
    public async Task A_busy_window_before_its_first_frame_shows_the_theme_background_not_white()
    {
        const int BusyMs = 2000;
        var (theme, samples) = await _wpf.Ui.Invoke(async () =>
        {
            var window = new MainWindow
            {
                Left = 120,
                Top = 120,
                Width = 700,
                Height = 500,
                Topmost = true,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            try
            {
                var handle = new WindowInteropHelper(window).EnsureHandle();
                _ = GetWindowRect(handle, out var bounds);
                var x = (bounds.Left + bounds.Right) / 2;
                var y = (bounds.Top + bounds.Bottom) / 2;
                var color = ((SolidColorBrush)window.FindResource("Bg.Window")).Color;

                var watch = Stopwatch.StartNew();
                var busyEnd = 0L;
                window.Loaded += (_, _) =>
                {
                    Thread.Sleep(BusyMs);
                    Interlocked.Exchange(ref busyEnd, watch.ElapsedMilliseconds);
                };

                var taken = new List<(long At, uint Pixel)>();
                using var stop = new CancellationTokenSource();
                var sampler = Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var screen = GetDC(IntPtr.Zero);
                        var pixel = GetPixel(screen, x, y);
                        _ = ReleaseDC(IntPtr.Zero, screen);
                        lock (taken)
                        {
                            taken.Add((watch.ElapsedMilliseconds, pixel));
                        }

                        Thread.Sleep(40);
                    }
                });

                var rendered = new TaskCompletionSource();
                window.ContentRendered += (_, _) => rendered.TrySetResult();
                window.Show();
                await rendered.Task;
                await stop.CancelAsync();
                await sampler;

                var end = Interlocked.Read(ref busyEnd);
                List<uint> late;
                lock (taken)
                {
                    late = taken.Where(sample => sample.At >= end - 400 && sample.At < end).Select(sample => sample.Pixel).ToList();
                }

                return (ColorRef(color), late);
            }
            finally
            {
                window.Close();
            }
        });

        // Экран заперт или фон темы сам белый (контрастная тема) — отличить нечего.
        if (samples.Count == 0 || samples.All(pixel => pixel == ClrInvalid) || theme == 0xFFFFFF)
        {
            return;
        }

        Assert.All(samples, pixel => Assert.True(
            Close(pixel, theme),
            $"на месте окна 0x{pixel:X6}, а фон темы 0x{theme:X6}"));
    }

    private static bool Close(uint pixel, uint expected)
    {
        for (var shift = 0; shift < 24; shift += 8)
        {
            if (Math.Abs((int)((pixel >> shift) & 0xFF) - (int)((expected >> shift) & 0xFF)) > 3)
            {
                return false;
            }
        }

        return true;
    }

    private static uint ColorRef(Color color) => (uint)(color.R | (color.G << 8) | (color.B << 16));

    /// <summary>
    /// Шлёт окну <c>WM_ERASEBKGND</c> с битмапом в памяти, заранее залитым белым, и читает его.
    /// </summary>
    private static uint EraseInto(IntPtr window)
    {
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, 8, 8);
        _ = ReleaseDC(IntPtr.Zero, screen);
        var previous = SelectObject(dc, bitmap);
        try
        {
            var all = new Rect { Left = 0, Top = 0, Right = 8, Bottom = 8 };
            _ = FillRect(dc, ref all, GetStockObject(0));
            _ = SendMessage(window, WmEraseBkgnd, dc, IntPtr.Zero);
            return GetPixel(dc, 4, 4);
        }
        finally
        {
            _ = SelectObject(dc, previous);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr handle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);
}
