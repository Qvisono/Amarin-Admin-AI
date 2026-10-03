using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Карточка «ответ готов» в правом нижнем углу. Отдельное окно поверх всех и без активации:
/// видна, в каком бы состоянии ни было главное окно — без фокуса, свёрнутое или спрятанное.
/// Слой внутри окна, который она заменила, рисовался лишь при окне на экране.
/// </summary>
public partial class NotificationToast : Window
{
    /// <summary>
    /// Сколько карточка висит. Пять секунд в углу большого экрана, пока работаешь в другой
    /// программе, легко пропустить, а её смысл — быть замеченной.
    /// </summary>
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(15);

    private readonly DispatcherTimer _dismiss = new() { Interval = Dwell };
    private bool _closing;
    private bool _suppressCardClick;
    private DateTime _shownAt = DateTime.UtcNow;

    /// <summary>
    /// Сколько карточка на экране. По этому главное окно отличает осознанное «вернулся прочитать»
    /// от активации в тот же миг, что и появление карточки, — иначе она гасла бы через пару кадров.
    /// </summary>
    internal TimeSpan VisibleFor => DateTime.UtcNow - _shownAt;

    /// <summary>Щелчок по карточке — не по крестику.</summary>
    public event Action? CardClicked;

    /// <summary>Рабочая область монитора для карточки, в пикселях устройства; null — основной монитор.</summary>
    private Rect? _workArea;

    public NotificationToast()
    {
        InitializeComponent();
        ApplyFallbackBrushes();
        _dismiss.Tick += (_, _) => Dismiss();
        Loaded += OnLoaded;

        // SizeToContent доводит размер за несколько проходов, а до тех пор карточка стоит за
        // экраном. Ставим её на место на каждом проходе, который мог поменять размер.
        SizeChanged += (_, _) => PositionBottomRight();
        ContentRendered += (_, _) => PositionBottomRight();
    }

    /// <summary>
    /// Палитру держат словари приложения, которые ставит ThemeManager. Собранная раньше него
    /// карточка получила бы null из каждого DynamicResource и на прозрачном окне не нарисовалась
    /// бы вовсе — уведомление молча «не появлялось» бы.
    /// </summary>
    private void ApplyFallbackBrushes()
    {
        if (TryFindResource("Bg.Panel") is not null)
        {
            return;
        }

        PerfLog.Write("toast theme_missing - using built-in dark brushes");
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

        // Владельца у окна нет (подчинённое прячется со свёрнутым владельцем), поэтому из Alt+Tab
        // его убирает стиль «инструмент», а «без активации» не даёт отнять фокус у программы,
        // в которой человек работает.
        var handle = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(handle, GwlExStyle);
        _ = SetWindowLong(handle, GwlExStyle, ex | WsExToolWindow | WsExNoActivate);

        // Примерное место по известному размеру карточки: первый кадр уже в углу, а не в 0,0.
        // Точное место приедет со следующим проходом раскладки.
        var area = WorkAreaInDips();
        Left = area.Right - EstimatedWidth;
        Top = area.Bottom - EstimatedHeight;
    }

    // Карточка 330 в ширину плюс поля по 30 под тень; высота зависит от текста.
    private const double EstimatedWidth = 390;
    private const double EstimatedHeight = 170;

    /// <summary>Собирает, ставит на место и показывает карточку.</summary>
    /// <param name="ownerHandle">
    /// Хэндл главного окна — только чтобы выбрать монитор. Владельцем оно намеренно не ставится:
    /// подчинённое окно прячется со свёрнутым владельцем, а нужна карточка именно тогда.
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

    /// <summary>Гасит и закрывает карточку, если это уже не идёт.</summary>
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

        _shownAt = DateTime.UtcNow;
        _dismiss.Start();
    }

    private void PositionBottomRight()
    {
        // При SizeToContent на первом Loaded ActualWidth ещё 0, а DesiredSize уже известен —
        // берём его, чтобы карточка не осталась за экраном.
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
    /// Рабочая область монитора главного окна в единицах WPF; если её не узнать —
    /// <see cref="SystemParameters.WorkArea"/> основного монитора.
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

    // Пока карточку читают или тянутся к ней, сама она не гаснет.
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
        // Щелчок всплывёт и в Card_MouseLeftButtonUp — там он не должен считаться «открыть».
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
