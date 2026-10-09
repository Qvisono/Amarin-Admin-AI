using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// «Отправить | Переместить» в меню чата (1.33.0): пункты есть всегда, без других профилей
/// приглушены, а перенос убирает чат из списка этого профиля и кладёт его в тот.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ChatTransferUiTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-transfer-ui-" + Guid.NewGuid().ToString("N"));

    public ChatTransferUiTests(WpfFixture wpf) => _wpf = wpf;

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
    public void The_chat_menu_offers_send_and_move_dimmed_while_there_is_nowhere_to_go()
    {
        var (alone, withOther) = With((window, services) =>
        {
            var aloneItems = MenuItems(window, "a");
            services.ProfileRegistry.Profiles.Add(new UserProfile { Id = "work", Name = "Работа" });
            var items = MenuItems(window, "a");
            return (aloneItems, items);
        });

        var send = Loc.Get("S.ChatList.SendTo") + "…";
        var move = Loc.Get("S.ChatList.MoveTo") + "…";
        Assert.False(alone.Single(item => item.Header == send).Enabled);
        Assert.False(alone.Single(item => item.Header == move).Enabled);
        Assert.True(withOther.Single(item => item.Header == send).Enabled);
        Assert.True(withOther.Single(item => item.Header == move).Enabled);

        // Перед «Удалить»: уход в другой профиль — не последнее, что делают с чатом.
        var headers = withOther.Select(item => item.Header).ToList();
        Assert.True(headers.IndexOf(move) < headers.IndexOf(Loc.Get("S.Common.Delete")));
    }

    [Fact]
    public void Moving_takes_the_chat_off_this_list_and_puts_it_into_the_other_profile()
    {
        var (rowsLeft, there, note) = With((window, services) =>
        {
            services.ProfileRegistry.Profiles.Add(new UserProfile { Id = "work", Name = "Работа" });
            Directory.CreateDirectory(services.Profiles.DataRootFor("work"));

            Pump(window.TransferChatsAsync(["b"], [], "work", move: true));
            Refresh(window);
            return (
                Rows(window),
                new ChatStore(services.Profiles.DataRootFor("work")).TryLoad("b"),
                (string?)typeof(MainWindow).GetField("_statusNote", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        });

        Assert.DoesNotContain("b", rowsLeft);
        Assert.Contains("a", rowsLeft);
        Assert.NotNull(there);
        Assert.Equal("Chat b", there.Title);

        // Удачный перенос молчит, как отправка ключа: так попросил человек.
        Assert.Null(note);
    }

    [Fact]
    public void Moving_the_open_chat_takes_the_unsent_text_along_and_clears_the_field()
    {
        var (field, draft) = With((window, services) =>
        {
            services.ProfileRegistry.Profiles.Add(new UserProfile { Id = "work", Name = "Работа" });
            var root = services.Profiles.DataRootFor("work");
            Directory.CreateDirectory(root);
            var open = (ChatSession)typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            open.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u", Text = "привет" });
            services.ChatStore.Save(open);
            services.ChatStore.Flush();
            window.MessageTextBox.Text = "недописанное";

            Pump(window.TransferChatsAsync([open.Id], [], "work", move: true));
            return (window.MessageTextBox.Text, new DraftStore(root, () => false).TryLoad(open.Id)?.Text);
        });

        Assert.Equal("", field);
        Assert.Equal("недописанное", draft);
    }

    [Fact]
    public void A_chat_that_is_answering_cannot_be_sent_or_moved()
    {
        var items = With((window, services) =>
        {
            services.ProfileRegistry.Profiles.Add(new UserProfile { Id = "work", Name = "Работа" });
            var session = services.ChatStore.TryLoad("a") ?? throw new InvalidOperationException();
            var start = window.Turns.TryStart(session, TurnKind.Send, DateTime.Now);
            try
            {
                return MenuItems(window, "a");
            }
            finally
            {
                if (start.Turn is { } turn)
                {
                    window.Turns.Finish(turn);
                }
            }
        });

        Assert.False(items.Single(item => item.Header == Loc.Get("S.ChatList.MoveTo") + "…").Enabled);
        Assert.False(items.Single(item => item.Header == Loc.Get("S.ChatList.SendTo") + "…").Enabled);
    }

    private static void Pump(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void Sending_leaves_the_chat_here_and_puts_a_copy_there()
    {
        var (rowsLeft, copies) = With((window, services) =>
        {
            services.ProfileRegistry.Profiles.Add(new UserProfile { Id = "home", Name = "Дом" });
            Directory.CreateDirectory(services.Profiles.DataRootFor("home"));

            Pump(window.TransferChatsAsync(["a", "c"], [], "home", move: false));
            Refresh(window);
            return (Rows(window), new ChatStore(services.Profiles.DataRootFor("home")).List().Select(entry => entry.Title).ToList());
        });

        Assert.Contains("a", rowsLeft);
        Assert.Contains("c", rowsLeft);
        Assert.Equal(["Chat a", "Chat c"], copies.Order(StringComparer.Ordinal));
    }

    private T With<T>(Func<MainWindow, AppServices, T> body) => _wpf.Ui.Invoke(() =>
    {
        var services = UiServices.Build(Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]), "k", new HttpClientHandler());
        var now = DateTime.Now;
        var minutes = 0;
        foreach (var id in new[] { "a", "b", "c" })
        {
            services.ChatStore.Save(new ChatSession { Id = id, Title = "Chat " + id, CreatedAt = now, UpdatedAt = now.AddMinutes(-minutes++) });
        }

        services.ChatStore.Flush();
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
        window.Show();
        Refresh(window);
        try
        {
            return body(window, services);
        }
        finally
        {
            CloseMenus();
            window.Close();
        }
    });

    /// <summary>Открывает меню строки и снимает его пункты: подпись и активен ли.</summary>
    private static List<(string Header, bool Enabled)> MenuItems(MainWindow window, string id)
    {
        var row = Panel(window).Children.OfType<Button>().First(button => button.Tag as string == id);
        typeof(MainWindow)
            .GetMethod("OpenChatActionsMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [row, id, PlacementMode.Bottom]);
        var items = OpenMenus()
            .SelectMany(menu => menu.Items.OfType<MenuItem>())
            .Select(item => (Convert.ToString(item.Header) ?? "", item.IsEnabled))
            .ToList();
        CloseMenus();
        return items;
    }

    private static void Refresh(MainWindow window)
    {
        typeof(MainWindow).GetMethod("RefreshChatList", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, []);
        window.UpdateLayout();
    }

    private static Panel Panel(MainWindow window) => (Panel)window.FindName("ChatListPanel")!;

    private static List<string> Rows(MainWindow window) =>
        [.. Panel(window).Children.OfType<Button>().Select(button => button.Tag).OfType<string>()];

    private static List<ContextMenu> OpenMenus() =>
        [.. PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>()
            .Select(source => source.RootVisual)
            .OfType<DependencyObject>()
            .SelectMany(root => root is ContextMenu own ? [own] : Descendants<ContextMenu>(root))
            .Where(menu => menu.IsOpen)];

    private static void CloseMenus()
    {
        foreach (var menu in OpenMenus())
        {
            menu.IsOpen = false;
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
