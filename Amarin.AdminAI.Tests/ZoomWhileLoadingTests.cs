using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Лупа в первые секунды после открытия большого чата, пока лента ещё достраивается.
/// </summary>
/// <remarks>
/// Раньше фоновая дорисовка шла и во время жеста: каждая порция меняла высоты над видимой
/// областью, текст под курсором уезжал, а компенсация смещения и сама лупа перетягивали
/// прокрутку кадр за кадром. После жеста устаревший признак «лента у низа» бросал
/// приближенную ленту в конец.
/// </remarks>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ZoomWhileLoadingTests : IDisposable
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-zoomload-" + Guid.NewGuid().ToString("N"));

    public ZoomWhileLoadingTests(WpfFixture wpf) => _wpf = wpf;

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
    public async Task The_background_fill_waits_while_the_magnifier_moves_and_finishes_after()
    {
        var (builtDuringZoom, framesSeen, leftAtEnd) = await _wpf.Ui.Invoke(async () =>
        {
            var (window, viewer) = await OpenBigChat();
            try
            {
                var zoom = typeof(MainWindow).GetField("_chatZoom", Hidden)!.GetValue(window)!;
                var zoomBy = zoom.GetType().GetMethod("ZoomBy", Hidden)!;
                var busy = zoom.GetType().GetProperty("IsBusy")!;

                var unbuiltBefore = Unbuilt(window);
                var built = 0;
                var frames = 0;
                void OnFrame(object? sender, EventArgs e)
                {
                    if ((bool)busy.GetValue(zoom)!)
                    {
                        frames++;
                        built = Math.Max(built, unbuiltBefore - Unbuilt(window));
                    }
                }

                CompositionTarget.Rendering += OnFrame;
                zoomBy.Invoke(zoom, [120, new Point(viewer.ActualWidth / 2, viewer.ActualHeight / 2)]);
                zoomBy.Invoke(zoom, [120, new Point(viewer.ActualWidth / 2, viewer.ActualHeight / 2)]);
                while ((bool)busy.GetValue(zoom)!)
                {
                    await Task.Delay(10);
                }

                CompositionTarget.Rendering -= OnFrame;

                // После жеста лента достраивается до конца.
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (Unbuilt(window) > 0 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20);
                }

                return (built, frames, Unbuilt(window));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(framesSeen > 0, "жест лупы не дал ни одного кадра — тест ничего не проверил");
        Assert.Equal(0, builtDuringZoom);
        Assert.Equal(0, leftAtEnd);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_feed_read_mid_chat_stays_put_while_the_rest_is_built(bool magnified)
    {
        var (drift, atEnd) = await _wpf.Ui.Invoke(async () =>
        {
            var (window, viewer) = await OpenBigChat();
            try
            {
                // Уводим ленту от низа и приближаем — как человек, разглядывающий середину.
                viewer.ScrollToVerticalOffset(Math.Max(0, viewer.ScrollableHeight - (viewer.ViewportHeight * 3)));
                await Settle();

                var zoom = typeof(MainWindow).GetField("_chatZoom", Hidden)!.GetValue(window)!;
                var zoomBy = zoom.GetType().GetMethod("ZoomBy", Hidden)!;
                var busy = zoom.GetType().GetProperty("IsBusy")!;
                if (magnified)
                {
                    zoomBy.Invoke(zoom, [120, new Point(viewer.ActualWidth / 2, viewer.ActualHeight / 2)]);
                }

                while ((bool)busy.GetValue(zoom)!)
                {
                    await Task.Delay(10);
                }

                await Settle();
                var hosts = (List<ChatMessageHost>)typeof(MainWindow).GetField("_messageHosts", Hidden)!.GetValue(window)!;
                var panel = (Panel)window.FindName("MessagesPanel")!;
                var watched = hosts.First(host => host.TranslatePoint(default, viewer).Y >= 0);
                var before = watched.TranslatePoint(default, viewer).Y;

                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (Unbuilt(window) > 0 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20);
                }

                await Settle();
                var after = watched.TranslatePoint(default, viewer).Y;
                return (Math.Abs(after - before), viewer.VerticalOffset >= viewer.ScrollableHeight - 1);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(drift <= 2, $"текст под глазами уехал на {drift:0.#} точки, пока достраивалась лента");
        Assert.False(atEnd, "приближенную ленту увело в конец");
    }

    // ───────────────────────── помощники ─────────────────────────

    private async Task<(MainWindow Window, ScrollViewer Viewer)> OpenBigChat()
    {
        var services = UiServices.Build(Path.Combine(_root, Guid.NewGuid().ToString("N")), "k", new HttpClientHandler());
        var session = new ChatSession { Id = "big", Title = "Big", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        for (var i = 0; i < 300; i++)
        {
            var user = i % 2 == 0;
            session.Messages.Add(new ChatDisplayMessage
            {
                Id = "m" + i,
                Role = user ? "user" : "assistant",
                CreatedAt = DateTime.Now.AddMinutes(i - 300),
                Status = AssistantStatus.Complete,
                ResolvedModelId = user ? null : "grok-4-6",
                Text = user ? "Вопрос номер " + i : Answer(i)
            });
        }

        services.ChatStore.Save(session);
        services.ChatStore.Flush();

        var window = new MainWindow
        {
            Width = 1100,
            Height = 800,
            Left = -32000,
            Top = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.AttachServices(services);
        window.Show();
        await Settle();

        typeof(MainWindow).GetMethod("OpenChat", Hidden)!.Invoke(window, ["big"]);
        window.UpdateLayout();
        var viewer = (ScrollViewer)window.FindName("ChatScrollViewer")!;
        return (window, viewer);
    }

    private static string Answer(int i)
    {
        var text = new StringBuilder("Ответ номер ").Append(i).Append(". Абзац текста, который переносится на несколько строк в ленте шириной около тысячи точек.\n\n");
        if (i % 3 == 1)
        {
            text.Append("```powershell\n");
            for (var line = 0; line < 8; line++)
            {
                text.Append("Get-Service -Name Spooler | Select-Object Status\n");
            }

            text.Append("```\n");
        }

        return text.ToString();
    }

    private static int Unbuilt(MainWindow window) =>
        (int)typeof(MainWindow).GetField("_unbuiltMessages", Hidden)!.GetValue(window)!;

    private static async Task Settle()
    {
        await Task.Delay(50);
        await Dispatcher.Yield(DispatcherPriority.Background);
    }
}
