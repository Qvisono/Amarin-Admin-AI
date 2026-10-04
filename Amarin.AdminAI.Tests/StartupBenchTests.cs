using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Холодный запуск окна: от процесса, где ещё не было ни WPF, ни кода программы, до первого
/// кадра — по шагам, как их проходит <c>Program.RunWpf</c>. Не утверждение, а строка таблицы для
/// сравнения версий.
/// </summary>
/// <remarks>
/// Работает только при <c>AMARIN_STARTUP_BENCH=&lt;файл&gt;</c> (строка дописывается в конец,
/// <c>AMARIN_PERF_LABEL</c> подписывает прогон) и <c>AMARIN_STARTUP_DATA=&lt;папка профиля&gt;</c>;
/// с <c>AMARIN_STARTUP_PREPARE=1</c> вместо замера кладёт в папку 300 чатов в 15 папках. WPF
/// допускает одно <c>Application</c> на процесс, а прогретый соседями код испортил бы замер,
/// поэтому его гоняют отдельным процессом с фильтром на этот класс (Release, на каждый прогон —
/// свежая копия папки); в общем прогоне он сразу выходит. Окно на время замера видно на экране.
/// Метки оконных тестов у класса нет: общего потока интерфейса он не берёт, а без переменной
/// окружения не делает ничего — и в CI тоже.
/// </remarks>
public sealed class StartupBenchTests
{
    private const int WmEraseBkgnd = 0x0014;

    [Fact]
    public void ColdStart()
    {
        var output = Environment.GetEnvironmentVariable("AMARIN_STARTUP_BENCH");
        var data = Environment.GetEnvironmentVariable("AMARIN_STARTUP_DATA");
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(data) || Application.Current is not null)
        {
            return;
        }

        if (Environment.GetEnvironmentVariable("AMARIN_STARTUP_PREPARE") == "1")
        {
            Prepare(data);
            return;
        }

        var marks = new List<(string Name, double Wall, double Cpu)>();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Run(data, marks);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "окно не запустилось за две минуты");
        if (error is not null)
        {
            throw error;
        }

        var label = Environment.GetEnvironmentVariable("AMARIN_PERF_LABEL") ?? "run";
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"| {label} |");
        foreach (var (_, wall, cpu) in marks)
        {
            text.Append(CultureInfo.InvariantCulture, $" {wall:0} / {cpu:0} |");
        }

        text.AppendLine();
        if (!File.Exists(output))
        {
            text.Insert(0, "| run | " + string.Join(" | ", marks.Select(mark => mark.Name + " ms (wall / UI cpu)")) + " |" + Environment.NewLine);
        }

        File.AppendAllText(output, text.ToString());
    }

    /// <summary>
    /// Шаги запуска с отметками: каждая — время от начала и время процессора потока окна.
    /// </summary>
    private static void Run(string data, List<(string, double, double)> marks)
    {
        var watch = Stopwatch.StartNew();
        var cpuStart = ThreadCpuMs();
        void Mark(string name) => marks.Add((name, watch.Elapsed.TotalMilliseconds, ThreadCpuMs() - cpuStart));

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var settingsStore = new AppSettingsStore(data);
        var settings = settingsStore.Load();
        ThemeManager.Initialize(app, settings.Theme);
        LanguageManager.Initialize(app, settings.LanguageCode);
        ThemeManager.Apply(settings.Theme);
        LanguageManager.Apply(settings.LanguageCode);
        Mark("app+theme");

        var services = UiServices.Build(data, "k", new Offline());
        Mark("services");

        var window = new MainWindow();
        Mark("ctor");

        window.AttachServices(services);
        Mark("attach");

        var handle = new WindowInteropHelper(window).EnsureHandle();
        var shown = false;
        HwndSource.FromHwnd(handle).AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool _) =>
        {
            if (msg == WmEraseBkgnd && !shown)
            {
                shown = true;
                Mark("visible");
            }

            return IntPtr.Zero;
        });

        var rendered = false;
        void OnFrame(object? sender, EventArgs e)
        {
            if (!rendered)
            {
                rendered = true;
                Mark("first frame");
                CompositionTarget.Rendering -= OnFrame;
            }
        }

        CompositionTarget.Rendering += OnFrame;
        var laidOut = false;
        window.LayoutUpdated += (_, _) =>
        {
            if (!laidOut)
            {
                laidOut = true;
                Mark("first layout");
            }
        };
        window.Loaded += (_, _) => Mark("loaded done");
        window.ContentRendered += (_, _) =>
        {
            Mark("content rendered");
            window.Dispatcher.BeginInvoke(() =>
            {
                window.Close();
                app.Shutdown();
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };

        app.Run(window);
    }

    private static void Prepare(string data)
    {
        Directory.CreateDirectory(data);
        var services = UiServices.Build(data, "k", new Offline());
        services.Settings.AutoCheckUpdates = false;
        services.SettingsStore.Save(services.Settings);

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
            session.Messages.Add(new ChatDisplayMessage { Role = "user", Text = "Вопрос " + i.ToString(CultureInfo.InvariantCulture) });
            session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Text = "Ответ " + i.ToString(CultureInfo.InvariantCulture) });
            services.ChatStore.Save(session);
            ids.Add(session.Id);
        }

        services.ChatStore.Flush();
        for (var f = 0; f < 15; f++)
        {
            var folder = services.Organizer.CreateFolder("Папка " + f.ToString(CultureInfo.InvariantCulture));
            services.Organizer.MoveToFolder(ids.Skip(f * 12).Take(12).ToList(), folder.Id);
        }

        services.Dispose();
    }

    private static double ThreadCpuMs()
    {
        _ = GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user);
        return (kernel + user) / 10_000.0;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    /// <summary>Сеть замеру не нужна: каталог моделей и прочие запросы получают отказ сразу.</summary>
    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
