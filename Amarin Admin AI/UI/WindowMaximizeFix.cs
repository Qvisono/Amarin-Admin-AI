using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Amarin.UI;

/// <summary>
/// Разворот окна строго по рабочей области монитора.
/// </summary>
/// <remarks>
/// <para>
/// Окно с собственным оформлением (<see cref="System.Windows.Shell.WindowChrome"/>) при
/// разворачивании получает от Windows размер монитора плюс невидимую рамку изменения размера —
/// у обычного окна она обрезается системным фреймом, а здесь на её месте наш собственный фон,
/// и он уезжает за края экрана и накрывает панель задач. Отвечаем на <c>WM_GETMINMAXINFO</c>
/// сами: позиция и размер развёрнутого окна — это <c>rcWork</c> текущего монитора, то есть
/// экран за вычетом панели задач, где бы она ни стояла.
/// </para>
/// <para>
/// Одного этого ответа мало: <c>WM_GETMINMAXINFO</c> спрашивают при переходе в развёрнутое
/// состояние, а размер окну назначают и после — при смене монитора, масштаба экрана или из
/// нашего же пересчёта масштаба интерфейса. Поэтому каждое изменение положения развёрнутого
/// окна дополнительно правится в <c>WM_WINDOWPOSCHANGING</c>.
/// </para>
/// <para>
/// Тем же ответом задаётся и минимальный размер. Отвечая на <c>WM_GETMINMAXINFO</c>, мы ставим
/// <c>handled</c> и тем самым отключаем штатную обработку WPF — а она как раз и переносила
/// <see cref="FrameworkElement.MinWidth"/> в <c>ptMinTrackSize</c>. Пока эти два поля не
/// заполнялись здесь, минимума у окна не было вовсе: система разрешала сжать его до
/// собственного минимума (порядка 130×40), и разметка обрезалась.
/// </para>
/// </remarks>
internal static class WindowMaximizeFix
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_NCCALCSIZE = 0x0083;
    private const int WVR_REDRAW = 0x0300;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOMOVE = 0x0002;

    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // Отступ содержимого — на каждое событие, после которого развёрнутое окно могло лечь
        // иначе: сам разворот, сдвиг на другой монитор, смена размера и масштаба.
        window.StateChanged += (_, _) => UpdateContentInset(window);
        window.SizeChanged += (_, _) => UpdateContentInset(window);
        window.LocationChanged += (_, _) => UpdateContentInset(window);
        window.DpiChanged += (_, _) => UpdateContentInset(window);

        // Окно, показанное сразу развёрнутым, смены состояния уже не увидит.
        window.Loaded += (_, _) => UpdateContentInset(window);

        if (TryHook(window))
        {
            return;
        }

        window.SourceInitialized += (_, _) => TryHook(window);
    }

    /// <summary>
    /// Отодвигает содержимое развёрнутого окна от краёв, за которые его вынесла Windows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Подгонка прямоугольника в <c>WM_GETMINMAXINFO</c> и <c>WM_WINDOWPOSCHANGING</c> хватала,
    /// пока окно числилось «инструментом». Став в 1.27.0 обычным окном (<c>WS_CAPTION</c>), оно
    /// при развороте всё равно получало невидимую рамку за краями экрана: содержимое уезжало
    /// на её ширину влево, вверх и под панель задач. Что именно решает Windows на каждом
    /// мониторе и масштабе, заранее не угадать, поэтому здесь не предполагается ничего: клиентская
    /// область меряется по факту и сравнивается с рабочей областью монитора, а разница уходит
    /// в поле содержимого. Легло окно ровно — поле нулевое.
    /// </para>
    /// <para>
    /// Поле ставится корневому элементу, а не окну: у окна с <c>WindowChrome</c> свои поля
    /// разметки не двигают саму клиентскую область. Вынесенная полоса при этом лежит за краем
    /// экрана или под панелью задач — видно её быть не может.
    /// </para>
    /// </remarks>
    internal static void UpdateContentInset(Window window)
    {
        if (window.Content is not FrameworkElement root)
        {
            return;
        }

        var inset = new Thickness(0);
        var handle = new WindowInteropHelper(window).Handle;
        if (window.WindowState == WindowState.Maximized &&
            handle != IntPtr.Zero &&
            TryGetClientOnScreen(handle, out var client) &&
            TryGetWorkArea(handle, out var work, out _) &&
            PresentationSource.FromVisual(window)?.CompositionTarget is { } target)
        {
            var device = Overhang(client, work);
            var toDip = target.TransformFromDevice;
            inset = new Thickness(
                device.Left * toDip.M11,
                device.Top * toDip.M22,
                device.Right * toDip.M11,
                device.Bottom * toDip.M22);
        }

        if (root.Margin != inset)
        {
            root.Margin = inset;
        }

        if (Core.PerfLog.IsEnabled && handle != IntPtr.Zero)
        {
            LogGeometry(window, handle, inset);
        }
    }

    /// <summary>
    /// Геометрия окна в журнал <c>AMARIN_PERF_LOG</c>.
    /// </summary>
    /// <remarks>
    /// Развёрнутое окно после смены стиля в 1.27.0 уезжало за края экрана у людей, а на машине
    /// разработки и в тестах сходилось. Угадывать, что делает Windows на чужом мониторе, третий
    /// раз незачем: строка называет все четыре прямоугольника разом — окно, клиентскую область,
    /// видимые границы по DWM и рабочую область — вместе с настоящим и подделанным масштабом.
    /// </remarks>
    private static void LogGeometry(Window window, IntPtr handle, Thickness inset)
    {
        GetWindowRect(handle, out var outer);
        TryGetClientOnScreen(handle, out var client);
        TryGetWorkArea(handle, out var work, out var monitor);
        var visible = DwmGetWindowAttribute(handle, DwmwaExtendedFrameBounds, out RECT frame, Marshal.SizeOf<RECT>()) == 0
            ? $"{frame.left},{frame.top},{frame.right},{frame.bottom}"
            : "n/a";
        var scale = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice.M11 ?? double.NaN;
        uint realDpi = 0;
        try
        {
            realDpi = GetDpiForWindow(handle);
        }
        catch (EntryPointNotFoundException)
        {
        }

        Core.PerfLog.Write(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"window_geometry state={window.WindowState} window={outer.left},{outer.top},{outer.right},{outer.bottom} " +
            $"client={client.Left},{client.Top},{client.Right},{client.Bottom} visible={visible} " +
            $"work={work.Left},{work.Top},{work.Right},{work.Bottom} monitor={monitor.Left},{monitor.Top},{monitor.Right},{monitor.Bottom} " +
            $"wpf_scale={scale:0.###} real_dpi={realDpi} inset={inset.Left:0.#},{inset.Top:0.#},{inset.Right:0.#},{inset.Bottom:0.#}"));
    }

    /// <summary>
    /// На сколько аппаратных пикселей клиентская область вылезает за рабочую область с каждой
    /// стороны. Внутрь не бывает: окно меньше рабочей области отступа не просит.
    /// </summary>
    internal static Thickness Overhang(Rect client, Rect work) => new(
        Math.Max(0, work.Left - client.Left),
        Math.Max(0, work.Top - client.Top),
        Math.Max(0, client.Right - work.Right),
        Math.Max(0, client.Bottom - work.Bottom));

    private static bool TryGetClientOnScreen(IntPtr handle, out Rect client)
    {
        client = default;
        if (!GetClientRect(handle, out var local))
        {
            return false;
        }

        var origin = new POINT { x = 0, y = 0 };
        if (!ClientToScreen(handle, ref origin))
        {
            return false;
        }

        client = new Rect(
            origin.x,
            origin.y,
            Math.Max(0, local.right - local.left),
            Math.Max(0, local.bottom - local.top));
        return true;
    }

    /// <summary>
    /// Ставит перехватчик, если окно уже создано.
    /// </summary>
    /// <remarks>
    /// Источник ищем по хэндлу, а не через <c>PresentationSource.FromVisual</c>. Окно этого
    /// приложения создаётся раньше показа: <c>UiScale</c> зовёт <c>EnsureHandle</c> ещё из
    /// <c>AttachServices</c>. В этот момент <c>SourceInitialized</c> уже произошло, а дерево
    /// визуалов к источнику ещё не привязано — и поиск по визуалу возвращает пустоту. Раньше на
    /// этом перехватчик молча терялся, и развёрнутое окно получало от Windows монитор плюс
    /// невидимую рамку: −8 сверху и слева, +8 справа и снизу, из-за чего оно и налезало на
    /// панель задач.
    /// </remarks>
    private static bool TryHook(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || HwndSource.FromHwnd(handle) is not { } source)
        {
            return false;
        }

        source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            Hook(window, source, hwnd, msg, wParam, lParam, ref handled));
        return true;
    }

    /// <summary>Рабочая область монитора, на котором сейчас окно, в аппаратных пикселях.</summary>
    internal static bool TryGetWorkArea(IntPtr handle, out Rect work, out Rect monitor)
    {
        work = default;
        monitor = default;

        var screen = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
        if (screen == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(screen, ref info))
        {
            return false;
        }

        work = new Rect(
            info.rcWork.left,
            info.rcWork.top,
            Math.Max(1, info.rcWork.right - info.rcWork.left),
            Math.Max(1, info.rcWork.bottom - info.rcWork.top));
        monitor = new Rect(
            info.rcMonitor.left,
            info.rcMonitor.top,
            Math.Max(1, info.rcMonitor.right - info.rcMonitor.left),
            Math.Max(1, info.rcMonitor.bottom - info.rcMonitor.top));
        return true;
    }

    private static IntPtr Hook(
        Window window,
        HwndSource source,
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        switch (msg)
        {
            case WM_GETMINMAXINFO:
                return OnGetMinMaxInfo(window, source, hwnd, lParam, ref handled);

            case WM_WINDOWPOSCHANGING:
                ClampMaximized(hwnd, lParam);
                return IntPtr.Zero;

            case WM_NCCALCSIZE:
                return OnNcCalcSize(hwnd, wParam, lParam, ref handled);

            default:
                return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Клиентская область развёрнутого окна — не больше рабочей области монитора, где бы
    /// Windows ни поставила само окно.
    /// </summary>
    /// <remarks>
    /// <para>
    /// До 1.27.0 окно было «инструментом» (<c>WS_EX_TOOLWINDOW</c>), и подгонки прямоугольника в
    /// <c>WM_GETMINMAXINFO</c>/<c>WM_WINDOWPOSCHANGING</c> хватало. Обычное окно Windows
    /// разворачивает по другим правилам — даже координаты <c>WINDOWPLACEMENT</c> у него
    /// считаются от рабочей области, а не от экрана (об этом прямо пишет исходник WPF
    /// <c>Window.GetNormalRectDeviceUnits</c>), — и содержимое снова уехало за края экрана и под
    /// панель задач. Здесь окну не навязывается ничего: пусть лежит, как решила система, а
    /// клиентская область, где рисует WPF, обрезается по рабочей области. Вынесенная полоса
    /// остаётся неклиентской и лежит за краем экрана или под панелью задач.
    /// </para>
    /// <para>
    /// Этот перехватчик ставится позже, чем у <see cref="System.Windows.Shell.WindowChrome"/>, а
    /// <see cref="HwndSource"/> зовёт последний добавленный первым, поэтому ответ наш. Обычного
    /// окна он не касается: там сообщение уходит WindowChrome, и клиентская область, как и
    /// прежде, равна окну целиком.
    /// </para>
    /// </remarks>
    private static IntPtr OnNcCalcSize(IntPtr hwnd, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Разворот ставит WS_MAXIMIZE до того, как назначить прямоугольник, а возврат снимает
        // его до того, как вернуть обычный, — поэтому сообщение о новом размере судит по флагу.
        if (lParam == IntPtr.Zero || !IsZoomed(hwnd))
        {
            return IntPtr.Zero;
        }

        // При wParam = TRUE здесь NCCALCSIZE_PARAMS, при FALSE — RECT; первым полем структуры
        // идёт тот же RECT предлагаемого окна, поэтому читается одинаково.
        var proposed = Marshal.PtrToStructure<RECT>(lParam);
        if (!TryGetWorkAreaForRect(proposed, out var work))
        {
            return IntPtr.Zero;
        }

        var client = ClientAreaFor(
            new Rect(proposed.left, proposed.top, proposed.right - proposed.left, proposed.bottom - proposed.top),
            work);
        Marshal.StructureToPtr(
            new RECT
            {
                left = (int)client.Left,
                top = (int)client.Top,
                right = (int)client.Right,
                bottom = (int)client.Bottom
            },
            lParam,
            fDeleteOld: false);

        handled = true;

        // Ноль при wParam = TRUE значил бы «сохрани старую клиентскую область у левого верхнего
        // угла» — ровно так же, как у WindowChrome, просим перерисовку.
        return wParam != IntPtr.Zero ? new IntPtr(WVR_REDRAW) : IntPtr.Zero;
    }

    /// <summary>
    /// Клиентская область развёрнутого окна: его прямоугольник, обрезанный по рабочей области.
    /// Окно, с рабочей областью не пересекающееся, остаётся как есть — пустой клиент хуже.
    /// </summary>
    internal static Rect ClientAreaFor(Rect proposed, Rect work)
    {
        var client = Rect.Intersect(proposed, work);
        return client.IsEmpty || client.Width <= 0 || client.Height <= 0 ? proposed : client;
    }

    /// <summary>
    /// Второй рубеж: что бы ни назначило развёрнутому окну размер — система, смена монитора
    /// или пересчёт масштаба, — прямоугольник правится на рабочую область прямо перед тем, как
    /// он вступит в силу. Одного <c>WM_GETMINMAXINFO</c> мало: его отвечают только на переход в
    /// развёрнутое состояние, а размер окну меняют и после.
    /// </summary>
    private static void ClampMaximized(IntPtr hwnd, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero || !IsZoomed(hwnd))
        {
            return;
        }

        var position = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        var moving = (position.flags & SWP_NOMOVE) == 0;
        var sizing = (position.flags & SWP_NOSIZE) == 0;
        if (!moving && !sizing)
        {
            return;
        }

        // Монитор считаем по предлагаемому прямоугольнику, а не по текущему положению окна:
        // при переносе развёрнутого окна на другой экран они ещё не совпадают.
        var proposed = new RECT
        {
            left = position.x,
            top = position.y,
            right = position.x + Math.Max(1, position.cx),
            bottom = position.y + Math.Max(1, position.cy)
        };
        if (!TryGetWorkAreaForRect(proposed, out var work))
        {
            return;
        }

        var changed = false;
        if (moving && (position.x != (int)work.X || position.y != (int)work.Y))
        {
            position.x = (int)work.X;
            position.y = (int)work.Y;
            changed = true;
        }

        if (sizing && (position.cx != (int)work.Width || position.cy != (int)work.Height))
        {
            position.cx = (int)work.Width;
            position.cy = (int)work.Height;
            changed = true;
        }

        if (changed)
        {
            Marshal.StructureToPtr(position, lParam, fDeleteOld: true);
        }
    }

    private static IntPtr OnGetMinMaxInfo(
        Window window,
        HwndSource source,
        IntPtr hwnd,
        IntPtr lParam,
        ref bool handled)
    {
        if (lParam == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var changed = ApplyMinimumSize(window, source, ref info);

        if (TryGetWorkArea(hwnd, out var work, out var monitor))
        {
            // Позиция считается от левого верхнего угла монитора, а не рабочего стола:
            // на втором мониторе rcMonitor.left уже не ноль.
            info.ptMaxPosition.x = (int)(work.X - monitor.X);
            info.ptMaxPosition.y = (int)(work.Y - monitor.Y);
            info.ptMaxSize.x = (int)work.Width;
            info.ptMaxSize.y = (int)work.Height;

            // Без ограничения дорожки Windows всё равно разрешит развёрнутому окну быть больше
            // рабочей области — панель задач снова окажется накрытой.
            info.ptMaxTrackSize.x = (int)work.Width;
            info.ptMaxTrackSize.y = (int)work.Height;
            changed = true;
        }

        if (!changed)
        {
            return IntPtr.Zero;
        }

        Marshal.StructureToPtr(info, lParam, fDeleteOld: true);
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>
    /// Переносит <see cref="FrameworkElement.MinWidth"/> и <see cref="FrameworkElement.MinHeight"/>
    /// в аппаратные пиксели дорожки изменения размера.
    /// </summary>
    /// <remarks>
    /// Пересчёт обязательно через <c>CompositionTarget.TransformToDevice</c>, а не через реальный
    /// DPI монитора: <see cref="UiScale"/> масштабирует интерфейс подделанным <c>WM_DPICHANGED</c>
    /// и заодно переписывает минимумы окна (<c>850 / factor</c>). Произведение одного на другое
    /// постоянно, поэтому физический минимум остаётся тем же самым при любом масштабе — а взятый
    /// в обход преобразования он бы уезжал вслед за масштабом.
    /// </remarks>
    private static bool ApplyMinimumSize(Window window, HwndSource source, ref MINMAXINFO info)
    {
        if (source.CompositionTarget is not { } target)
        {
            return false;
        }

        var scale = target.TransformToDevice;
        var width = window.MinWidth * scale.M11;
        var height = window.MinHeight * scale.M22;
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0)
        {
            return false;
        }

        info.ptMinTrackSize.x = (int)Math.Ceiling(width);
        info.ptMinTrackSize.y = (int)Math.Ceiling(height);
        return true;
    }

    private static bool TryGetWorkAreaForRect(RECT rect, out Rect work)
    {
        work = default;

        var screen = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
        if (screen == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(screen, ref info))
        {
            return false;
        }

        work = new Rect(
            info.rcWork.left,
            info.rcWork.top,
            Math.Max(1, info.rcWork.right - info.rcWork.left),
            Math.Max(1, info.rcWork.bottom - info.rcWork.top));
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public int flags;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    private const int DwmwaExtendedFrameBounds = 9;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);


    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
