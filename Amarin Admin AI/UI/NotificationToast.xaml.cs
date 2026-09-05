using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Amarin.Core;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Amarin.UI;

/// <summary>
/// Bottom-right "response is ready" toast. A standalone top-most, non-activating window so it
/// shows regardless of the main window's state — unfocused, minimized or hidden. The in-window
/// overlay it replaced could only paint while the main window was actually on screen.
/// </summary>
public partial class NotificationToast : Window
{
    /// <summary>
    /// How long the card stays up. Five seconds was easy to miss in the corner of a large
    /// screen while working in another app — the whole point is to be noticed.
    /// </summary>
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(15);

    private readonly DispatcherTimer _dismiss = new() { Interval = Dwell };
    private bool _closing;
    private bool _suppressCardClick;

    /// <summary>Raised when the card body (not the close button) is clicked.</summary>
    public event Action? CardClicked;

    /// <summary>Monitor the toast should sit on, in device pixels; null means the primary one.</summary>
    private Rect? _workArea;

    public NotificationToast()
    {
        InitializeComponent();
        ApplyFallbackBrushes();
        _dismiss.Tick += (_, _) => Dismiss();
        Loaded += OnLoaded;

        // SizeToContent resolves the window size across several passes, and the card is
        // parked off-screen until then. Re-place it on every pass that can change the size.
        SizeChanged += (_, _) => PositionBottomRight();
        ContentRendered += (_, _) => PositionBottomRight();
    }

    /// <summary>
    /// The palette lives in three Application-level dictionaries swapped by ThemeManager. If a
    /// toast is ever built before that ran, every DynamicResource resolves to null and the card
    /// paints as nothing on a transparent window — i.e. the toast silently "does not appear".
    /// </summary>
    private void ApplyFallbackBrushes()
    {
        if (TryFindResource("Bg.Panel") is not null)
        {
            return;
        }

        PerfLog.Write("toast theme_missing — using built-in dark brushes");
        Card.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        Card.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        Preview.Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
        Meta.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
        LogoLetter.Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
        Title2.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Not owned by the main window (owned windows hide when the owner is minimised),
        // so mark it as a tool window to keep it out of Alt-Tab, and never-activate so it
        // can't steal focus from the app the user is in.
        var handle = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, ex | WsExToolWindow | WsExNoActivate);

        // Rough placement from the known card size, so the very first frame is already in the
        // right corner instead of at 0,0. The exact position lands on the next layout pass.
        var area = WorkAreaInDips();
        Left = area.Right - EstimatedWidth;
        Top = area.Bottom - EstimatedHeight;
    }

    // Card is 330 wide inside a 30px shadow margin; height varies with the preview text.
    private const double EstimatedWidth = 390;
    private const double EstimatedHeight = 170;

    /// <summary>Builds, positions and shows the toast.</summary>
    /// <param name="ownerHandle">
    /// Main window handle, used only to pick the monitor. The toast is deliberately not owned —
    /// an owned window hides whenever its owner is minimised, which is exactly when it is needed.
    /// </param>
    public static NotificationToast Show(
        string modelId,
        string previewLine,
        string metaLine,
        int uiScalePercent,
        IntPtr ownerHandle,
        Action? onActivated)
    {
        var toast = new NotificationToast();

        var factor = Math.Clamp(uiScalePercent, 50, 300) / 100.0;
        if (Math.Abs(factor - 1.0) > 0.001 && toast.Content is FrameworkElement root)
        {
            root.LayoutTransform = new ScaleTransform(factor, factor);
        }

        ModelBrand.Apply(toast, modelId, toast.Logo, toast.LogoLetter, null);
        toast.Preview.Text = previewLine;
        toast.Meta.Text = metaLine;
        toast._workArea = WorkAreaOf(ownerHandle);
        if (onActivated is not null)
        {
            toast.CardClicked += onActivated;
        }

        toast.Show();
        PerfLog.Write($"toast shown model={modelId} scale={uiScalePercent}");
        return toast;
    }

    /// <summary>Fade the toast out and close it, unless that is already under way.</summary>
    public void Dismiss()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _dismiss.Stop();

        if (Content is not FrameworkElement root ||
            root.Resources["ToastOut"] is not Storyboard storyboard)
        {
            Close();
            return;
        }

        void OnCompleted(object? sender, EventArgs e)
        {
            storyboard.Completed -= OnCompleted;
            Close();
        }

        storyboard.Completed += OnCompleted;
        storyboard.Begin(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionBottomRight();
        if (Content is FrameworkElement root && root.Resources["ToastIn"] is Storyboard storyboard)
        {
            storyboard.Begin(this);
        }

        _dismiss.Start();
    }

    private void PositionBottomRight()
    {
        // ActualWidth is still 0 on the first Loaded pass under SizeToContent; DesiredSize is
        // already filled by then, so fall back to it rather than leaving the card off-screen.
        var width = ActualWidth >= 1 ? ActualWidth : DesiredSize.Width;
        var height = ActualHeight >= 1 ? ActualHeight : DesiredSize.Height;
        if (width < 1 || height < 1)
        {
            return;
        }

        var area = WorkAreaInDips();
        Left = area.Right - width;
        Top = area.Bottom - height;
    }

    /// <summary>
    /// Work area of the main window's monitor, converted to WPF units. Falls back to
    /// <see cref="SystemParameters.WorkArea"/> (primary monitor) when anything is unavailable.
    /// </summary>
    private Rect WorkAreaInDips()
    {
        if (_workArea is not { } device)
        {
            return SystemParameters.WorkArea;
        }

        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        if (transform is null)
        {
            return SystemParameters.WorkArea;
        }

        var topLeft = transform.Value.Transform(new Point(device.Left, device.Top));
        var bottomRight = transform.Value.Transform(new Point(device.Right, device.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private static Rect? WorkAreaOf(IntPtr ownerHandle)
    {
        if (ownerHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var monitor = MonitorFromWindow(ownerHandle, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return null;
            }

            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return null;
            }

            return new Rect(
                info.rcWork.left,
                info.rcWork.top,
                info.rcWork.right - info.rcWork.left,
                info.rcWork.bottom - info.rcWork.top);
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    // Auto-dismiss must not fire while the user is reading or reaching for the toast.
    private void Card_MouseEnter(object sender, MouseEventArgs e) => _dismiss.Stop();

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_closing)
        {
            _dismiss.Start();
        }
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_suppressCardClick)
        {
            _suppressCardClick = false;
            return;
        }

        CardClicked?.Invoke();
        Dismiss();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // The click also bubbles to Card_MouseLeftButtonUp; keep it from counting as "open".
        _suppressCardClick = true;
        Dismiss();
    }

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }
}
