using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Amarin.Computer;
using Amarin.Core;
using Amarin.Tools;
using Drawing = System.Drawing;
using Path = System.Windows.Shapes.Path;

namespace Amarin.UI;

/// <summary>
/// Снимок выделенной области экрана (1.37.0): экран «замирает» снимком, человек тянет рамку мышью,
/// и выделенное уходит вложением в открытый чат.
/// </summary>
/// <remarks>
/// <para>
/// Снимок делается до показа выделения: так в нём то, что было на экране в миг нажатия сочетания,
/// а не тёмная подложка выделения и не подсказки, всплывшие от наведения.
/// </para>
/// <para>
/// На каждом мониторе — своё окно ровно по его границам в физических пикселях (как полосы
/// <see cref="ComputerGlow"/>): одно окно на весь рабочий стол WPF растянул бы по масштабу одного
/// монитора, и на втором картинка легла бы со сдвигом. Выделение — в пределах монитора, где его
/// начали. Esc и правая кнопка отменяют, выделение меньше нескольких точек — тоже.
/// </para>
/// </remarks>
internal static class RegionSnip
{
    /// <summary>Рамка меньше этого — промах щелчком, а не выделение.</summary>
    private const int MinSide = 4;

    private static bool _active;

    /// <summary>Показывает выделение и ждёт его; PNG base64 выделенного или null, если отменили.</summary>
    /// <param name="owner">Окно, чьи кисти темы берёт выделение.</param>
    public static async Task<string?> SelectAsync(Window owner)
    {
        if (_active)
        {
            return null;
        }

        _active = true;
        try
        {
            var (desktop, bounds) = ScreenshotTool.CaptureDesktop();
            using (desktop)
            {
                var source = ToBitmapSource(desktop);
                var done = new TaskCompletionSource<Drawing.Rectangle?>();
                var windows = WindowsComputer.AllMonitors()
                    .Select(monitor => new SnipWindow(owner, source, monitor.Bounds, bounds, done))
                    .ToList();
                foreach (var window in windows)
                {
                    window.Show();
                    window.Place();
                }

                // Клавиатура — окну под курсором: там человек и начнёт выделять, и туда же нажмёт Esc.
                _ = GetCursorPos(out var cursor);
                (windows.FirstOrDefault(window => window.Contains(cursor.X, cursor.Y)) ?? windows.FirstOrDefault())?.Activate();

                var area = await done.Task;
                foreach (var window in windows)
                {
                    window.Close();
                }

                return area is { } chosen ? ScreenshotTool.CropPng(desktop, chosen) : null;
            }
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            PerfLog.Write("region snip failed " + ex.Message);
            return null;
        }
        finally
        {
            _active = false;
        }
    }

    private static BitmapSource ToBitmapSource(Drawing.Bitmap bitmap)
    {
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            _ = DeleteObject(handle);
        }
    }

    /// <summary>
    /// Рамка в окне монитора — в пиксели картинки рабочего стола. Масштаб берётся из размера окна
    /// и монитора, а не из настроек программы: окно стоит по монитору в его собственном масштабе.
    /// </summary>
    internal static Drawing.Rectangle ToPixels(Rect rect, Size window, ScreenRect monitor, Drawing.Rectangle desktop)
    {
        var scaleX = monitor.Width / Math.Max(1, window.Width);
        var scaleY = monitor.Height / Math.Max(1, window.Height);
        var left = (int)Math.Round(rect.Left * scaleX) + monitor.Left - desktop.Left;
        var top = (int)Math.Round(rect.Top * scaleY) + monitor.Top - desktop.Top;
        return new Drawing.Rectangle(left, top, (int)Math.Round(rect.Width * scaleX), (int)Math.Round(rect.Height * scaleY));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    /// <summary>Окно выделения на одном мониторе: его часть снимка, затемнение и рамка.</summary>
    private sealed class SnipWindow : Window
    {
        private readonly ScreenRect _monitor;
        private readonly Drawing.Rectangle _desktop;
        private readonly TaskCompletionSource<Drawing.Rectangle?> _done;
        private readonly Canvas _canvas = new();
        private readonly Path _shade = new();
        private readonly Rectangle _frame = new() { StrokeThickness = 1.5, Visibility = Visibility.Collapsed };
        private readonly Border _size = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Visibility = Visibility.Collapsed };
        private readonly TextBlock _sizeText = new() { FontSize = 11 };
        private Point? _start;

        public SnipWindow(Window owner, BitmapSource desktop, ScreenRect monitor, Drawing.Rectangle desktopBounds, TaskCompletionSource<Drawing.Rectangle?> done)
        {
            _monitor = monitor;
            _desktop = desktopBounds;
            _done = done;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Title = "Amarin";
            Cursor = Cursors.Cross;
            Resources.MergedDictionaries.Add(owner.Resources);

            var piece = new CroppedBitmap(desktop, new Int32Rect(
                monitor.Left - desktopBounds.Left,
                monitor.Top - desktopBounds.Top,
                Math.Min(monitor.Width, desktop.PixelWidth - (monitor.Left - desktopBounds.Left)),
                Math.Min(monitor.Height, desktop.PixelHeight - (monitor.Top - desktopBounds.Top))));
            piece.Freeze();

            _shade.SetResourceReference(Shape.FillProperty, "Overlay.Scrim");
            _frame.SetResourceReference(Shape.StrokeProperty, "Accent.Fill");
            _size.SetResourceReference(Border.BackgroundProperty, "Bg.Elevated");
            _sizeText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Bright");
            _size.Child = _sizeText;

            var hint = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 6, 12, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 24, 0, 0),
                IsHitTestVisible = false,
                Child = new TextBlock { FontSize = 12 }
            };
            hint.SetResourceReference(Border.BackgroundProperty, "Bg.Elevated");
            ((TextBlock)hint.Child).SetResourceReference(TextBlock.ForegroundProperty, "Text.Bright");
            ((TextBlock)hint.Child).SetResourceReference(TextBlock.TextProperty, "S.Snip.Hint");

            _canvas.Children.Add(_shade);
            _canvas.Children.Add(_frame);
            _canvas.Children.Add(_size);
            Content = new Grid
            {
                Children =
                {
                    new Image { Source = piece, Stretch = Stretch.Fill },
                    _canvas,
                    hint
                }
            };

            SizeChanged += (_, _) => Shade(null);
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            MouseRightButtonDown += (_, _) => _done.TrySetResult(null);
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    _done.TrySetResult(null);
                }
            };
            Deactivated += (_, _) =>
            {
                // Человек ушёл в другую программу (Alt+Tab, всплывшее окно) — выделение отменяется,
                // а не висит поверх всего экрана.
                if (!_done.Task.IsCompleted && !Application.Current.Windows.OfType<SnipWindow>().Any(window => window.IsActive))
                {
                    _done.TrySetResult(null);
                }
            };
        }

        public bool Contains(int x, int y) =>
            x >= _monitor.Left && x < _monitor.Left + _monitor.Width && y >= _monitor.Top && y < _monitor.Top + _monitor.Height;

        /// <summary>Ставит окно ровно на монитор, в физических пикселях.</summary>
        public void Place()
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();
            _ = ComputerNative.SetWindowPos(handle, ComputerNative.HwndTopmost, _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height,
                ComputerNative.SwpShowWindow);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(KeepPlace);
        }

        /// <summary>Смена масштаба монитора не должна менять окно: оно стоит по его границам.</summary>
        private IntPtr KeepPlace(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == ComputerNative.WmDpiChanged)
            {
                _ = ComputerNative.SetWindowPos(hwnd, ComputerNative.HwndTopmost, _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height,
                    ComputerNative.SwpNoActivate);
                handled = true;
            }

            return IntPtr.Zero;
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _start = e.GetPosition(_canvas);
            CaptureMouse();
            Shade(new Rect(_start.Value, _start.Value));
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (_start is { } start && IsMouseCaptured)
            {
                Shade(new Rect(start, Clamp(e.GetPosition(_canvas))));
            }
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (_start is not { } start)
            {
                return;
            }

            ReleaseMouseCapture();
            _start = null;
            var area = ToPixels(new Rect(start, Clamp(e.GetPosition(_canvas))));
            _done.TrySetResult(area.Width >= MinSide && area.Height >= MinSide ? area : null);
        }

        private Point Clamp(Point point) =>
            new(Math.Clamp(point.X, 0, ActualWidth), Math.Clamp(point.Y, 0, ActualHeight));

        private Drawing.Rectangle ToPixels(Rect rect) => RegionSnip.ToPixels(rect, new Size(ActualWidth, ActualHeight), _monitor, _desktop);

        /// <summary>Затемнение вокруг выделения, рамка и размер в пикселях.</summary>
        private void Shade(Rect? selection)
        {
            var whole = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
            if (selection is not { } rect || rect.Width < 1 || rect.Height < 1)
            {
                _shade.Data = whole;
                _frame.Visibility = Visibility.Collapsed;
                _size.Visibility = Visibility.Collapsed;
                return;
            }

            _shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, whole, new RectangleGeometry(rect));
            _frame.Visibility = Visibility.Visible;
            _frame.Width = rect.Width;
            _frame.Height = rect.Height;
            Canvas.SetLeft(_frame, rect.Left);
            Canvas.SetTop(_frame, rect.Top);

            var pixels = ToPixels(rect);
            _sizeText.Text = $"{pixels.Width} × {pixels.Height}";
            _size.Visibility = Visibility.Visible;
            Canvas.SetLeft(_size, rect.Left);
            Canvas.SetTop(_size, rect.Top >= 26 ? rect.Top - 24 : rect.Bottom + 4);
        }
    }
}
