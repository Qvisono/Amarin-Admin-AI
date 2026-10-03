using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Боковая панель обновляется точечно, а папки раскрываются плавно. До 1.30.0 щелчок по папке
/// сносил список целиком и строил каждую строку заново — на трёхстах чатах 120 мс без кадра.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ChatListPatchUiTests : IDisposable
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-list-patch-" + Guid.NewGuid().ToString("N"));

    public ChatListPatchUiTests(WpfFixture wpf) => _wpf = wpf;

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
    public void Opening_a_folder_leaves_every_other_row_in_place()
    {
        var (same, total, opened) = With(shown: false, (window, panel, folders) =>
        {
            var before = Rows(panel);
            Click(Header(panel, folders[0]));
            var after = Rows(panel);

            // Строки других папок и общего списка — те же объекты, а не построенные заново.
            var kept = before.Count(pair => after.TryGetValue(pair.Key, out var row) && ReferenceEquals(row, pair.Value));
            return (kept, before.Count, after.ContainsKey("a0"));
        });

        Assert.True(opened, "строки раскрытой папки не появились");
        Assert.Equal(total, same);
    }

    [Fact]
    public void A_renamed_chat_is_the_only_row_rebuilt()
    {
        var (rebuilt, total) = With(shown: false, (window, panel, folders) =>
        {
            var before = Rows(panel);
            var services = (AppServices)typeof(MainWindow).GetField("_services", Hidden)!.GetValue(window)!;
            services.ChatStore.Rename("loose3", "Новое имя");
            Call(window, "RefreshChatList");
            var after = Rows(panel);
            return (before.Count(pair => !ReferenceEquals(after.GetValueOrDefault(pair.Key), pair.Value)), before.Count);
        });

        // Папки свёрнуты: на виду шесть чатов без папки, и заново построен ровно один.
        Assert.Equal(1, rebuilt);
        Assert.Equal(6, total);
    }

    [Fact]
    public async Task A_folder_grows_out_of_its_card_without_a_jump_and_folds_back_into_it()
    {
        var heights = await WithShown(async (window, panel, folders) =>
        {
            var closed = panel.ActualHeight;

            Click(Header(panel, folders[1]));
            panel.UpdateLayout();
            var openingFirstFrame = panel.ActualHeight;
            await Task.Delay(500);
            panel.UpdateLayout();
            var open = panel.ActualHeight;

            Click(Header(panel, folders[1]));
            panel.UpdateLayout();
            var closingFirstFrame = panel.ActualHeight;
            var leavingDuringFold = panel.Children.OfType<Button>().Count(button => button.Tag is null);
            await Task.Delay(500);
            panel.UpdateLayout();
            return (closed, openingFirstFrame, open, closingFirstFrame, leavingDuringFold, panel.ActualHeight,
                panel.Children.OfType<Button>().Count(button => button.Tag is null));
        });

        Assert.InRange(heights.openingFirstFrame, heights.closed - 1, heights.closed + 1);
        Assert.True(heights.open > heights.closed + 50, $"папка не раскрылась: {heights.closed} -> {heights.open}");
        Assert.InRange(heights.closingFirstFrame, heights.open - 1, heights.open + 1);
        Assert.Equal(3, heights.leavingDuringFold);
        Assert.InRange(heights.Item6, heights.closed - 1, heights.closed + 1);
        Assert.Equal(0, heights.Item7);
    }

    [Fact]
    public void Without_animations_a_folder_folds_at_once()
    {
        var previous = UiMotion.Enabled;
        try
        {
            var (rows, leaving, stillThere) = With(shown: true, (window, panel, folders) =>
            {
                // После AttachServices: окно ставит UiMotion.Enabled из настроек оформления.
                UiMotion.Enabled = false;
                Click(Header(panel, folders[2]));
                var during = Rows(panel).Keys.Count(key => key.StartsWith('c'));
                Click(Header(panel, folders[2]));
                return (during, panel.Children.OfType<Button>().Count(button => button.Tag is null), Rows(panel).ContainsKey("c0"));
            });

            Assert.Equal(3, rows);
            Assert.Equal(0, leaving);
            Assert.False(stillThere);
        }
        finally
        {
            UiMotion.Enabled = previous;
        }
    }

    private T With<T>(bool shown, Func<MainWindow, Panel, List<ChatFolder>, T> body) => _wpf.Ui.Invoke(() =>
    {
        var (window, panel, folders) = Open(shown);
        try
        {
            return body(window, panel, folders);
        }
        finally
        {
            window.Close();
        }
    });

    private Task<T> WithShown<T>(Func<MainWindow, Panel, List<ChatFolder>, Task<T>> body) => _wpf.Ui.Invoke(async () =>
    {
        var (window, panel, folders) = Open(shown: true);
        try
        {
            return await body(window, panel, folders);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Три свёрнутые папки по три чата и шесть чатов без папки.</summary>
    private (MainWindow Window, Panel Panel, List<ChatFolder> Folders) Open(bool shown)
    {
        var services = UiServices.Build(_root, "k", new HttpClientHandler());
        services.Settings.AutoCheckUpdates = false;
        var folders = new List<ChatFolder>();
        var prefixes = new[] { "a", "b", "c" };
        for (var f = 0; f < 3; f++)
        {
            var ids = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                ids.Add(prefixes[f] + i);
                Save(services, prefixes[f] + i);
            }

            var folder = services.Organizer.CreateFolder("Папка " + f);
            services.Organizer.MoveToFolder(ids, folder.Id);
            services.Organizer.SetCollapsed(folder.Id, true);
            folders.Add(folder);
        }

        for (var i = 0; i < 6; i++)
        {
            Save(services, "loose" + i);
        }

        var window = new MainWindow
        {
            Width = 1100,
            Height = 900,
            Left = -32000,
            Top = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.AttachServices(services);
        if (shown)
        {
            window.Show();
        }

        Call(window, "RefreshChatList");
        var panel = (Panel)window.FindName("ChatListPanel")!;
        panel.UpdateLayout();
        return (window, panel, folders);
    }

    private static void Save(AppServices services, string id)
    {
        services.ChatStore.Save(new ChatSession { Id = id, Title = id, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now });
        services.ChatStore.Flush();
    }

    private static Dictionary<string, Button> Rows(Panel panel) =>
        panel.Children.OfType<Button>().Where(button => button.Tag is string).ToDictionary(button => (string)button.Tag, StringComparer.Ordinal);

    private static Button Header(Panel panel, ChatFolder folder) =>
        panel.Children.OfType<Button>().Single(button => button.Tag is ChatFolder current && current.Id == folder.Id);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static object? Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow)
            .GetMethods(Hidden)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(window, args);
}
