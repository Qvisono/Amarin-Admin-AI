using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Перетаскивание чатов боковой панели по разделам на живом окне.
/// </summary>
/// <remarks>
/// Жест ведётся через <c>ChatListDrag.Press</c>/<c>MoveTo</c>/<c>Release</c> в координатах списка:
/// положение мыши в поднятом <c>RaiseEvent</c> событии берётся у настоящего курсора. Окно стоит за
/// краем экрана — ему нужна раскладка, чтобы у строк были места.
/// </remarks>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ChatListDragTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-drag-" + Guid.NewGuid().ToString("N"));

    public ChatListDragTests(WpfFixture wpf) => _wpf = wpf;

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
    public void A_chat_dropped_on_a_folder_moves_into_it()
    {
        var (folder, placed) = With((window, services, drag) =>
        {
            var created = services.Organizer.CreateFolder("Work");
            Refresh(window);

            Drag(window, drag, Row(window, "a"), Center(window, FolderHeader(window)));
            return (created.Id, services.Organizer.PlacementOf("a").FolderId);
        });

        Assert.Equal(folder, placed);
    }

    [Fact]
    public void A_pinned_chat_dropped_on_a_folder_is_unpinned_or_it_would_stay_on_top()
    {
        var (placed, pinned) = With((window, services, drag) =>
        {
            services.ChatStore.SetPinned("a", true);
            var folder = services.Organizer.CreateFolder("Work");
            Refresh(window);

            Drag(window, drag, Row(window, "a"), Center(window, FolderHeader(window)));
            return (services.Organizer.PlacementOf("a").FolderId == folder.Id, IsPinned(services, "a"));
        });

        Assert.True(placed);
        Assert.False(pinned);
    }

    [Fact]
    public void A_folder_chat_dropped_on_the_pinned_header_is_pinned_and_keeps_its_folder()
    {
        var (pinned, folderKept) = With((window, services, drag) =>
        {
            services.ChatStore.SetPinned("b", true);
            var folder = services.Organizer.CreateFolder("Work");
            services.Organizer.MoveToFolder(["a"], folder.Id);
            Refresh(window);

            var header = Panel(window).Children.OfType<TextBlock>()
                .First(block => ChatRowState.GetDropTarget(block) is { Kind: ChatDropKind.Pinned });
            Drag(window, drag, Row(window, "a"), Center(window, header));
            return (IsPinned(services, "a"), services.Organizer.PlacementOf("a").FolderId == folder.Id);
        });

        Assert.True(pinned);
        Assert.True(folderKept);
    }

    [Fact]
    public void With_nothing_pinned_a_pin_zone_appears_on_top_and_takes_the_drop()
    {
        var (zoneShown, target, pinned, layerAfter) = With((window, services, drag) =>
        {
            var panel = Panel(window);
            var scroller = (ScrollViewer)window.FindName("SideBarScrollViewer")!;
            var layer = (Canvas)window.FindName("ChatDragLayer")!;
            var row = Row(window, "a");

            drag.Press(row, Center(window, row));
            drag.MoveTo(Center(window, row) + new Vector(0, 12));
            var shown = layer.Visibility == Visibility.Visible;

            // Зона лежит поверх верха колонки: в координатах списка это выше его начала.
            var listTop = panel.TranslatePoint(default, scroller).Y;
            drag.MoveTo(new Point(40, 20 - listTop));
            var where = drag.Target;
            drag.Release();
            return (shown, where, IsPinned(services, "a"), layer.Visibility);
        });

        Assert.True(zoneShown);
        Assert.Equal(ChatDropTarget.Pinned, target);
        Assert.True(pinned);
        Assert.Equal(Visibility.Collapsed, layerAfter);
    }

    [Fact]
    public void A_small_wobble_stays_a_click()
    {
        var (dragged, dragging) = With((window, _, drag) =>
        {
            var row = Row(window, "a");
            drag.Press(row, Center(window, row));
            drag.MoveTo(Center(window, row) + new Vector(1, 1));
            var during = drag.IsDragging;
            return (drag.Release(), during);
        });

        Assert.False(dragged);
        Assert.False(dragging);
    }

    [Fact]
    public void Escape_cancels_the_drag_and_leaves_everything_as_it_was()
    {
        var (cancelled, released, folder, opacity) = With((window, services, drag) =>
        {
            services.Organizer.CreateFolder("Work");
            Refresh(window);
            var row = Row(window, "a");

            drag.Press(row, Center(window, row));
            drag.MoveTo(Center(window, FolderHeader(window)));
            var wasCancelled = drag.Cancel();
            return (wasCancelled, drag.Release(), services.Organizer.PlacementOf("a").FolderId, row.Opacity);
        });

        Assert.True(cancelled);
        Assert.False(released);
        Assert.Null(folder);
        Assert.Equal(1.0, opacity);
    }

    [Fact]
    public void A_selection_is_dragged_together()
    {
        var (a, b) = With((window, services, drag) =>
        {
            var folder = services.Organizer.CreateFolder("Work");
            Refresh(window);
            Call(window, "ToggleChatSelection", "a");
            Call(window, "ToggleChatSelection", "b");

            Drag(window, drag, Row(window, "a"), Center(window, FolderHeader(window)));
            return (services.Organizer.PlacementOf("a").FolderId == folder.Id, services.Organizer.PlacementOf("b").FolderId == folder.Id);
        });

        Assert.True(a);
        Assert.True(b);
    }

    [Fact]
    public void The_section_a_chat_already_lies_in_is_not_offered()
    {
        var target = With((window, services, drag) =>
        {
            var folder = services.Organizer.CreateFolder("Work");
            services.Organizer.MoveToFolder(["a"], folder.Id);
            Refresh(window);
            var row = Row(window, "a");

            drag.Press(row, Center(window, row));
            drag.MoveTo(Center(window, FolderHeader(window)));
            var where = drag.Target;
            drag.Cancel();
            return where;
        });

        Assert.Null(target);
    }

    [Fact]
    public void Search_results_offer_no_sections()
    {
        var targets = With((window, _, _) =>
        {
            ((TextBox)window.FindName("SearchBox")!).Text = "Chat";
            Refresh(window);
            return Panel(window).Children.OfType<FrameworkElement>().Select(ChatRowState.GetDropTarget).ToList();
        });

        Assert.All(targets, Assert.Null);
    }

    [Fact]
    public void The_list_is_not_rebuilt_under_a_drag_but_right_after_it()
    {
        var (sameRow, pending, appeared) = With((window, services, drag) =>
        {
            var row = Row(window, "a");
            drag.Press(row, Center(window, row));
            drag.MoveTo(Center(window, row) + new Vector(0, 12));

            services.ChatStore.Save(new ChatSession { Id = "late", Title = "Late", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now });
            services.ChatStore.Flush();
            Refresh(window);
            var kept = ReferenceEquals(Row(window, "a"), row);
            var waiting = drag.RefreshPending;

            drag.Cancel();
            return (kept, waiting, Panel(window).Children.OfType<Button>().Any(button => button.Tag as string == "late"));
        });

        Assert.True(sameRow);
        Assert.True(pending);
        Assert.True(appeared);
    }

    // ───────────────────────── помощники ─────────────────────────

    private T With<T>(Func<MainWindow, AppServices, ChatListDrag, T> body) => _wpf.Ui.Invoke(() =>
    {
        var services = UiServices.Build(_root, "k", new HttpClientHandler());
        foreach (var id in new[] { "a", "b", "c" })
        {
            services.ChatStore.Save(new ChatSession { Id = id, Title = "Chat " + id, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now });
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
        try
        {
            var drag = (ChatListDrag)typeof(MainWindow)
                .GetField("_chatDrag", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
            drag.RealMouse = false;
            Refresh(window);
            return body(window, services, drag);
        }
        finally
        {
            window.Close();
        }
    });

    private static void Refresh(MainWindow window)
    {
        Call(window, "RefreshChatList");
        window.UpdateLayout();
    }

    private static void Drag(MainWindow window, ChatListDrag drag, Button row, Point to)
    {
        drag.Press(row, Center(window, row));
        drag.MoveTo(Center(window, row) + new Vector(0, 12));
        drag.MoveTo(to);
        Assert.True(drag.Release());
        window.UpdateLayout();
    }

    private static Panel Panel(MainWindow window) => (Panel)window.FindName("ChatListPanel")!;

    private static Button Row(MainWindow window, string id) =>
        Panel(window).Children.OfType<Button>().Single(button => button.Tag as string == id);

    private static Button FolderHeader(MainWindow window) =>
        Panel(window).Children.OfType<Button>().Single(button => button.Tag is ChatFolder);

    private static Point Center(MainWindow window, FrameworkElement element) =>
        element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), Panel(window));

    private static bool IsPinned(AppServices services, string id) =>
        services.ChatStore.List().Single(entry => entry.Id == id).IsPinned;

    private static object? Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(window, args);
}
