using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Amarin.Core;
using Amarin.Tools;
using static Amarin.Computer.ComputerNative;

namespace Amarin.Computer;

/// <summary>
/// Экран, окна, мышь и клавиатура этого ПК для агента (<see cref="IComputer"/>).
/// </summary>
/// <remarks>
/// <para>
/// Только исполняет: что можно трогать, решает ядро (<see cref="ComputerSafety"/>). Здесь —
/// чтобы ядро видело правду: какое окно под точкой, повышено ли оно, не консоль ли, не
/// заблокирована ли Windows.
/// </para>
/// <para>
/// Координаты физические: процесс PerMonitorV2 (<c>app.manifest</c>), и Windows, и UI Automation
/// отдают ему настоящие пиксели. Масштаб интерфейса программы (<c>UiScale</c>) подделывает DPI
/// только её окну и сюда не доходит.
/// </para>
/// </remarks>
internal sealed class WindowsComputer : IComputer
{
    /// <summary>Сколько ждать UI Automation: зависшая программа не должна держать агента вечно.</summary>
    private static readonly TimeSpan AutomationTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Пауза между событиями ввода: программам нужно время разобрать нажатие.</summary>
    private static readonly TimeSpan InputGap = TimeSpan.FromMilliseconds(40);

    /// <summary>Сколько знаков уходит одним вызовом <c>SendInput</c>.</summary>
    /// <remarks>
    /// Пачками, а не по знаку с паузой: 4000 знаков печатались полминуты. Целиком одним вызовом
    /// тоже нельзя — часть программ теряет знаки, когда очередь ввода переполнена.
    /// </remarks>
    internal const int TypingBatch = 24;

    private static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(10);

    private readonly UiaTree _tree = new();

    public ComputerDesk Desk()
    {
        if (SessionLocked())
        {
            return ComputerDesk.Locked;
        }

        // Ввод уходит на тот стол, что сейчас принимает его. Не открылся или не «Default» —
        // на экране вход в Windows или вопрос UAC, и программам туда хода нет.
        var desk = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desk == IntPtr.Zero)
        {
            return ComputerDesk.Secure;
        }

        try
        {
            var name = new char[64];
            return GetUserObjectInformationW(desk, UoiName, name, name.Length * 2, out _) &&
                   new string(name).TrimEnd('\0').Equals("Default", StringComparison.OrdinalIgnoreCase)
                ? ComputerDesk.Open
                : ComputerDesk.Secure;
        }
        finally
        {
            _ = CloseDesktop(desk);
        }
    }

    public IReadOnlyList<ComputerMonitor> Monitors() => AllMonitors();

    /// <summary>Мониторы этого ПК — и для свечения по краям, которому экземпляр порта не нужен.</summary>
    internal static IReadOnlyList<ComputerMonitor> AllMonitors()
    {
        var found = new List<(ScreenRect Bounds, bool Primary, double Scale)>();
        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(monitor, ref info))
            {
                var scale = GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
                found.Add((ToRect(info.Monitor), (info.Flags & MonitorInfoPrimary) != 0, scale));
            }

            return true;
        }, IntPtr.Zero);

        // Главный монитор — первый, остальные слева направо: так их номера понятны и модели, и
        // человеку, и не прыгают между вызовами.
        return
        [
            .. found
                .OrderByDescending(item => item.Primary)
                .ThenBy(item => item.Bounds.Left)
                .ThenBy(item => item.Bounds.Top)
                .Select((item, index) => new ComputerMonitor(index + 1, item.Bounds, item.Primary, item.Scale))
        ];
    }

    public IReadOnlyList<ComputerWindow> Windows()
    {
        var monitors = Monitors();
        var foreground = Root(GetForegroundWindow());
        var handles = new List<IntPtr>();
        _ = EnumWindows((hwnd, _) =>
        {
            handles.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        var windows = new List<ComputerWindow>();
        foreach (var hwnd in handles)
        {
            if (Listed(hwnd) && Describe(hwnd, monitors, foreground) is { } window)
            {
                windows.Add(window);
            }
        }

        return windows;
    }

    public ComputerWindow? Window(long handle)
    {
        var hwnd = new IntPtr(handle);
        return IsWindow(hwnd) ? Describe(hwnd, Monitors(), Root(GetForegroundWindow())) : null;
    }

    public ComputerWindow? WindowAt(ScreenPoint point)
    {
        var hit = WindowFromPoint(new Point { X = point.X, Y = point.Y });
        var root = Root(hit);
        return root == IntPtr.Zero ? null : Describe(root, Monitors(), Root(GetForegroundWindow()));
    }

    public ComputerWindow? Foreground()
    {
        var root = Root(GetForegroundWindow());
        return root == IntPtr.Zero ? null : Describe(root, Monitors(), root);
    }

    public ComputerElement? Focused() => UiaTree.Focused();

    public Task<ComputerTree> InspectAsync(long window, int limit, TimeSpan budget, CancellationToken cancellationToken) =>
        Automation(() => _tree.Inspect(new IntPtr(window), limit, budget), cancellationToken);

    /// <remarks>
    /// Окно закрыто — элемента нет, что бы ни ответил запомненный объект UI Automation: поставщик
    /// закрытого окна ещё какое-то время отвечает пустыми свойствами вместо ошибки.
    /// </remarks>
    public Task<ComputerElement?> ElementAsync(long window, string key, CancellationToken cancellationToken) =>
        IsWindow(new IntPtr(window))
            ? Automation(() => _tree.Element(new IntPtr(window), key), cancellationToken)
            : Task.FromResult<ComputerElement?>(null);

    public Task<ComputerShot> CaptureAsync(ScreenRect area, int maxSide, CancellationToken cancellationToken) =>
        Task.Run(() => ScreenshotTool.CaptureArea(area, maxSide), cancellationToken);

    public async Task PointerAsync(ComputerPointer action, CancellationToken cancellationToken)
    {
        await MoveAsync(action.At, cancellationToken).ConfigureAwait(false);
        switch (action.Kind)
        {
            case ComputerPointerKind.Click:
                await ButtonAsync(MouseLeftDown, MouseLeftUp, cancellationToken).ConfigureAwait(false);
                break;

            case ComputerPointerKind.DoubleClick:
                await ButtonAsync(MouseLeftDown, MouseLeftUp, cancellationToken).ConfigureAwait(false);
                await ButtonAsync(MouseLeftDown, MouseLeftUp, cancellationToken).ConfigureAwait(false);
                break;

            case ComputerPointerKind.RightClick:
                await ButtonAsync(MouseRightDown, MouseRightUp, cancellationToken).ConfigureAwait(false);
                break;

            case ComputerPointerKind.Scroll:
                // Колесо: плюс — от себя, то есть вверх. У модели плюс — вниз.
                if (action.ScrollY != 0)
                {
                    Send(Mouse(MouseWheel, data: -action.ScrollY * 120));
                }

                if (action.ScrollX != 0)
                {
                    Send(Mouse(MouseHWheel, data: action.ScrollX * 120));
                }

                break;

            case ComputerPointerKind.Drag when action.To is { } to:
                Send(Mouse(MouseLeftDown));
                await Task.Delay(InputGap, cancellationToken).ConfigureAwait(false);

                // Шагами, а не прыжком: программа узнаёт перетаскивание по движению с зажатой кнопкой.
                const int Steps = 12;
                for (var step = 1; step <= Steps; step++)
                {
                    var x = action.At.X + (to.X - action.At.X) * step / Steps;
                    var y = action.At.Y + (to.Y - action.At.Y) * step / Steps;
                    Send(Absolute(new ScreenPoint(x, y)));
                    await Task.Delay(15, cancellationToken).ConfigureAwait(false);
                }

                Send(Mouse(MouseLeftUp));
                break;
        }
    }

    public async Task TypeAsync(string text, CancellationToken cancellationToken)
    {
        foreach (var batch in TypingBatches(text, TypingBatch))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Send(batch);
            await Task.Delay(TypingPause, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Текст событиями ввода, по <paramref name="size"/> знаков в пачке. Перевод строки — Enter,
    /// табуляция — Tab, остальное — знаком Юникода, мимо раскладки.
    /// </summary>
    internal static IEnumerable<Input[]> TypingBatches(string text, int size)
    {
        var batch = new List<Input>(size * 2);
        var count = 0;
        foreach (var ch in text.ReplaceLineEndings("\n"))
        {
            switch (ch)
            {
                case '\n':
                    batch.Add(Key(0x0D, up: false));
                    batch.Add(Key(0x0D, up: true));
                    break;
                case '\t':
                    batch.Add(Key(0x09, up: false));
                    batch.Add(Key(0x09, up: true));
                    break;
                default:
                    batch.Add(Unicode(ch, up: false));
                    batch.Add(Unicode(ch, up: true));
                    break;
            }

            if (++count == size)
            {
                yield return [.. batch];
                batch.Clear();
                count = 0;
            }
        }

        if (batch.Count > 0)
        {
            yield return [.. batch];
        }
    }

    public async Task KeysAsync(IReadOnlyList<string> chord, CancellationToken cancellationToken)
    {
        var keys = chord.Select(ComputerKeyMap.VirtualKey).ToList();
        var down = keys.Select(key => Key(key, up: false));
        var up = Enumerable.Reverse(keys).Select(key => Key(key, up: true));
        Send([.. down, .. up]);
        await Task.Delay(InputGap, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> FocusWindowAsync(long window, CancellationToken cancellationToken) =>
        Task.Run(() => Activate(new IntPtr(window)), cancellationToken);

    public Task<bool> FocusElementAsync(long window, string key, CancellationToken cancellationToken) =>
        IsWindow(new IntPtr(window))
            ? Automation(() => _tree.Focus(new IntPtr(window), key), cancellationToken)
            : Task.FromResult(false);

    public Task<bool> InvokeAsync(long window, string key, CancellationToken cancellationToken) =>
        IsWindow(new IntPtr(window))
            ? Automation(() => _tree.Invoke(new IntPtr(window), key), cancellationToken)
            : Task.FromResult(false);

    public async Task SettleAsync(TimeSpan max, CancellationToken cancellationToken)
    {
        // Затишье — когда переднее окно, его заголовок и окно с фокусом дважды подряд не
        // поменялись. Опрос частый и без UI Automation: прежде на каждое нажатие уходило не
        // меньше полусекунды, даже когда в окне не менялось ничего.
        var clock = Stopwatch.StartNew();
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        var last = Signature();
        var calm = 0;
        while (clock.Elapsed < max && calm < 2)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            var now = Signature();
            calm = now == last ? calm + 1 : 0;
            last = now;
        }
    }

    public string? ClipboardText() => ClipboardNative.HasText() ? StaThread.Run(ClipboardNative.TryGetText) : null;

    public IDisposable Hold() => new AwakeRequest();

    // ───────────────────────── окна ─────────────────────────

    /// <summary>Окно, которое человек видит отдельным окном, — как его показывает Alt+Tab.</summary>
    private static bool Listed(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || Cloaked(hwnd))
        {
            return false;
        }

        if ((GetWindowLongW(hwnd, GwlExStyle) & WsExToolWindow) != 0)
        {
            return false;
        }

        // Панель задач без заголовка — тоже окно, которое человек трогает: значки в трее там.
        return Text(hwnd).Length > 0 || ClassOf(hwnd) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static ComputerWindow? Describe(IntPtr hwnd, IReadOnlyList<ComputerMonitor> monitors, IntPtr foreground)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return null;
        }

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        var className = ClassOf(hwnd);
        var title = Text(hwnd);
        if (title.Length == 0 && className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            title = "Taskbar";
        }

        var bounds = Bounds(hwnd);
        var monitor = monitors.FirstOrDefault(item => item.Bounds.Contains(bounds.Center))?.Index ??
                      monitors.FirstOrDefault(item => !item.Bounds.Intersect(bounds).IsEmpty)?.Index ?? 1;
        var process = ProcessName((int)pid);
        return new ComputerWindow(
            hwnd.ToInt64(),
            title,
            process,
            (int)pid,
            bounds,
            monitor,
            KindOf(hwnd, className, process),
            Elevated((int)pid),
            IsIconic(hwnd),
            hwnd == foreground);
    }

    /// <summary>Что это за окно — для правил ядра: консоль, «Выполнить», Проводник, выбор файла.</summary>
    internal static ComputerWindowKind KindOf(IntPtr hwnd, string className, string process)
    {
        if (className is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "PseudoConsoleWindow" or "mintty" ||
            process.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase) ||
            process.Equals("OpenConsole", StringComparison.OrdinalIgnoreCase) ||
            process.Equals("conhost", StringComparison.OrdinalIgnoreCase))
        {
            return ComputerWindowKind.Console;
        }

        if (className is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW")
        {
            return ComputerWindowKind.Explorer;
        }

        if (className == "#32770" && hwnd != IntPtr.Zero)
        {
            // «Выполнить» и «Создать задачу» Диспетчера задач: строка в них запускает программу.
            // Узнаются по полю команды (12298), а не по заголовку — тот переведён.
            if (GetDlgItem(hwnd, 12298) != IntPtr.Zero &&
                (process.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                 process.Equals("Taskmgr", StringComparison.OrdinalIgnoreCase)))
            {
                return ComputerWindowKind.RunDialog;
            }

            if (FindWindowExW(hwnd, IntPtr.Zero, "DUIViewWndClassName", null) != IntPtr.Zero)
            {
                return ComputerWindowKind.FileDialog;
            }
        }

        return ComputerWindowKind.Normal;
    }

    /// <summary>
    /// Запущен ли процесс от администратора. Не удалось узнать — считаем, что да: ввод туда всё
    /// равно не дойдёт, а честный отказ лучше нажатия в пустоту.
    /// </summary>
    private static bool Elevated(int pid)
    {
        if (pid == Environment.ProcessId)
        {
            return false;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (process == IntPtr.Zero)
        {
            return true;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                return true;
            }

            try
            {
                return !GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) || elevated != 0;
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    /// <summary>
    /// Имя программы окна — у самого процесса, на каждый вопрос. Не узнать — «?».
    /// </summary>
    /// <remarks>
    /// Без кэша по номеру процесса: программа живёт днями, а Windows раздаёт номера заново, и
    /// запомненное имя досталось бы чужому процессу. По имени <see cref="KindOf"/> узнаёт окно
    /// «Выполнить» и консоли, его же видят модель и SynGuard. Сам вопрос — доли миллисекунды.
    /// </remarks>
    internal static string ProcessName(int pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (process == IntPtr.Zero)
        {
            return "?";
        }

        try
        {
            var path = new char[1024];
            var length = path.Length;
            return QueryFullProcessImageNameW(process, 0, path, ref length)
                ? Path.GetFileNameWithoutExtension(new string(path, 0, length))
                : "?";
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    private static IntPtr Root(IntPtr hwnd) => hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GaRoot);

    private static bool Cloaked(IntPtr hwnd) =>
        DwmGetWindowAttributeInt(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>Видимые границы окна, без невидимой рамки, которую Windows 10 и 11 добавляют вокруг.</summary>
    private static ScreenRect Bounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var frame, Marshal.SizeOf<Rect>()) == 0)
        {
            return ToRect(frame);
        }

        return GetWindowRect(hwnd, out var rect) ? ToRect(rect) : default;
    }

    private static string Text(IntPtr hwnd)
    {
        var buffer = new char[512];
        var length = GetWindowTextW(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = GetClassNameW(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    private static ScreenRect ToRect(Rect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    /// <summary>
    /// Выводит окно вперёд. Windows не даёт фоновому процессу забрать фокус, а окно программы на
    /// время управления свёрнуто, — поэтому, если простой вызов не прошёл, ввод потока
    /// присоединяется к потоку переднего окна, как делает сама Windows при Alt+Tab.
    /// </summary>
    private static bool Activate(IntPtr hwnd)
    {
        if (!IsWindow(hwnd))
        {
            return false;
        }

        if (IsIconic(hwnd))
        {
            _ = ShowWindow(hwnd, SwRestore);
        }

        if (SetForegroundWindow(hwnd) && Root(GetForegroundWindow()) == hwnd)
        {
            return true;
        }

        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var ownThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != ownThread &&
                       AttachThreadInput(ownThread, foregroundThread, true);
        try
        {
            _ = BringWindowToTop(hwnd);
            _ = SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                _ = AttachThreadInput(ownThread, foregroundThread, false);
            }
        }

        return Root(GetForegroundWindow()) == hwnd;
    }

    private static (IntPtr Window, string Title, IntPtr Focus) Signature()
    {
        var foreground = Root(GetForegroundWindow());
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        var focus = GetGUIThreadInfo(0, ref info) ? info.Focus : IntPtr.Zero;
        return (foreground, Text(foreground), focus);
    }

    private static bool SessionLocked() => SessionInfo() is { Flags: WtsSessionStateLock };

    /// <summary>
    /// Номер и флаги блокировки текущего сеанса из WTSINFOEX_LEVEL1. Номер читается из того же
    /// буфера ради теста: он сверяет его с сеансом процесса и так ловит неверное смещение, не
    /// завися от того, заблокирован ли компьютер во время прогона.
    /// </summary>
    internal static (int SessionId, int Flags)? SessionInfo()
    {
        // WTS_CURRENT_SESSION = -1.
        if (!WTSQuerySessionInformationW(IntPtr.Zero, -1, WtsSessionInfoEx, out var buffer, out var returned))
        {
            return null;
        }

        try
        {
            return returned >= WtsInfoExSessionFlags + 4
                ? (Marshal.ReadInt32(buffer, WtsInfoExSessionId), Marshal.ReadInt32(buffer, WtsInfoExSessionFlags))
                : null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    // ───────────────────────── ввод ─────────────────────────

    private static async Task MoveAsync(ScreenPoint point, CancellationToken cancellationToken)
    {
        Send(Absolute(point));
        await Task.Delay(InputGap, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ButtonAsync(uint down, uint up, CancellationToken cancellationToken)
    {
        Send(Mouse(down));
        await Task.Delay(30, cancellationToken).ConfigureAwait(false);
        Send(Mouse(up));
        await Task.Delay(InputGap, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Абсолютное перемещение по всему виртуальному столу — на любой монитор.</summary>
    internal static Input Absolute(ScreenPoint point)
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen) - 1);
        var height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen) - 1);
        return new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Dx = (int)Math.Round((point.X - left) * 65535.0 / width),
                    Dy = (int)Math.Round((point.Y - top) * 65535.0 / height),
                    Flags = MouseMove | MouseAbsolute | MouseVirtualDesk
                }
            }
        };
    }

    private static Input Mouse(uint flags, int data = 0) => new()
    {
        Type = InputMouse,
        Data = new InputUnion { Mouse = new MouseInput { Flags = flags, MouseData = data } }
    };

    private static Input Key(ushort vk, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                Vk = vk,
                Flags = (ComputerKeyMap.IsExtended(vk) ? KeyExtended : 0) | (up ? KeyUp : 0)
            }
        }
    };

    private static Input Unicode(char ch, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion { Keyboard = new KeyboardInput { Scan = ch, Flags = KeyUnicode | (up ? KeyUp : 0) } }
    };

    /// <summary>Отправляет ввод. Не дошёл — исключение: модель узнает, что ничего не нажато.</summary>
    private static void Send(params Input[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static async Task<T> Automation<T>(Func<T> work, CancellationToken cancellationToken) =>
        await Task.Run(work, cancellationToken).WaitAsync(AutomationTimeout, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Держит систему и экран, пока его не отпустят: запрос питания на процесс, а не флаг потока —
    /// агент работает на пуле потоков, и флаг слетел бы с потоком, который его поставил.
    /// </summary>
    private sealed class AwakeRequest : IDisposable
    {
        private IntPtr _request;

        public AwakeRequest()
        {
            var reason = new ReasonContext
            {
                Version = PowerRequestContextVersion,
                Flags = PowerRequestContextSimpleString,
                SimpleReasonString = "Amarin Admin AI is operating this PC for a task."
            };
            _request = PowerCreateRequest(ref reason);
            if (_request != new IntPtr(-1) && _request != IntPtr.Zero)
            {
                _ = PowerSetRequest(_request, PowerRequestSystemRequired);
                _ = PowerSetRequest(_request, PowerRequestDisplayRequired);
            }

            // Погасший экран будит только ввод: пустое движение мыши ничего не сдвигает.
            try
            {
                Send(Mouse(MouseMove));
            }
            catch (Win32Exception)
            {
            }
        }

        public void Dispose()
        {
            var request = Interlocked.Exchange(ref _request, IntPtr.Zero);
            if (request == IntPtr.Zero || request == new IntPtr(-1))
            {
                return;
            }

            _ = PowerClearRequest(request, PowerRequestDisplayRequired);
            _ = PowerClearRequest(request, PowerRequestSystemRequired);
            _ = CloseHandle(request);
        }
    }
}
