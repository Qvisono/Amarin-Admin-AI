using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Замер скорости: запуск окна, открытие большого чата, самые долгие остановки кадров, память и
/// ЦП в простое. Не утверждение, а таблица для сравнения «до/после».
/// </summary>
/// <remarks>
/// Работает только при <c>AMARIN_PERF_BENCH=&lt;файл&gt;</c> (строки дописываются в конец файла,
/// <c>AMARIN_PERF_LABEL</c> подписывает прогон); обычный прогон сразу выходит. Числа имеют смысл
/// в отдельном процессе и сборке Release: в общем прогоне код уже прогрет соседними тестами.
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
            var results = new List<(string Name, double Value, string Unit)>();
            for (var i = 0; i < 3; i++)
            {
                var (construct, attach, firstFrame) = await StartWindow(i);
                results.Add(($"start#{i + 1} ctor", construct, "ms"));
                results.Add(($"start#{i + 1} attach", attach, "ms"));
                results.Add(($"start#{i + 1} first frame", firstFrame, "ms"));
            }

            results.AddRange(await OpenBigChat());
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
        var services = UiServices.Build(Path.Combine(_root, "start" + run), "k", new HttpClientHandler());

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

    private async Task<List<(string, double, string)>> OpenBigChat()
    {
        var results = new List<(string, double, string)>();
        var services = UiServices.Build(Path.Combine(_root, "chat"), "k", new HttpClientHandler());
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

        window.Close();
        await Settle(200);
        return results;
    }

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
