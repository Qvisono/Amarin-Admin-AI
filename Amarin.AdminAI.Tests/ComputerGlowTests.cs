using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Свечение по краям и курсор Amarin. Свечение ставится на подставной монитор за краем экрана, а
/// системный курсор тесты не трогают никогда: подмена Windows подставляется.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ComputerGlowTests
{
    private static readonly ComputerMonitor OffScreen = new(1, new ScreenRect(-32000, 0, 800, 600), Primary: true);

    private readonly WpfFixture _wpf;

    public ComputerGlowTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void The_glow_lines_every_edge_of_the_monitor_and_stays_out_of_screenshots_and_clicks()
    {
        _wpf.Ui.Invoke<object?>(() =>
        {
            var glow = new ComputerGlow();
            try
            {
                glow.Show([OffScreen]);

                var rects = glow.Strips.Select(strip => Rect(Handle(strip))).ToList();
                Assert.Equal(
                [
                    new ScreenRect(-32000, 0, 800, 28),
                    new ScreenRect(-32000, 572, 800, 28),
                    new ScreenRect(-32000, 0, 28, 600),
                    new ScreenRect(-31228, 0, 28, 600)
                ], rects);

                foreach (var strip in glow.Strips)
                {
                    var handle = Handle(strip);
                    Assert.NotEqual(0, GetWindowLong(handle, -20) & 0x20);
                    _ = Amarin.Computer.ComputerNative.GetWindowDisplayAffinity(handle, out var affinity);
                    Assert.Equal(0x11u, affinity);
                }
            }
            finally
            {
                glow.Hide();
            }

            Assert.Empty(glow.Strips);
            return null;
        });
    }

    [Fact]
    public void A_click_at_the_edge_reaches_the_window_under_the_glow()
    {
        // Иначе нажатие в панель задач досталось бы полосе, и правило «окна самой программы не
        // трогать» отказало бы агенту у любого края экрана.
        _wpf.Ui.Invoke<object?>(() =>
        {
            var under = new Window
            {
                Left = -32000,
                Top = 0,
                Width = 800,
                Height = 600,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            under.Show();
            var glow = new ComputerGlow();
            try
            {
                glow.Show([OffScreen]);
                var hit = WindowFromPoint(new NativePoint { X = -31990, Y = 10 });

                Assert.Equal(new WindowInteropHelper(under).Handle, GetAncestor(hit, 2));
            }
            finally
            {
                glow.Hide();
                under.Close();
            }

            return null;
        });
    }

    [Fact]
    public void The_cursor_is_a_soft_pink_tip_with_its_hotspot_on_the_tip()
    {
        _wpf.Ui.Invoke<object?>(() =>
        {
            var image = ComputerCursor.Render(32);
            var pixels = new byte[32 * 32 * 4];
            new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 32 * 4, 0);

            var hotspot = ComputerCursor.Hotspot(32);
            var (b, g, r, a) = Pixel(hotspot + 2, hotspot + 2);

            Assert.Equal(32, image.PixelWidth);
            Assert.InRange(hotspot, 2, 5);
            Assert.True(a > 200, $"кончик прозрачен: {a}");
            Assert.True(r > 200 && r > g + 60 && r > b + 30, $"кончик не красно-розовый: {r},{g},{b}");
            Assert.Equal(0, Pixel(31, 31).A);
            Assert.Equal(0, Pixel(0, 31).A);
            return null;

            (byte B, byte G, byte R, byte A) Pixel(int x, int y)
            {
                var i = (y * 32 + x) * 4;
                return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
            }
        });
    }

    [Fact]
    public void The_cursor_comes_back_at_the_end_and_after_a_crash_on_the_next_start()
    {
        var marker = Path.Combine(Path.GetTempPath(), "amarin-cursor-" + Guid.NewGuid().ToString("N"));
        var system = new FakeCursors();
        try
        {
            var cursor = new ComputerCursor(marker, system);
            _wpf.Ui.Invoke<object?>(() =>
            {
                cursor.Apply();
                cursor.Apply();
                return null;
            });

            Assert.Equal(1, system.Replaced);
            Assert.True(File.Exists(marker), "метки подмены нет: после падения курсор не вернётся");

            cursor.Restore();
            cursor.Restore();

            Assert.Equal(1, system.Resets);
            Assert.False(File.Exists(marker));

            // Процесс упал, не вернув курсор: метка осталась, и следующий запуск возвращает его.
            File.WriteAllText(marker, "");
            ComputerCursor.RestoreLeftover(marker, system);
            ComputerCursor.RestoreLeftover(marker, system);

            Assert.Equal(2, system.Resets);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    private sealed class FakeCursors : ICursorSystem
    {
        public int Replaced { get; private set; }

        public int Resets { get; private set; }

        public void Replace(BitmapSource image, int hotspotX, int hotspotY) => Replaced++;

        public void Reset() => Resets++;
    }

    private static IntPtr Handle(Window window) => new WindowInteropHelper(window).Handle;

    private static ScreenRect Rect(IntPtr handle)
    {
        _ = GetWindowRect(handle, out var rect);
        return new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
}
