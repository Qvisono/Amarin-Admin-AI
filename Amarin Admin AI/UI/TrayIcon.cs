using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Amarin.UI;

/// <summary>Что показывает точка на значке в трее.</summary>
internal enum TrayState
{
    Idle,
    Busy,
    Waiting,
    Error
}

/// <summary>
/// Значок в области уведомлений (G1) на <c>Shell_NotifyIcon</c> — без WinForms.
/// </summary>
/// <remarks>
/// <para>
/// WinForms дал бы <c>NotifyIcon</c> готовым, но <c>UseWindowsForms</c> возвращает в проект
/// неявные using (а с ними спор <c>System.Drawing</c> и <c>System.Windows.Media</c> за имена) и
/// лишние мегабайты в exe. Здесь только то, что нужно: значок, подсказка, щелчки и всплывающее
/// уведомление.
/// </para>
/// <para>
/// Сообщения значка приходят в окно программы через его <see cref="HwndSource"/> — окно живёт
/// всё время работы, в том числе спрятанным. Проводник, перезапустившись, забывает все значки и
/// рассылает <c>TaskbarCreated</c>: по нему значок ставится заново, иначе он пропадал бы до
/// перезапуска программы.
/// </para>
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 0x4A; // WM_APP + 74
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const int NIIF_INFO = 0x1, NIIF_WARNING = 0x2, NIIF_NOSOUND = 0x10;
    private const int NOTIFYICON_VERSION_4 = 4;
    private const int WM_CONTEXTMENU = 0x007B, WM_LBUTTONDBLCLK = 0x0203;
    private const int NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, NIN_BALLOONUSERCLICK = 0x0405;
    private const int SM_CXSMICON = 49;

    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly ImageSource _appIcon;
    private IntPtr _icon;
    private TrayState _state = TrayState.Idle;
    private string _tip = "";
    private bool _added;
    private Action? _onBalloonClick;

    /// <summary>Левый щелчок (или Enter с клавиатуры) — показать или спрятать окно.</summary>
    public event Action? Activated;

    /// <summary>Правый щелчок — меню. Точка — где показать, в пикселях экрана.</summary>
    public event Action<Point>? MenuRequested;

    public TrayIcon(Window owner, ImageSource appIcon)
    {
        _hwnd = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd) ?? throw new InvalidOperationException("no window source");
        _appIcon = appIcon;
        _source.AddHook(Hook);
    }

    public bool IsShown => _added;

    public void Show(string tip)
    {
        _tip = tip;
        _added = Add();
    }

    public void SetState(TrayState state, string tip)
    {
        if (state == _state && tip == _tip)
        {
            return;
        }

        _state = state;
        _tip = tip;
        if (_added)
        {
            var data = Data(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
            data.hIcon = RefreshIcon();
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
    }

    /// <summary>
    /// Всплывающее уведомление значка. Windows 10 и 11 показывают его системным уведомлением.
    /// </summary>
    /// <param name="onClick">Что сделать по щелчку на уведомлении — обычно открыть окно.</param>
    /// <param name="silent">Без системного звука: своя мелодия уже прозвучала.</param>
    public bool Balloon(string title, string text, bool warning, Action? onClick, bool silent = false)
    {
        if (!_added)
        {
            return false;
        }

        _onBalloonClick = onClick;
        var data = Data(NIF_INFO);
        data.szInfoTitle = Trim(title, 63);
        data.szInfo = Trim(text, 255);
        data.dwInfoFlags = (warning ? NIIF_WARNING : NIIF_INFO) | (silent ? NIIF_NOSOUND : 0);
        return Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        _source.RemoveHook(Hook);
        if (_added)
        {
            var data = Data(0);
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    private bool Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = RefreshIcon();
        if (!Shell_NotifyIconW(NIM_ADD, ref data))
        {
            return false;
        }

        // Версия 4: щелчки приходят как NIN_SELECT и WM_CONTEXTMENU, координаты — в wParam.
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref data);
        return true;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)TaskbarCreated && TaskbarCreated != 0 && _added)
        {
            _added = Add();
            return IntPtr.Zero;
        }

        if (msg != CallbackMessage)
        {
            return IntPtr.Zero;
        }

        var kind = (int)((long)lParam & 0xFFFF);
        switch (kind)
        {
            case NIN_SELECT:
            case NIN_KEYSELECT:
            case WM_LBUTTONDBLCLK:
                handled = true;
                Activated?.Invoke();
                break;
            case WM_CONTEXTMENU:
                handled = true;
                var packed = (long)wParam;
                MenuRequested?.Invoke(new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF)));
                break;
            case NIN_BALLOONUSERCLICK:
                handled = true;
                var click = _onBalloonClick;
                _onBalloonClick = null;
                click?.Invoke();
                break;
        }

        return IntPtr.Zero;
    }

    private NOTIFYICONDATAW Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        szTip = Trim(_tip, 127),
        szInfo = "",
        szInfoTitle = ""
    };

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    /// <summary>Значок программы с точкой состояния в углу. Прежний HICON освобождается.</summary>
    private IntPtr RefreshIcon()
    {
        var next = BuildIcon(_appIcon, _state, GetSystemMetrics(SM_CXSMICON));
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }

        _icon = next;
        return next;
    }

    /// <summary>
    /// Рисует значок средствами WPF и переводит в HICON. Цвет точки — из ресурсов темы, как и
    /// всё в программе: иначе точка не узнавалась бы рядом с остальным интерфейсом.
    /// </summary>
    internal static IntPtr BuildIcon(ImageSource appIcon, TrayState state, int size)
    {
        size = Math.Clamp(size, 16, 64);
        var bitmap = Render(appIcon, state, size);
        var pixels = new byte[size * size * 4];
        new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, size * 4, 0);

        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size,
            biPlanes = 1,
            biBitCount = 32
        };
        var color = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var mask = CreateBitmap(size, size, 1, 1, new byte[((size + 15) / 16) * 2 * size]);
        var info = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    internal static BitmapSource Render(ImageSource appIcon, TrayState state, int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(appIcon, new Rect(0, 0, size, size));
            var key = state switch
            {
                TrayState.Busy => "Accent.Fill",
                TrayState.Waiting => "Status.Warning",
                TrayState.Error => "Status.Danger",
                _ => null
            };

            if (key is not null && Application.Current?.TryFindResource(key) is SolidColorBrush brush)
            {
                var radius = size * 0.2;
                var center = new Point(size - radius - 0.5, size - radius - 0.5);
                context.DrawEllipse(Brushes.White, null, center, radius + 1, radius + 1);
                context.DrawEllipse(brush, null, center, radius, radius);
            }
        }

        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return target;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;

        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[] bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
