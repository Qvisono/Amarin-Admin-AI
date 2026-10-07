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
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Изменение размера окна с большим чатом. До 1.30.0 каждый шаг перетаскивания края
/// перекладывал все построенные сообщения — на чате в 1200 сообщений 1,7 секунды на шаг, — а
/// текст, который человек читал, уезжал: менялась высота всего, что над ним.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class TranscriptResizeTests : IDisposable
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int Messages = 300;

    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-resize-" + Guid.NewGuid().ToString("N"));

    public TranscriptResizeTests(WpfFixture wpf) => _wpf = wpf;

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
    public async Task Dragging_the_edge_lays_out_only_what_is_on_screen_and_the_reading_place_stays()
    {
        var outcome = await WithBigChat(1200, 760, async (window, hosts) =>
        {
            var viewer = (ScrollViewer)window.FindName("ChatScrollViewer")!;

            // Читаем середину: лента не у низа, и над глазом полтораста сообщений.
            viewer.ScrollToVerticalOffset(TopOf(hosts[Messages / 2]) + 40);
            window.UpdateLayout();
            await Pump();
            var reading = Reading(viewer, hosts);
            var before = ScreenTop(reading, viewer);

            var boxes = Documents(hosts);
            var laidOut = 0;
            foreach (var box in boxes)
            {
                box.SizeChanged += (_, e) => laidOut += e.WidthChanged ? 1 : 0;
            }

            var handle = new WindowInteropHelper(window).Handle;
            var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            var height = (int)Math.Round(window.ActualHeight * scale);
            var worst = 0;
            var drift = 0.0;
            var slowest = 0.0;
            _ = SendMessage(handle, WmEnterSizeMove, IntPtr.Zero, IntPtr.Zero);
            for (var step = 1; step <= 8; step++)
            {
                laidOut = 0;
                var watch = Stopwatch.StartNew();
                _ = SetWindowPos(handle, IntPtr.Zero, 0, 0, (int)Math.Round((1200 - (step * 35)) * scale), height, SwpNoMove | SwpNoZOrder | SwpNoActivate);
                window.UpdateLayout();
                slowest = Math.Max(slowest, watch.Elapsed.TotalMilliseconds);
                worst = Math.Max(worst, laidOut);
                drift = Math.Max(drift, Math.Abs(ScreenTop(reading, viewer) - before));
            }

            _ = SendMessage(handle, WmExitSizeMove, IntPtr.Zero, IntPtr.Zero);
            await Until(() => hosts.All(host => !host.IsFrozen));
            window.UpdateLayout();
            drift = Math.Max(drift, Math.Abs(ScreenTop(reading, viewer) - before));
            return (worst, all: boxes.Count, drift, stale: Stale(boxes), slowest);
        });

        Assert.True(outcome.worst <= 30, $"на шаге переложено {outcome.worst} документов из {outcome.all}");
        Assert.True(outcome.drift <= 2, $"читаемое сообщение уехало на {outcome.drift:0.#} точки");
        Assert.Equal(0, outcome.stale);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"resize step, slowest: {outcome.slowest:0.#} ms; documents per step: {outcome.worst}/{outcome.all}"));
    }

    /// <summary>
    /// Край боковой панели — тот же жест, что край окна, только окно размера не меняет. До 1.32.0
    /// заморозка слушала одно окно, и каждый шаг ручки перекладывал всю ленту: на большом чате
    /// программа приходила в себя по две-три секунды.
    /// </summary>
    [Fact]
    public async Task Dragging_the_sidebar_edge_lays_out_only_what_is_on_screen_and_the_reading_place_stays()
    {
        var outcome = await WithBigChat(1000, 760, async (window, hosts) =>
        {
            var viewer = (ScrollViewer)window.FindName("ChatScrollViewer")!;
            viewer.ScrollToVerticalOffset(TopOf(hosts[Messages / 2]) + 40);
            window.UpdateLayout();
            await Pump();
            var reading = Reading(viewer, hosts);
            var before = ScreenTop(reading, viewer);

            var boxes = Documents(hosts);
            var laidOut = 0;
            foreach (var box in boxes)
            {
                box.SizeChanged += (_, e) => laidOut += e.WidthChanged ? 1 : 0;
            }

            var grip = (Thumb)window.FindName("SidebarGrip")!;
            var worst = 0;
            var drift = 0.0;
            grip.RaiseEvent(new DragStartedEventArgs(0, 0));
            for (var step = 1; step <= 8; step++)
            {
                laidOut = 0;
                grip.RaiseEvent(new DragDeltaEventArgs(18, 0));
                window.UpdateLayout();
                worst = Math.Max(worst, laidOut);
                drift = Math.Max(drift, Math.Abs(ScreenTop(reading, viewer) - before));
            }

            grip.RaiseEvent(new DragCompletedEventArgs(0, 0, canceled: false));
            await Until(() => hosts.All(host => !host.IsFrozen));
            window.UpdateLayout();
            drift = Math.Max(drift, Math.Abs(ScreenTop(reading, viewer) - before));
            return (worst, all: boxes.Count, drift, stale: Stale(boxes));
        });

        Assert.True(outcome.worst <= 30, $"на шаге ручки переложено {outcome.worst} документов из {outcome.all}");
        Assert.True(outcome.drift <= 2, $"читаемое сообщение уехало на {outcome.drift:0.#} точки");
        Assert.Equal(0, outcome.stale);
    }

    [Fact]
    public async Task Collapsing_the_sidebar_lays_out_the_visible_part_first_and_the_rest_right_after()
    {
        var outcome = await WithBigChat(1000, 650, async (window, hosts) =>
        {
            var boxes = Documents(hosts);
            var laidOut = 0;
            foreach (var box in boxes)
            {
                box.SizeChanged += (_, e) => laidOut += e.WidthChanged ? 1 : 0;
            }

            var collapse = typeof(MainWindow).GetMethod("SetSidebarCollapsed", Hidden)!;
            try
            {
                collapse.Invoke(window, [true]);
                window.UpdateLayout();
                var first = laidOut;
                await Until(() => hosts.All(host => !host.IsFrozen));
                window.UpdateLayout();
                return (first, all: boxes.Count, after: laidOut, stale: Stale(boxes));
            }
            finally
            {
                collapse.Invoke(window, [false]);
            }
        });

        Assert.True(outcome.first <= 30, $"сворачивание панели переложило сразу {outcome.first} документов из {outcome.all}");
        Assert.True(outcome.after > outcome.first, "остальное так и не переложилось");
        Assert.Equal(0, outcome.stale);
    }

    [Fact]
    public async Task Maximizing_lays_out_the_visible_part_first_and_the_rest_right_after()
    {
        var outcome = await WithBigChat(900, 650, async (window, hosts) =>
        {
            var boxes = Documents(hosts);
            var laidOut = 0;
            foreach (var box in boxes)
            {
                box.SizeChanged += (_, e) => laidOut += e.WidthChanged ? 1 : 0;
            }

            try
            {
                window.WindowState = WindowState.Maximized;
                window.UpdateLayout();
                var first = laidOut;
                await Until(() => hosts.All(host => !host.IsFrozen));
                window.UpdateLayout();
                return (first, all: boxes.Count, after: laidOut, stale: Stale(boxes));
            }
            finally
            {
                window.WindowState = WindowState.Normal;
            }
        });

        Assert.True(outcome.first <= 30, $"первый кадр разворота переложил {outcome.first} документов из {outcome.all}");
        Assert.True(outcome.after > outcome.first, "остальное так и не переложилось");
        Assert.Equal(0, outcome.stale);
    }

    [Fact]
    public void A_frozen_message_keeps_its_width_and_takes_the_new_one_once_released()
    {
        var (frozenWidth, thawedWidth) = _wpf.Ui.Invoke(() =>
        {
            var view = new Border { Child = new TextBlock { Text = string.Join(' ', Enumerable.Repeat("слово", 200)), TextWrapping = TextWrapping.Wrap } };
            var host = new ChatMessageHost { Message = new ChatDisplayMessage { Id = "m", Role = "assistant", Text = "x" }, Actions = null! };
            host.Fill(view);
            host.Measure(new Size(600, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, 600, host.DesiredSize.Height));

            host.Freeze();
            host.Measure(new Size(400, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, 400, host.DesiredSize.Height));
            var frozen = view.ActualWidth;

            host.Thaw();
            host.Measure(new Size(400, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, 400, host.DesiredSize.Height));
            return (frozen, view.ActualWidth);
        });

        Assert.Equal(600, frozenWidth, 1);
        Assert.Equal(400, thawedWidth, 1);
    }

    private async Task<T> WithBigChat<T>(double width, double height, Func<MainWindow, List<ChatMessageHost>, Task<T>> body)
    {
        return await _wpf.Ui.Invoke(async () =>
        {
            var services = UiServices.Build(_root, "k", new HttpClientHandler());
            services.Settings.AutoCheckUpdates = false;
            var session = BigChat();
            services.ChatStore.Save(session);
            services.ChatStore.Flush();

            var window = new MainWindow
            {
                Width = width,
                Height = height,
                Left = -32000,
                Top = 0,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.AttachServices(services);
            window.Show();
            try
            {
                typeof(MainWindow).GetMethod("OpenChat", Hidden)!.Invoke(window, [session.Id]);
                var hosts = (List<ChatMessageHost>)typeof(MainWindow).GetField("_messageHosts", Hidden)!.GetValue(window)!;
                await Until(() => hosts.All(host => host.IsMaterialized), seconds: 60);
                window.UpdateLayout();
                return await body(window, hosts);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static ChatSession BigChat()
    {
        var session = new ChatSession { Id = "resize-" + Guid.NewGuid().ToString("N")[..8], Title = "Resize", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        for (var i = 0; i < Messages; i++)
        {
            var user = i % 2 == 0;
            var text = new StringBuilder();
            if (user)
            {
                text.Append("Вопрос номер ").Append(i).Append(": почему служба не стартует после обновления?");
            }
            else
            {
                text.AppendLine("## Что я нашёл").AppendLine();
                text.AppendLine("Служба останавливается из-за повреждённого драйвера. Ниже шаги и пояснения, почему каждый из них нужен, " +
                                "и что проверить после перезапуска, чтобы ошибка не вернулась.").AppendLine();
                text.AppendLine("- Перезапустить службу").AppendLine("- Очистить очередь").AppendLine("- Переустановить драйвер");
            }

            session.Messages.Add(new ChatDisplayMessage
            {
                Id = "m" + i.ToString(CultureInfo.InvariantCulture),
                Role = user ? "user" : "assistant",
                CreatedAt = DateTime.Now.AddMinutes(i - Messages),
                Text = text.ToString(),
                Status = AssistantStatus.Complete,
                ResolvedModelId = user ? null : "grok-4-6"
            });
        }

        return session;
    }

    /// <summary>Сообщение, на котором стоит верх видимого: его начало и держит место чтения.</summary>
    private static ChatMessageHost Reading(ScrollViewer viewer, List<ChatMessageHost> hosts)
    {
        var top = viewer.VerticalOffset;
        return hosts.Last(host => TopOf(host) <= top);
    }

    private static double TopOf(ChatMessageHost host) => VisualTreeHelper.GetOffset(host).Y;

    private static double ScreenTop(ChatMessageHost host, ScrollViewer viewer) => host.TranslatePoint(default, viewer).Y;

    /// <summary>Документы сообщений, которые растягиваются по ширине ленты.</summary>
    private static List<RichTextBox> Documents(List<ChatMessageHost> hosts) =>
        hosts.SelectMany(host => Descendants<RichTextBox>(host)).Where(box => double.IsNaN(box.Width)).ToList();

    /// <summary>Документы, чья ширина страницы так и не догнала ширину поля.</summary>
    private static int Stale(List<RichTextBox> boxes) =>
        boxes.Count(box => !double.IsNaN(box.Document.PageWidth) && Math.Abs(box.Document.PageWidth - box.ActualWidth) > 1);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is T match)
            {
                yield return match;
            }

            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
            {
                stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    private static async Task Pump()
    {
        await Task.Delay(50);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static async Task Until(Func<bool> condition, int seconds = 20)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            await Task.Delay(15);
        }

        Assert.True(condition(), "не дождались");
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
}
