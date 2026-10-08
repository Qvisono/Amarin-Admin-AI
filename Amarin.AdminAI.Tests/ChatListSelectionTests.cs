using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Выбор чатов шире Ctrl/Shift+щелчка (1.32.0): рамкой, Ctrl+A, Shift+Del, Esc и меню выбора.
/// </summary>
/// <remarks>
/// Рамка ведётся через <c>ChatListMarquee.Press</c>/<c>MoveTo</c>/<c>Release</c> в координатах
/// списка — положение мыши в поднятом <c>RaiseEvent</c> событии берётся у настоящего курсора, а
/// окно стоит за краем экрана. Где разбор идёт по настоящему событию (нажатие на строку), оно
/// поднимается на самой строке.
/// </remarks>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ChatListSelectionTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-select-" + Guid.NewGuid().ToString("N"));

    public ChatListSelectionTests(WpfFixture wpf) => _wpf = wpf;

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
    public void A_marquee_from_a_group_title_selects_the_rows_it_covers()
    {
        var (selected, expected, layerDuring, layerAfter) = With((window, _, marquee) =>
        {
            var title = Panel(window).Children.OfType<TextBlock>().First();
            var last = Row(window, "c");
            var from = Top(window, title) + new Vector(30, 4);
            var to = Center(window, last);
            var covered = Panel(window).Children.OfType<Button>()
                .Where(button => button.Tag is string && Top(window, button).Y < to.Y)
                .Select(button => (string)button.Tag)
                .ToHashSet();

            marquee.Press(from, additive: false, onHeader: false);
            marquee.MoveTo(to);
            var during = Layer(window).Visibility;
            marquee.Release();
            return (Selected(window), covered, during, Layer(window).Visibility);
        });

        Assert.Equal(expected.OrderBy(id => id), selected.OrderBy(id => id));
        Assert.Contains("c", selected);
        Assert.Equal(Visibility.Visible, layerDuring);
        Assert.Equal(Visibility.Collapsed, layerAfter);
    }

    [Fact]
    public void A_marquee_over_a_collapsed_folder_selects_its_hidden_chats_and_the_folder()
    {
        var (selected, folders, folderId, headerSelected) = With((window, services, marquee) =>
        {
            var folder = services.Organizer.CreateFolder("Work");
            services.Organizer.MoveToFolder(["x", "y"], folder.Id);
            services.Organizer.SetCollapsed(folder.Id, true);
            Refresh(window);

            var header = FolderHeader(window);
            marquee.Press(Top(window, header) + new Vector(20, -6), additive: false, onHeader: false);
            marquee.MoveTo(Center(window, header));
            marquee.Release();
            return (Selected(window), SelectedFolders(window), folder.Id, ChatRowState.GetIsSelected(FolderHeader(window)));
        });

        Assert.Contains("x", selected);
        Assert.Contains("y", selected);
        Assert.Equal([folderId], folders);
        Assert.True(headerSelected);
    }

    [Fact]
    public void A_marquee_with_ctrl_adds_to_the_selection()
    {
        var selected = With((window, _, marquee) =>
        {
            Call(window, "ToggleChatSelection", "a");
            var row = Row(window, "c");
            marquee.Press(Center(window, row) + new Vector(0, 40), additive: true, onHeader: false);
            marquee.MoveTo(Center(window, row));
            marquee.Release();
            return Selected(window);
        });

        Assert.Contains("a", selected);
        Assert.Contains("c", selected);
    }

    [Fact]
    public void A_click_on_empty_space_clears_the_selection()
    {
        var selected = With((window, _, marquee) =>
        {
            Call(window, "ToggleChatSelection", "a");
            var below = Center(window, Panel(window).Children.OfType<Button>().Last()) + new Vector(0, 60);
            marquee.Press(below, additive: false, onHeader: false);
            marquee.Release();
            return Selected(window);
        });

        Assert.Empty(selected);
    }

    [Fact]
    public void A_press_on_a_chat_row_or_its_menu_button_is_not_a_marquee()
    {
        // Нажатие на строке — это перетаскивание чата, на «⋯» — его меню.
        var (afterRow, afterActions) = With((window, _, marquee) =>
        {
            var row = Row(window, "a");
            Press(row);
            marquee.MoveTo(Center(window, row) + new Vector(0, 80));
            var row1 = marquee.IsSelecting;
            marquee.Release();

            row.ApplyTemplate();
            var actions = (Button)row.Template.FindName("Actions", row)!;
            Press(actions);
            marquee.MoveTo(Center(window, row) + new Vector(0, 80));
            var actions1 = marquee.IsSelecting;
            marquee.Release();
            return (row1, actions1);
        });

        Assert.False(afterRow);
        Assert.False(afterActions);
    }

    [Fact]
    public void The_marquee_is_built_by_the_first_press_in_the_column_not_by_the_window()
    {
        // В конструкторе окна рамка стоила ~7 мс холодного запуска, а нужна она лишь тому, кто
        // за неё возьмётся. Первое нажатие заводит её один раз; дальше нажатия ловит она сама.
        var (beforePress, afterPress, sameAfterSecond) = _wpf.Ui.Invoke(() =>
        {
            var (window, _) = OpenWindow();
            try
            {
                // Событие прямое: на колонке его поднимает её собственный разбор нажатия, туда и шлём.
                var column = (UIElement)window.FindName("SideBarScrollViewer")!;
                var before = BuiltMarquee(window);
                Press(column);
                var first = BuiltMarquee(window);
                first?.Cancel();
                Press(column);
                return (before, first, ReferenceEquals(first, BuiltMarquee(window)));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Null(beforePress);
        Assert.NotNull(afterPress);
        Assert.True(sameAfterSecond);
    }

    [Fact]
    public void The_list_waits_for_the_marquee_before_rebuilding()
    {
        var (during, after) = With((window, services, marquee) =>
        {
            var row = Row(window, "a");
            marquee.Press(Center(window, row) + new Vector(0, 60), additive: false, onHeader: false);
            marquee.MoveTo(Center(window, row));

            services.ChatStore.Save(new ChatSession { Id = "late", Title = "Late", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now });
            Refresh(window);
            var shownDuring = RowOrNull(window, "late") is not null;
            marquee.Release();
            window.UpdateLayout();
            return (shownDuring, RowOrNull(window, "late") is not null);
        });

        Assert.False(during);
        Assert.True(after);
    }

    [Fact]
    public void Escape_cancels_the_marquee_and_brings_the_old_selection_back()
    {
        var (during, after) = With((window, _, marquee) =>
        {
            Call(window, "ToggleChatSelection", "a");
            var row = Row(window, "c");
            marquee.Press(Center(window, row) + new Vector(0, 40), additive: false, onHeader: false);
            marquee.MoveTo(Center(window, row));
            var mid = Selected(window);
            Assert.True(marquee.Cancel());
            return (mid, Selected(window));
        });

        Assert.DoesNotContain("a", during);
        Assert.Equal(["a"], after);
    }

    [Fact]
    public void Ctrl_a_selects_every_chat_with_collapsed_folders_and_the_archive()
    {
        var (handled, selected, folders, folderId) = With((window, services, _) =>
        {
            var folder = services.Organizer.CreateFolder("Work");
            services.Organizer.MoveToFolder(["x"], folder.Id);
            services.Organizer.SetCollapsed(folder.Id, true);
            services.Organizer.SetArchived(["y"], true);
            Refresh(window);

            var result = (bool)Call(window, "TrySelectAllChats")!;
            return (result, Selected(window), SelectedFolders(window), folder.Id);
        });

        Assert.True(handled);
        Assert.Equal(["a", "b", "c", "x", "y"], selected.OrderBy(id => id));
        Assert.Equal([folderId], folders);
    }

    [Fact]
    public void Ctrl_a_leaves_the_chats_alone_while_settings_are_open()
    {
        var (handled, selected) = With((window, _, _) =>
        {
            Call(window, "OpenSettings", (object?)null);
            var result = (bool)Call(window, "TrySelectAllChats")!;
            return (result, Selected(window));
        });

        Assert.False(handled);
        Assert.Empty(selected);
    }

    [Fact]
    public void Text_fields_keep_their_own_select_all_and_cut()
    {
        var verdicts = With((window, _, _) =>
        {
            var empty = new TextBox();
            var typed = new TextBox { Text = "черновик" };
            var selectedText = new TextBox { Text = "черновик" };
            selectedText.Select(0, 3);
            var answer = new RichTextBox { IsReadOnly = true };

            // Хозяин блока — окно: блок берёт у него стили своих кнопок.
            var code = FindCodeBox(CodeBlockView.Create(window, "Get-Service", "powershell"));

            return new
            {
                EmptyAll = MainWindow.TextOwnsSelectAll(empty),
                TypedAll = MainWindow.TextOwnsSelectAll(typed),
                AnswerAll = MainWindow.TextOwnsSelectAll(answer),
                CodeAll = MainWindow.TextOwnsSelectAll(code),
                NothingAll = MainWindow.TextOwnsSelectAll(null),
                TypedCut = MainWindow.TextOwnsCut(typed),
                SelectedCut = MainWindow.TextOwnsCut(selectedText)
            };
        });

        // Пустое поле ввода — Ctrl+A про чаты; поле с текстом и блок кода — их «выделить всё».
        Assert.False(verdicts.EmptyAll);
        Assert.True(verdicts.TypedAll);
        Assert.False(verdicts.AnswerAll);
        Assert.True(verdicts.CodeAll);
        Assert.False(verdicts.NothingAll);

        // Shift+Delete — «вырезать» только при выделенном тексте.
        Assert.False(verdicts.TypedCut);
        Assert.True(verdicts.SelectedCut);
    }

    [Fact]
    public async Task Shift_delete_asks_once_and_deletes_the_selection_with_its_whole_folders()
    {
        var (asked, text, chatsLeft, folderLeft) = await _wpf.Ui.Invoke(async () =>
        {
            var (window, services, marquee) = Open();
            try
            {
                var folder = services.Organizer.CreateFolder("Work");
                services.Organizer.MoveToFolder(["x", "y"], folder.Id);
                services.Organizer.SetCollapsed(folder.Id, true);
                Refresh(window);
                var header = FolderHeader(window);
                marquee.Press(Top(window, header) + new Vector(20, -6), additive: false, onHeader: false);
                marquee.MoveTo(Center(window, header));
                marquee.Release();

                Assert.True((bool)Call(window, "TryDeleteChatsByKey")!);
                var shown = ((FrameworkElement)window.FindName("NoticeOverlay")!).Visibility;
                var body = ((TextBlock)window.FindName("NoticeText")!).Text;
                var pending = PendingNotice(window);
                Call(window, "CloseNotice", true);
                await pending;
                await Task.Delay(50);

                return (shown, body, services.ChatStore.List().Select(entry => entry.Id).ToList(),
                    services.Organizer.Snapshot().Folders.Any(item => item.Id == folder.Id));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(Visibility.Visible, asked);
        Assert.Equal(Loc.Format("S.ChatList.DeleteWithFoldersConfirm", 2, 1), text);
        Assert.DoesNotContain("x", chatsLeft);
        Assert.DoesNotContain("y", chatsLeft);
        Assert.Contains("a", chatsLeft);
        Assert.False(folderLeft);
    }

    [Fact]
    public void The_menu_of_a_selected_row_keeps_the_selection_and_of_another_row_clears_it()
    {
        var (keptForSelected, keptForOther) = With((window, _, _) =>
        {
            Call(window, "ToggleChatSelection", "a");
            Call(window, "ToggleChatSelection", "b");
            Call(window, "OpenChatActionsMenu", Row(window, "a"), "a", PlacementMode.Bottom);
            var kept = Selected(window).Count;
            CloseMenus();

            Call(window, "OpenChatActionsMenu", Row(window, "c"), "c", PlacementMode.Bottom);
            var other = Selected(window).Count;
            CloseMenus();
            return (kept, other);
        });

        Assert.Equal(2, keptForSelected);
        Assert.Equal(0, keptForOther);
    }

    [Fact]
    public void While_the_column_coasts_the_rows_ignore_the_mouse_and_take_it_back_when_it_stops()
    {
        // Под инерцией колеса строки проезжают под неподвижной мышью, и каждая раскладывала
        // заново заголовок и кнопку «⋯»: над строками прокрутка дёргалась, у полосы шла гладко.
        var (moving, rowWhileMoving, tipWhileMoving, stopped, rowAfter) = With((window, _, _) =>
        {
            var column = (ScrollViewer)window.FindName("SideBarScrollViewer")!;
            var row = Row(window, "a");
            SmoothScroll.Fling(column, 4000);
            var inMotion = SmoothScroll.GetIsInMotion(column);
            var scrolling = ChatRowState.GetScrolling(row);
            var tip = ToolTipService.GetIsEnabled(row);
            SmoothScroll.Cancel(column);
            return (inMotion, scrolling, tip, SmoothScroll.GetIsInMotion(column), ChatRowState.GetScrolling(row));
        });

        Assert.True(moving);
        Assert.True(rowWhileMoving);
        Assert.False(tipWhileMoving);
        Assert.False(stopped);
        Assert.False(rowAfter);
    }

    [Fact]
    public void A_press_anywhere_outside_the_list_clears_the_selection_but_the_title_bar_keeps_it()
    {
        // Как щелчок по пустому месту колонки: нажатие в ленте, в поле ввода или на кнопках
        // боковой панели снимает выбор. Заголовок окна — нет: окно двигают, не меняя выбора.
        var (input, transcript, search, title) = With((window, _, _) =>
        {
            bool PressClears(string element)
            {
                Call(window, "ToggleChatSelection", "a");
                Call(window, "ToggleChatSelection", "b");
                PressDown((UIElement)window.FindName(element)!);
                return Selected(window).Count == 0;
            }

            var cleared = (PressClears("MessageTextBox"), PressClears("ChatScrollViewer"), PressClears("SearchBox"));
            Call(window, "ClearChatSelection");
            Call(window, "ToggleChatSelection", "a");
            PressDown((UIElement)window.FindName("TitleText")!);
            return (cleared.Item1, cleared.Item2, cleared.Item3, Selected(window).Count);
        });

        Assert.True(input);
        Assert.True(transcript);
        Assert.True(search);
        Assert.Equal(1, title);
    }

    [Fact]
    public void The_menu_key_on_the_column_opens_the_menu_of_the_whole_selection()
    {
        // Полосы с кнопками над списком нет (1.32.0): с клавиатуры до действий над выбором ведёт
        // клавиша меню — после рамки и Ctrl+A фокус как раз у колонки, а не у строки.
        var (headers, kept, withoutSelection) = With((window, _, _) =>
        {
            Call(window, "ToggleChatSelection", "a");
            Call(window, "ToggleChatSelection", "b");
            var column = (UIElement)window.FindName("SideBarScrollViewer")!;
            column.RaiseEvent(KeyboardMenu(column));
            var items = OpenMenus().SelectMany(menu => menu.Items.OfType<MenuItem>()).Select(item => item.Header as string).ToList();
            var selected = Selected(window).Count;
            CloseMenus();

            // Без выбора клавиша меню на колонке ничего не открывает: у пустого места меню нет.
            Call(window, "ClearChatSelection");
            column.RaiseEvent(KeyboardMenu(column));
            var none = OpenMenus().Count;
            CloseMenus();
            return (items, selected, none);
        });

        Assert.Contains(Loc.Get("S.Common.Delete"), headers);
        Assert.Contains(Loc.Get("S.ChatList.MoveToFolder") + "…", headers);
        Assert.Equal(2, kept);
        Assert.Equal(0, withoutSelection);
    }

    [Fact]
    public void Renaming_several_chats_gives_them_one_name()
    {
        var titles = With((window, services, _) =>
        {
            Call(window, "RenameChats", new List<string> { "a", "b" });
            ((TextBox)window.FindName("NameInput")!).Text = "Общее";
            Call(window, "NameSaveButton_Click", window, new RoutedEventArgs());
            return services.ChatStore.List().Where(entry => entry.Id is "a" or "b").Select(entry => entry.Title).ToList();
        });

        Assert.Equal(["Общее", "Общее"], titles);
    }

    // ───────────────────────── оснастка ─────────────────────────

    private T With<T>(Func<MainWindow, AppServices, ChatListMarquee, T> body) => _wpf.Ui.Invoke(() =>
    {
        var (window, services, marquee) = Open();
        try
        {
            return body(window, services, marquee);
        }
        finally
        {
            CloseMenus();
            window.Close();
        }
    });

    private (MainWindow Window, AppServices Services, ChatListMarquee Marquee) Open()
    {
        var (window, services) = OpenWindow();
        var marquee = (ChatListMarquee)typeof(MainWindow)
            .GetProperty("Marquee", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
        marquee.RealMouse = false;
        Refresh(window);
        return (window, services, marquee);
    }

    private (MainWindow Window, AppServices Services) OpenWindow()
    {
        var services = UiServices.Build(Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]), "k", new HttpClientHandler());
        var now = DateTime.Now;
        var minutes = 0;
        foreach (var id in new[] { "a", "b", "c", "x", "y" })
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
        return (window, services);
    }

    private static ChatListMarquee? BuiltMarquee(MainWindow window) =>
        (ChatListMarquee?)typeof(MainWindow).GetField("_marquee", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    private static void Refresh(MainWindow window)
    {
        Call(window, "RefreshChatList");
        window.UpdateLayout();
    }

    private static Panel Panel(MainWindow window) => (Panel)window.FindName("ChatListPanel")!;

    private static Canvas Layer(MainWindow window) => (Canvas)window.FindName("ChatMarqueeLayer")!;

    private static Button Row(MainWindow window, string id) => RowOrNull(window, id) ?? throw new InvalidOperationException("нет строки " + id);

    private static Button? RowOrNull(MainWindow window, string id) =>
        Panel(window).Children.OfType<Button>().FirstOrDefault(button => button.Tag as string == id);

    private static Button FolderHeader(MainWindow window) =>
        Panel(window).Children.OfType<Button>().First(button => button.Tag is ChatFolder);

    private static Point Top(MainWindow window, FrameworkElement element) => element.TranslatePoint(default, Panel(window));

    private static Point Center(MainWindow window, FrameworkElement element) =>
        element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), Panel(window));

    private static List<string> Selected(MainWindow window) =>
        [.. ((ChatSelection)typeof(MainWindow).GetField("_selection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Items];

    private static List<string> SelectedFolders(MainWindow window) => [.. (IReadOnlyList<string>)Call(window, "SelectedFolders")!];

    private static Task PendingNotice(MainWindow window) =>
        typeof(MainWindow).GetField("_notice", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) is { } notice &&
        notice.GetType().GetProperty("Task")?.GetValue(notice) is Task task
            ? task
            : Task.CompletedTask;

    /// <summary>Нажатие на элементе так, как его видит окно: туннелем сверху вниз.</summary>
    private static void PressDown(UIElement element) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseDownEvent
        });

    /// <summary>Нажатие на элементе — настоящим событием: его разбирает сама рамка.</summary>
    private static void Press(UIElement element) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
        });

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

    /// <summary>
    /// Клавиша меню на элементе: у вызова с клавиатуры нет точки курсора (-1, -1). Конструктор
    /// аргументов у WPF внутренний — так же их создаёт сама служба контекстных меню.
    /// </summary>
    private static ContextMenuEventArgs KeyboardMenu(object source) =>
        (ContextMenuEventArgs)Activator.CreateInstance(
            typeof(ContextMenuEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [source, true, -1.0, -1.0],
            culture: null)!;

    private static RichTextBox FindCodeBox(DependencyObject root)
    {
        var stack = new Stack<object>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is RichTextBox box)
            {
                return box;
            }

            if (node is DependencyObject element)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(element))
                {
                    stack.Push(child);
                }
            }
        }

        throw new InvalidOperationException("в блоке кода нет поля");
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

    private static object? Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(window, args);
}
