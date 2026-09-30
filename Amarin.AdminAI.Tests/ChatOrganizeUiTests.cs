using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Папки, теги, выбор и ширина боковой панели (D5) на живом окне со службами.
/// </summary>
/// <remarks>Окно своё, не показывается и закрывается в finally — по образцу <see cref="VariantUiTests"/>.</remarks>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ChatOrganizeUiTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-organize-ui-" + Guid.NewGuid().ToString("N"));

    public ChatOrganizeUiTests(WpfFixture wpf) => _wpf = wpf;

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

    private static object? Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(window, args);

    private static T Find<T>(MainWindow window, string name) where T : class => (T)window.FindName(name)!;

    private static void Save(AppServices services, string id, string title)
    {
        services.ChatStore.Save(new ChatSession { Id = id, Title = title, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now });
        services.ChatStore.Flush();
    }

    private static Button? Row(MainWindow window, string id) =>
        Find<Panel>(window, "ChatListPanel").Children.OfType<Button>().FirstOrDefault(button => button.Tag as string == id);

    private T With<T>(Func<MainWindow, AppServices, T> body) => _wpf.Ui.Invoke(() =>
    {
        var services = UiServices.Build(_root, "k", new HttpClientHandler());
        var window = new MainWindow();
        window.AttachServices(services);
        try
        {
            return body(window, services);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void A_folder_is_a_header_that_folds_its_chats_and_tags_show_as_dots()
    {
        var (header, dotted, plain, afterFold) = With((window, services) =>
        {
            Save(services, "inside", "Inside");
            Save(services, "outside", "Outside");
            var folder = services.Organizer.CreateFolder("Work");
            services.Organizer.MoveToFolder(["inside"], folder.Id);
            services.Organizer.ToggleTag(["outside"], services.Organizer.CreateTag("hot", "Status.Danger").Id);
            Call(window, "RefreshChatList");

            var folderButton = Find<Panel>(window, "ChatListPanel").Children.OfType<Button>()
                .Single(button => button.Tag is ChatFolder);
            var headerText = System.Windows.Automation.AutomationProperties.GetName(folderButton);
            var dot = ChatRowState.GetTag1(Row(window, "outside")!);
            var none = ChatRowState.GetTag1(Row(window, "inside")!);

            folderButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            return (headerText, dot, none, Row(window, "inside"));
        });

        Assert.Equal("Work (1)", header);
        Assert.NotNull(dotted);
        Assert.Null(plain);
        Assert.Null(afterFold);
    }

    [Fact]
    public void Ctrl_selection_marks_rows_and_shows_the_batch_bar()
    {
        var (selected, bar, count, cleared) = With((window, services) =>
        {
            Save(services, "a", "A");
            Save(services, "b", "B");
            Call(window, "RefreshChatList");

            Call(window, "ToggleChatSelection", "a");
            Call(window, "ToggleChatSelection", "b");
            var marks = (ChatRowState.GetIsSelected(Row(window, "a")!), ChatRowState.GetIsSelected(Row(window, "b")!));
            var visible = Find<Border>(window, "BatchBar").Visibility;
            var text = Find<TextBlock>(window, "BatchCount").Text;

            Call(window, "ClearChatSelection");
            return (marks, visible, text, (Find<Border>(window, "BatchBar").Visibility, ChatRowState.GetIsSelected(Row(window, "a")!)));
        });

        Assert.Equal((true, true), selected);
        Assert.Equal(Visibility.Visible, bar);
        Assert.Contains("2", count, StringComparison.Ordinal);
        Assert.Equal((Visibility.Collapsed, false), cleared);
    }

    [Fact]
    public void Archiving_moves_chats_under_the_archive_header()
    {
        var (hidden, header) = With((window, services) =>
        {
            Save(services, "old", "Old");
            Save(services, "new", "New");
            Call(window, "ArchiveChats", new List<string> { "old" }, true);

            var archive = Find<Panel>(window, "ChatListPanel").Children.OfType<Button>()
                .SingleOrDefault(button => button.Tag is ChatListArchive);
            return (Row(window, "old") is null, archive is not null);
        });

        Assert.True(hidden);
        Assert.True(header);
    }

    [Fact]
    public async Task Deleting_asks_in_the_app_notice_and_forgets_the_placement()
    {
        var (asked, rowGone, stored, placed) = await _wpf.Ui.Invoke(async () =>
        {
            var services = UiServices.Build(_root, "k", new HttpClientHandler());
            var window = new MainWindow();
            window.AttachServices(services);
            try
            {
                Save(services, "doomed", "Doomed");
                services.Organizer.SetArchived(["doomed"], true);
                var task = (Task)Call(window, "DeleteChatsAsync", new List<string> { "doomed" })!;
                var notice = Find<FrameworkElement>(window, "NoticeOverlay").Visibility;

                Call(window, "CloseNotice", true);
                await task;
                return (notice, Row(window, "doomed") is null,
                    services.ChatStore.List().Any(entry => entry.Id == "doomed"),
                    services.Organizer.Snapshot().Chats.ContainsKey("doomed"));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(Visibility.Visible, asked);
        Assert.True(rowGone);
        Assert.False(stored);
        Assert.False(placed);
    }

    [Fact]
    public void The_sidebar_keeps_its_width_and_remembers_being_collapsed()
    {
        var (width, persisted) = With((window, services) =>
        {
            services.Settings.SidebarWidth = 260;
            Call(window, "SetSidebarCollapsed", false);
            var column = Find<ColumnDefinition>(window, "SidebarColumn").Width.Value;

            Call(window, "SetSidebarCollapsed", true);
            var saved = services.SettingsStore.Load().SidebarCollapsed;
            Call(window, "SetSidebarCollapsed", false);
            return (column, saved);
        });

        Assert.Equal(260, width);
        Assert.True(persisted);
    }
}
