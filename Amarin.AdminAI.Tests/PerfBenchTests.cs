using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Замер скорости: запуск окна, открытие большого чата, изменение размера и разворот окна с ним,
/// открытие настроек, журнала и «Состояния ПК», раскрытие папок в длинном списке чатов, самые
/// долгие остановки кадров, память и ЦП в простое. Не утверждение, а таблица для сравнения
/// «до/после».
/// </summary>
/// <remarks>
/// Работает только при <c>AMARIN_PERF_BENCH=&lt;файл&gt;</c> (строки дописываются в конец файла,
/// <c>AMARIN_PERF_LABEL</c> подписывает прогон); обычный прогон сразу выходит. Числа имеют смысл
/// в отдельном процессе и сборке Release: в общем прогоне код уже прогрет соседними тестами.
/// Разворот показывает окно на экране — на время замера его будет видно.
/// </remarks>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class PerfBenchTests : IDisposable
{
    private const int ChatMessages = 1200;

    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-bench-" + Guid.NewGuid().ToString("N"));

    public PerfBenchTests(WpfFixture wpf) => _wpf = wpf;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Bench()
    {
        var output = Environment.GetEnvironmentVariable("AMARIN_PERF_BENCH");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var label = Environment.GetEnvironmentVariable("AMARIN_PERF_LABEL") ?? "run";
        var lines = await _wpf.Ui.Invoke(async () =>
        {
            // Время по часам у всего, что ждёт кадра, зависит от экрана: при погашенном мониторе
            // DWM почти не составляет кадров, и WPF на каждом изменении размера ждёт показа по
            // две сотни миллисекунд даже у пустого окна. Поэтому рядом — время процессора потока
            // окна (cpu): это работа самого окна, и её экран не искажает.
            var results = new List<(string Name, double Value, string Unit)>
            {
                ("display refreshes per second (DWM)", DisplayRefreshesPerSecond(), "")
            };
            for (var i = 0; i < 3; i++)
            {
                var (construct, attach, firstFrame) = await StartWindow(i);
                results.Add(($"start#{i + 1} ctor", construct, "ms"));
                results.Add(($"start#{i + 1} attach", attach, "ms"));
                results.Add(($"start#{i + 1} first frame", firstFrame, "ms"));
            }

            results.AddRange(await SettingsAfterStart());
            results.AddRange(await OpenBigChat());
            results.AddRange(await ToggleFolders());
            return results;
        });

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"## {label} — {DateTime.Now:yyyy-MM-dd HH:mm}");
        foreach (var (name, value, unit) in lines)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {value:0.#} {unit} |");
        }

        File.AppendAllText(output, text.ToString());
    }

    private async Task<(double Construct, double Attach, double FirstFrame)> StartWindow(int run)
    {
        var services = Services("start" + run);

        var watch = Stopwatch.StartNew();
        var window = NewWindow();
        var construct = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        window.AttachServices(services);
        var attach = watch.Elapsed.TotalMilliseconds;

        var rendered = new TaskCompletionSource();
        window.ContentRendered += (_, _) => rendered.TrySetResult();
        watch.Restart();
        window.Show();
        await rendered.Task;
        var firstFrame = watch.Elapsed.TotalMilliseconds;

        window.Close();
        await Settle(200);
        return (construct, attach, firstFrame);
    }

    /// <summary>
    /// Настройки сразу после первого кадра (до всякого прогрева) и после трёх секунд простоя, а
    /// между ними — самая долгая остановка потока окна: всё, что окно делает в простое после
    /// запуска, ввод ждёт не дольше неё.
    /// </summary>
    private async Task<List<(string, double, string)>> SettingsAfterStart()
    {
        var results = new List<(string, double, string)>();
        foreach (var (label, wait) in new[] { ("right after first frame", 0), ("after 3 s idle", 3000) })
        {
            var window = NewWindow();
            window.AttachServices(Services("settings-" + wait));
            var rendered = new TaskCompletionSource();
            window.ContentRendered += (_, _) => rendered.TrySetResult();
            window.Show();
            await rendered.Task;
            if (wait > 0)
            {
                var stalls = new UiStalls();
                stalls.Start();
                await Settle(wait);
                results.Add(("max UI stall over 3 s after first frame", stalls.Stop(), "ms"));
            }

            var watch = Stopwatch.StartNew();
            var cpu = UiCpuMs();
            Invoke(window, "SettingsButton_Click");
            window.UpdateLayout();
            results.Add(($"settings open {label}, cpu", UiCpuMs() - cpu, "ms"));
            await Dispatcher.Yield(DispatcherPriority.Background);
            results.Add(($"settings open {label}", watch.Elapsed.TotalMilliseconds, "ms"));
            await Settle(300);
            window.Close();
            await Settle(200);
        }

        return results;
    }

    /// <summary>
    /// Самая долгая остановка потока окна: таймер на приоритете ввода тикает каждые 10 мс, и
    /// пауза между тиками сверх них — время, которое нажатие клавиши ждало бы своей очереди.
    /// </summary>
    private sealed class UiStalls
    {
        private readonly Stopwatch _clock = new();
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        private double _last;
        private double _max;

        public void Start()
        {
            _timer.Tick += OnTick;
            _clock.Start();
            _timer.Start();
        }

        public double Stop()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            return _max;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            var now = _clock.Elapsed.TotalMilliseconds;
            _max = Math.Max(_max, now - _last - 10);
            _last = now;
        }
    }

    private async Task<List<(string, double, string)>> OpenBigChat()
    {
        var results = new List<(string, double, string)>();
        var services = Services("chat");
        var session = BigChat();
        services.ChatStore.Save(session);
        services.ChatStore.Flush();

        var window = NewWindow();
        window.AttachServices(services);
        var rendered = new TaskCompletionSource();
        window.ContentRendered += (_, _) => rendered.TrySetResult();
        window.Show();
        await rendered.Task;
        await Settle(500);
        results.Add(("idle CPU over 5 s, empty chat", await IdleCpu(), "ms"));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        var frames = new FrameGaps();
        frames.Start();
        var watch = Stopwatch.StartNew();
        Call(window, "OpenChat", session.Id);
        results.Add(("chat open (sync)", watch.Elapsed.TotalMilliseconds, "ms"));

        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        results.Add(("chat first idle", watch.Elapsed.TotalMilliseconds, "ms"));

        var deadline = TimeSpan.FromSeconds(120);
        while (Unbuilt(window) > 0 && watch.Elapsed < deadline)
        {
            await Task.Delay(15);
        }

        results.Add(("chat fully built", watch.Elapsed.TotalMilliseconds, "ms"));
        frames.Stop();
        results.Add(("max frame gap while building", frames.MaxGap, "ms"));
        results.Add(("frames > 50 ms while building", frames.Over50, ""));

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        results.Add(("allocated while opening", allocated / 1048576.0, "MB"));
        results.Add(("heap growth after open", (heapAfter - heapBefore) / 1048576.0, "MB"));

        // Сразу после открытия ещё дописываются индекс поиска и файл чата — простой меряется позже.
        await Settle(5000);
        var idle = new FrameGaps();
        idle.CountOnly();
        results.Add(("idle CPU over 5 s, big chat", await IdleCpu(), "ms"));
        results.Add(("idle UI wakeups over 5 s", idle.Stop(), ""));

        results.AddRange(await DragEdge(window));
        results.AddRange(await MaximizeAndRestore(window));
        results.AddRange(await OpenOverlays(window));

        window.Close();
        await Settle(200);
        return results;
    }

    /// <summary>
    /// Окно с построенным большим чатом тянут за край: двадцать шагов ширины внутри
    /// <c>WM_ENTERSIZEMOVE</c>/<c>WM_EXITSIZEMOVE</c>, как это делает Windows, пока держат мышь.
    /// </summary>
    private static async Task<List<(string, double, string)>> DragEdge(MainWindow window)
    {
        var results = new List<(string, double, string)>();
        var handle = new WindowInteropHelper(window).Handle;
        var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var layouts = new DocumentLayouts(window);
        var height = (int)Math.Round(window.ActualHeight * scale);

        // 1280 → 900 и обратно: всё время уже колонки ленты (1070), то есть каждый шаг меняет
        // ширину сообщений.
        var widths = Enumerable.Range(1, 10).Select(i => 1280 - (38 * i))
            .Concat(Enumerable.Range(1, 10).Select(i => 900 + (38 * i)))
            .ToList();

        var frames = new FrameGaps();
        frames.Start();
        _ = SendMessage(handle, WmEnterSizeMove, IntPtr.Zero, IntPtr.Zero);
        var steps = new List<double>();
        var cpu = new List<double>();
        var counts = new List<int>();
        foreach (var width in widths)
        {
            layouts.Reset();
            var watch = Stopwatch.StartNew();
            var cpuBefore = UiCpuMs();
            _ = SetWindowPos(handle, IntPtr.Zero, 0, 0, (int)Math.Round(width * scale), height, SwpNoMove | SwpNoZOrder | SwpNoActivate);
            window.UpdateLayout();
            cpu.Add(UiCpuMs() - cpuBefore);
            await Dispatcher.Yield(DispatcherPriority.Background);
            steps.Add(watch.Elapsed.TotalMilliseconds);
            counts.Add(layouts.Count);
        }

        var released = Stopwatch.StartNew();
        var releasedCpu = UiCpuMs();
        _ = SendMessage(handle, WmExitSizeMove, IntPtr.Zero, IntPtr.Zero);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var settled = released.Elapsed.TotalMilliseconds;
        var settledCpu = UiCpuMs() - releasedCpu;
        await Settle(1500);
        frames.Stop();

        results.Add(("resize step, average", steps.Average(), "ms"));
        results.Add(("resize step, slowest", steps.Max(), "ms"));
        results.Add(("resize step cpu, average", cpu.Average(), "ms"));
        results.Add(("resize step cpu, slowest", cpu.Max(), "ms"));
        results.Add(("documents laid out per resize step", counts.Average(), ""));
        results.Add(("resize: settled after release", settled, "ms"));
        results.Add(("resize: settling cpu after release", settledCpu, "ms"));
        results.Add(("resize: max frame gap", frames.MaxGap, "ms"));
        results.Add(("resize: documents left at a stale width", layouts.Stale(), ""));
        layouts.Dispose();
        return results;
    }

    /// <summary>Маленькое окно с большим чатом разворачивают на весь экран и возвращают.</summary>
    private static async Task<List<(string, double, string)>> MaximizeAndRestore(MainWindow window)
    {
        var results = new List<(string, double, string)>();
        var handle = new WindowInteropHelper(window).Handle;
        var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        _ = SetWindowPos(handle, IntPtr.Zero, 0, 0, (int)Math.Round(900 * scale), (int)Math.Round(700 * scale), SwpNoMove | SwpNoZOrder | SwpNoActivate);
        window.UpdateLayout();
        await Settle(1500);

        var layouts = new DocumentLayouts(window);
        foreach (var (state, name) in new[] { (WindowState.Maximized, "maximize"), (WindowState.Normal, "restore") })
        {
            layouts.Reset();
            var frames = new FrameGaps();
            frames.Start();
            var watch = Stopwatch.StartNew();
            var cpuBefore = UiCpuMs();
            window.WindowState = state;
            window.UpdateLayout();
            var cpu = UiCpuMs() - cpuBefore;
            await Dispatcher.Yield(DispatcherPriority.Background);
            results.Add(($"{name}: first frame", watch.Elapsed.TotalMilliseconds, "ms"));
            results.Add(($"{name}: cpu to lay it out", cpu, "ms"));
            results.Add(($"{name}: documents laid out for it", layouts.Count, ""));
            await Settle(2000);
            frames.Stop();
            results.Add(($"{name}: max frame gap over 2 s", frames.MaxGap, "ms"));
        }

        results.Add(("maximize/restore: documents left at a stale width", layouts.Stale(), ""));
        layouts.Dispose();
        return results;
    }

    /// <summary>Настройки, журнал и «Состояние ПК»: сколько от щелчка до кадра с открытым.</summary>
    private static async Task<List<(string, double, string)>> OpenOverlays(MainWindow window)
    {
        var results = new List<(string, double, string)>();
        if (window.FindName("HealthOverlay") is HealthPanel health)
        {
            // Пробы на подставных ответах: замеряется окно, а не опрос системы.
            health.Probes =
            [
                _ => Task.FromResult(HealthRules.System(new SystemHealth(59, 49, TimeSpan.FromDays(1.3)))),
                _ => Task.FromResult(HealthRules.Stability(new EventHealth(0, 260, 0))),
                _ => Task.FromResult(HealthRules.Updates(new UpdateHealth(false, 3)))
            ];
        }

        foreach (var (name, open, close) in new[]
                 {
                     ("settings", "SettingsButton_Click", "SettingsCloseButton_Click"),
                     ("journal", "OpenJournal", "CloseJournal"),
                     ("health", "OpenHealth", "CloseHealth")
                 })
        {
            var times = new List<double>();
            var cpu = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                var watch = Stopwatch.StartNew();
                var cpuBefore = UiCpuMs();
                Invoke(window, open);
                window.UpdateLayout();
                cpu.Add(UiCpuMs() - cpuBefore);
                await Dispatcher.Yield(DispatcherPriority.Background);
                times.Add(watch.Elapsed.TotalMilliseconds);
                await Settle(300);
                Invoke(window, close);
                await Settle(200);
            }

            results.Add(($"{name} open, first", times[0], "ms"));
            results.Add(($"{name} open, then average", times.Skip(1).Average(), "ms"));
            results.Add(($"{name} open cpu, first", cpu[0], "ms"));
            results.Add(($"{name} open cpu, then average", cpu.Skip(1).Average(), "ms"));
        }

        return results;
    }

    /// <summary>
    /// Длинный список чатов с папками: щелчок по папке до кадра, самая долгая остановка кадров
    /// после него и запись раскладки на диск.
    /// </summary>
    private async Task<List<(string, double, string)>> ToggleFolders()
    {
        var results = new List<(string, double, string)>();
        var services = Services("folders");
        var ids = new List<string>();
        for (var i = 0; i < 300; i++)
        {
            var session = new ChatSession
            {
                Id = "c" + i.ToString("000", CultureInfo.InvariantCulture),
                Title = "Чат номер " + i.ToString(CultureInfo.InvariantCulture),
                CreatedAt = DateTime.Now.AddHours(-i),
                UpdatedAt = DateTime.Now.AddHours(-i)
            };
            session.Messages.Add(new ChatDisplayMessage { Id = "q", Role = "user", Text = "Вопрос " + i, CreatedAt = session.CreatedAt });
            services.ChatStore.Save(session);
            ids.Add(session.Id);
        }

        services.ChatStore.Flush();
        var folders = new List<ChatFolder>();
        for (var f = 0; f < 15; f++)
        {
            var folder = services.Organizer.CreateFolder("Папка " + f.ToString(CultureInfo.InvariantCulture));
            services.Organizer.MoveToFolder(ids.Skip(f * 12).Take(12).ToList(), folder.Id);
            folders.Add(folder);
        }

        var window = NewWindow();
        window.AttachServices(services);
        var rendered = new TaskCompletionSource();
        window.ContentRendered += (_, _) => rendered.TrySetResult();
        window.Show();
        await rendered.Task;
        Invoke(window, "RefreshChatList");
        await Settle(500);

        var panel = (Panel)window.FindName("ChatListPanel")!;
        var target = folders[7].Id;
        var clicks = new List<double>();
        var cpu = new List<double>();
        var gaps = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            var header = panel.Children.OfType<Button>().Single(button => button.Tag is ChatFolder folder && folder.Id == target);
            var frames = new FrameGaps();
            frames.Start();
            var watch = Stopwatch.StartNew();
            var cpuBefore = UiCpuMs();
            header.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.UpdateLayout();
            cpu.Add(UiCpuMs() - cpuBefore);
            await Dispatcher.Yield(DispatcherPriority.Background);
            clicks.Add(watch.Elapsed.TotalMilliseconds);
            await Settle(600);
            frames.Stop();
            gaps.Add(frames.MaxGap);
        }

        results.Add(("folder click to frame, average", clicks.Average(), "ms"));
        results.Add(("folder click to frame, slowest", clicks.Max(), "ms"));
        results.Add(("folder click cpu, average", cpu.Average(), "ms"));
        results.Add(("folder click: max frame gap", gaps.Max(), "ms"));

        var write = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            services.Organizer.SetCollapsed(target, i % 2 == 0);
        }

        results.Add(("organizer change on the UI thread", write.Elapsed.TotalMilliseconds / 20, "ms"));

        // Переименование одного чата: состав списка тот же, поменялась одна строка.
        var rename = Stopwatch.StartNew();
        var renameCpu = UiCpuMs();
        services.ChatStore.Rename(ids[200], "Новое имя");
        Invoke(window, "RefreshChatList");
        window.UpdateLayout();
        results.Add(("chat list after a rename, cpu", UiCpuMs() - renameCpu, "ms"));
        await Dispatcher.Yield(DispatcherPriority.Background);
        results.Add(("chat list after a rename", rename.Elapsed.TotalMilliseconds, "ms"));

        window.Close();
        await Settle(200);
        return results;
    }

    private AppServices Services(string folder)
    {
        var services = UiServices.Build(Path.Combine(_root, folder), "k", new HttpClientHandler());

        // Проверка обновлений сходила бы в GitHub посреди замера.
        services.Settings.AutoCheckUpdates = false;
        return services;
    }

    /// <summary>Считает раскладки документов сообщений: сколько <c>RichTextBox</c> поменяли ширину.</summary>
    private sealed class DocumentLayouts : IDisposable
    {
        private readonly List<RichTextBox> _boxes;

        public DocumentLayouts(MainWindow window)
        {
            _boxes = Descendants<RichTextBox>((DependencyObject)window.FindName("MessagesPanel")!).ToList();
            foreach (var box in _boxes)
            {
                box.SizeChanged += OnSizeChanged;
            }
        }

        public int Count { get; private set; }

        public void Reset() => Count = 0;

        /// <summary>Документы, чья ширина страницы так и не догнала ширину поля.</summary>
        public int Stale() => _boxes.Count(box =>
            box.IsVisible && !double.IsNaN(box.Document.PageWidth) && box.Width is double.NaN &&
            Math.Abs(box.Document.PageWidth - box.ActualWidth) > 1);

        public void Dispose()
        {
            foreach (var box in _boxes)
            {
                box.SizeChanged -= OnSizeChanged;
            }
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
            {
                Count++;
            }
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is T match && !ReferenceEquals(node, root))
            {
                yield return match;
            }

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    private static void Invoke(MainWindow window, string name)
    {
        var method = typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                     ?? throw new MissingMethodException(nameof(MainWindow), name);
        _ = method.Invoke(window, method.GetParameters().Length == 0 ? [] : [null, null]);
    }

    /// <summary>Время процессора, которое поток окна потратил с его запуска, в миллисекундах.</summary>
    private static double UiCpuMs()
    {
        _ = GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user);
        return (kernel + user) / 10_000.0;
    }

    /// <summary>Сколько раз за секунду DWM обновил экран: при погашенном мониторе — единицы.</summary>
    private static double DisplayRefreshesPerSecond()
    {
        var first = new DwmTimingInfo { Size = Marshal.SizeOf<DwmTimingInfo>(), Rest = new byte[248] };
        var second = new DwmTimingInfo { Size = Marshal.SizeOf<DwmTimingInfo>(), Rest = new byte[248] };
        if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref first) != 0)
        {
            return double.NaN;
        }

        Thread.Sleep(1000);
        return DwmGetCompositionTimingInfo(IntPtr.Zero, ref second) == 0 ? second.Refreshes - first.Refreshes : double.NaN;
    }

    /// <summary>
    /// <c>DWM_TIMING_INFO</c> (pack 1, 292 байта): поля до счётчика обновлений и хвост. DWM
    /// сверяет размер, поэтому хвост ровно до конца структуры.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DwmTimingInfo
    {
        public int Size;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public ulong RefreshPeriod;
        public uint ComposeNumerator;
        public uint ComposeDenominator;
        public ulong VBlank;
        public ulong Refreshes;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 248)]
        public byte[] Rest;
    }

    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, int flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DwmTimingInfo info);

    /// <summary>Время процессора всех потоков за пять секунд простоя.</summary>
    private static async Task<double> IdleCpu()
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var before = process.TotalProcessorTime;
        await Task.Delay(5000);
        process.Refresh();
        return (process.TotalProcessorTime - before).TotalMilliseconds;
    }

    private static MainWindow NewWindow() => new()
    {
        Width = 1280,
        Height = 860,
        Left = -32000,
        Top = 0,
        ShowActivated = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.Manual
    };

    /// <summary>Переписка на <see cref="ChatMessages"/> сообщений: текст, код, таблицы, формулы.</summary>
    private static ChatSession BigChat()
    {
        var session = new ChatSession
        {
            Id = "bench-" + Guid.NewGuid().ToString("N")[..8],
            Title = "Bench",
            CreatedAt = DateTime.Now.AddDays(-1),
            UpdatedAt = DateTime.Now
        };

        var started = DateTime.Now.AddHours(-ChatMessages);
        for (var i = 0; i < ChatMessages; i++)
        {
            var user = i % 2 == 0;
            session.Messages.Add(new ChatDisplayMessage
            {
                Id = "m" + i.ToString(CultureInfo.InvariantCulture),
                Role = user ? "user" : "assistant",
                CreatedAt = started.AddMinutes(i),
                Text = user ? UserText(i) : AssistantText(i),
                Status = AssistantStatus.Complete,
                ResolvedModelId = user ? null : "grok-4-6",
                Duration = user ? TimeSpan.Zero : TimeSpan.FromSeconds(3 + (i % 20))
            });
        }

        return session;
    }

    private static string UserText(int i) => (i % 6) switch
    {
        0 => "Проверь, почему служба печати не стартует после обновления, и покажи журнал за сутки.",
        2 => "Короткий вопрос номер " + i,
        _ => "Сравни два подхода к резервному копированию и скажи, какой надёжнее для домашнего ПК с одним диском и облаком."
    };

    private static string AssistantText(int i)
    {
        var text = new StringBuilder();
        text.AppendLine("## Что я нашёл").AppendLine();
        text.AppendLine("Служба **Spooler** останавливается из-за повреждённого драйвера. Ниже шаги и пояснения, " +
                        "почему каждый из них нужен, и что проверить после перезапуска.").AppendLine();
        if (i % 3 == 1)
        {
            text.AppendLine("```powershell");
            for (var line = 0; line < 12; line++)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"Get-Service -Name Spooler | Select-Object Status, StartType # {line}");
            }

            text.AppendLine("```").AppendLine();
        }

        if (i % 5 == 1)
        {
            text.AppendLine("| Параметр | Было | Стало |");
            text.AppendLine("|---|---|---|");
            for (var row = 0; row < 6; row++)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"| Строка {row} | {row * 3} | {row * 5} |");
            }

            text.AppendLine();
        }

        if (i % 7 == 1)
        {
            text.AppendLine("Оценка: $$T = \\frac{n \\cdot s}{v} + \\sqrt{t_0^2 + \\sigma^2}$$").AppendLine();
        }

        text.AppendLine("- Перезапустить службу").AppendLine("- Очистить очередь").AppendLine("- Переустановить драйвер");
        return text.ToString();
    }

    private static int Unbuilt(MainWindow window) =>
        typeof(MainWindow).GetField("_unbuiltMessages", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) as int? ?? 0;

    private static object? Call(object target, string name, params object?[] args)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                     ?? throw new MissingMethodException(target.GetType().Name, name);
        return method.Invoke(target, args);
    }

    private static async Task Settle(int ms)
    {
        await Task.Delay(ms);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Промежутки между кадрами: чем дольше поток интерфейса занят, тем длиннее промежуток.</summary>
    private sealed class FrameGaps
    {
        private readonly Stopwatch _clock = new();
        private double _last;
        private bool _countOnly;
        private int _ticks;

        public double MaxGap { get; private set; }

        public int Over50 { get; private set; }

        public void Start()
        {
            _clock.Start();
            CompositionTarget.Rendering += OnRendering;
        }

        /// <summary>
        /// Считает пробуждения потока интерфейса, а не кадры: подписка на Rendering сама заказывала
        /// бы кадры и испортила бы замер простоя.
        /// </summary>
        public void CountOnly()
        {
            _countOnly = true;
            _clock.Start();
            ComponentDispatcher.ThreadIdle += OnIdle;
        }

        public int Stop()
        {
            CompositionTarget.Rendering -= OnRendering;
            ComponentDispatcher.ThreadIdle -= OnIdle;
            return _ticks;
        }

        private void OnIdle(object? sender, EventArgs e) => _ticks++;

        private void OnRendering(object? sender, EventArgs e)
        {
            if (_countOnly)
            {
                return;
            }

            var now = _clock.Elapsed.TotalMilliseconds;
            if (_last > 0)
            {
                var gap = now - _last;
                MaxGap = Math.Max(MaxGap, gap);
                if (gap > 50)
                {
                    Over50++;
                }
            }

            _last = now;
        }
    }
}
