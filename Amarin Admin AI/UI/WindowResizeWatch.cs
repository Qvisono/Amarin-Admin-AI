using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Amarin.UI;

/// <summary>
/// Говорит, когда размер окна меняется жестом: окно тянут за край или разворачивают и
/// возвращают. На это время лента перекладывает только видимое (см. <c>TranscriptReflow</c>).
/// </summary>
/// <remarks>
/// <para>
/// Только слушает: ни одно сообщение здесь не обрабатывается и не меняется. Минимальный размер
/// и прямоугольник развёрнутого окна — забота <see cref="WindowMaximizeFix"/>, и второго
/// хозяина у <c>WM_GETMINMAXINFO</c> быть не должно.
/// </para>
/// <para>
/// <see cref="Began"/> поднимается на <c>WM_WINDOWPOSCHANGING</c>, то есть до того, как окно
/// получило новый размер и WPF его разложил: перехватчик этого объекта стоит позже остальных,
/// а <see cref="HwndSource"/> зовёт последний добавленный первым. Иначе первый же шаг
/// перетаскивания (и весь разворот) успевал бы переложить ленту целиком.
/// </para>
/// <para>
/// Жест — это два случая. Внутри <c>WM_ENTERSIZEMOVE</c>…<c>WM_EXITSIZEMOVE</c> человек держит
/// мышь на кромке: конец — отпускание. Разворот, возврат и подъём из панели задач в
/// развёрнутом виде меняют <c>IsZoomed</c>: конец — первый простой после нового кадра. Всё
/// прочее — программная смена ширины, перенос на монитор с другим масштабом и поддельный
/// <c>WM_DPICHANGED</c> из <see cref="UiScale"/> — жестом не считается и раскладывается как
/// раньше, сразу целиком.
/// </para>
/// </remarks>
internal sealed class WindowResizeWatch
{
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int SWP_NOSIZE = 0x0001;

    private readonly Window _window;
    private bool _hooked;
    private bool _inSizeMove;
    private bool _zoomed;
    private bool _endQueued;

    private WindowResizeWatch(Window window) => _window = window;

    /// <summary>Размер вот-вот поменяется жестом.</summary>
    public event Action? Began;

    /// <summary>Жест кончился, и новый размер разложен.</summary>
    public event Action? Ended;

    /// <summary>Идёт жест: между <see cref="Began"/> и <see cref="Ended"/>.</summary>
    public bool IsActive { get; private set; }

    public static WindowResizeWatch Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var watch = new WindowResizeWatch(window);
        if (!watch.TryHook())
        {
            window.SourceInitialized += (_, _) => watch.TryHook();
        }

        return watch;
    }

    /// <remarks>
    /// Источник — по хэндлу, а не по визуалу: окно создаётся раньше показа, и на
    /// <c>SourceInitialized</c> дерево ещё не привязано к источнику (та же ловушка, что
    /// описана у <see cref="WindowMaximizeFix"/>).
    /// </remarks>
    private bool TryHook()
    {
        if (_hooked)
        {
            return true;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero || HwndSource.FromHwnd(handle) is not { } source)
        {
            return false;
        }

        _zoomed = IsZoomed(handle);
        source.AddHook(Hook);
        _hooked = true;
        return true;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_ENTERSIZEMOVE:
                _inSizeMove = true;
                break;

            case WM_EXITSIZEMOVE:
                _inSizeMove = false;
                if (IsActive)
                {
                    QueueEnd();
                }

                break;

            case WM_WINDOWPOSCHANGING:
                OnPositionChanging(hwnd, lParam);
                break;

            case WM_WINDOWPOSCHANGED:
                _zoomed = IsZoomed(hwnd);
                break;
        }

        return IntPtr.Zero;
    }

    private void OnPositionChanging(IntPtr hwnd, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero || IsIconic(hwnd))
        {
            return;
        }

        var position = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if ((position.flags & SWP_NOSIZE) != 0 ||
            !GetWindowRect(hwnd, out var current) ||
            (current.right - current.left == position.cx && current.bottom - current.top == position.cy))
        {
            return;
        }

        // Разворот ставит WS_MAXIMIZE до того, как назначить прямоугольник, а возврат снимает
        // его раньше, — поэтому по флагу здесь уже видно, куда окно идёт.
        var stateChange = IsZoomed(hwnd) != _zoomed;
        if (!_inSizeMove && !stateChange)
        {
            return;
        }

        if (!IsActive)
        {
            IsActive = true;
            Began?.Invoke();
        }

        if (!_inSizeMove)
        {
            QueueEnd();
        }
    }

    /// <summary>
    /// Конец — приоритетом <see cref="DispatcherPriority.ContextIdle"/>: после раскладки и кадра
    /// нового размера, когда отпускать остальное уже не мешает его показать.
    /// </summary>
    private void QueueEnd()
    {
        if (_endQueued)
        {
            return;
        }

        _endQueued = true;
        _window.Dispatcher.BeginInvoke(
            () =>
            {
                _endQueued = false;
                if (_inSizeMove || !IsActive)
                {
                    return;
                }

                IsActive = false;
                Ended?.Invoke();
            },
            DispatcherPriority.ContextIdle);
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
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
