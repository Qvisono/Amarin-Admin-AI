using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Чистка интерфейса 1.28.0: подстраницы настроек, Connections внутри Automation, вложенные
/// чаты папок, фильтр по тегу в меню, одна галка трея.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class SettingsRedesignTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-redesign-" + Guid.NewGuid().ToString("N"));

    public SettingsRedesignTests(WpfFixture wpf) => _wpf = wpf;

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

    private static MainWindow Shared() => Application.Current.Windows.OfType<MainWindow>().First(window => window.IsVisible);

    private static object? Call(object target, string method, params object?[] args) =>
        target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(target, args);

    private T WithWindow<T>(Func<MainWindow, AppServices, T> body) => _wpf.Ui.Invoke(() =>
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

    private static Button? Row(MainWindow window, string id) =>
        ((Panel)window.FindSetting("ChatListPanel")!).Children.OfType<Button>().FirstOrDefault(button => button.Tag as string == id);

    private static void Save(AppServices services, string id)
    {
        services.ChatStore.Save(new ChatSession { Id = id, Title = id, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now });
        services.ChatStore.Flush();
    }

    // ───────────────────────── настройки ─────────────────────────

    /// <summary>Подключения — вкладками в Automation, а не пунктом навигации; окно 720×520.</summary>
    [Fact]
    public void Connections_are_tabs_of_automation_and_the_window_is_compact_again()
    {
        var (nav, machines, mcp, size) = _wpf.Ui.Invoke(() =>
        {
            var window = Shared();
            var automation = (SettingsAutomationPage)window.FindSetting("AutomationPage")!;
            return (
                window.FindSetting("NavConnections"),
                automation.FindName("MachinesTab"),
                automation.FindName("McpTab"),
                new Size(((FrameworkElement)window.FindSetting("SettingsCard")!).Width, ((FrameworkElement)window.FindSetting("SettingsCard")!).Height));
        });

        Assert.Null(nav);
        Assert.NotNull(machines);
        Assert.NotNull(mcp);
        Assert.Equal(new Size(720, 520), size);
    }

    /// <summary>
    /// Строка «›» открывает подстраницу, Esc (через <see cref="SettingsDrill.TryBackIn"/>)
    /// возвращает, а уход на другую страницу сбрасывает подстраницу к корню.
    /// </summary>
    [Fact]
    public void A_sub_page_opens_goes_back_and_resets_when_the_page_is_left()
    {
        var (opened, rootHidden, back, reset) = _wpf.Ui.Invoke(() =>
        {
            var window = Shared();
            var overlay = (FrameworkElement)window.FindSetting("SettingsOverlay")!;
            var general = (RadioButton)window.FindSetting("NavGeneral")!;
            var profile = (RadioButton)window.FindSetting("NavProfile")!;
            var link = (Button)window.FindSetting("WindowsLinkRow")!;
            var sub = (FrameworkElement)window.FindSetting("GeneralWindowsSub")!;
            var wasVisible = overlay.Visibility;
            var wasChecked = SettingsNavNames.All
                .Select(name => (RadioButton)window.FindSetting(name)!)
                .FirstOrDefault(radio => radio.IsChecked == true);
            overlay.Visibility = Visibility.Visible;
            general.IsChecked = true;
            window.UpdateLayout();
            try
            {
                link.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, link));
                window.UpdateLayout();
                var root = (FrameworkElement)((Panel)sub.Parent).Children[0];
                var isOpen = sub.Visibility == Visibility.Visible;
                var rootCollapsed = root.Visibility == Visibility.Collapsed;

                var wentBack = SettingsDrill.TryBackIn(overlay) && sub.Visibility == Visibility.Collapsed && root.Visibility == Visibility.Visible;

                link.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, link));
                profile.IsChecked = true;
                window.UpdateLayout();
                var wasReset = sub.Visibility == Visibility.Collapsed && root.Visibility == Visibility.Visible;
                return (isOpen, rootCollapsed, wentBack, wasReset);
            }
            finally
            {
                if (wasChecked is not null)
                {
                    wasChecked.IsChecked = true;
                }

                overlay.Visibility = wasVisible;
            }
        });

        Assert.True(opened);
        Assert.True(rootHidden);
        Assert.True(back);
        Assert.True(reset);
    }

    /// <summary>
    /// Переключение вкладок Automation ничего не двигает: выбранная вкладка не шире прежней
    /// (полужирная подпись толкала соседей), а содержимое всех вкладок начинается на одной высоте
    /// (описание в две строки уводило список ниже).
    /// </summary>
    [Fact]
    public void Switching_automation_tabs_moves_nothing()
    {
        var (widths, tops) = _wpf.Ui.Invoke(() =>
        {
            var window = Shared();
            var overlay = (FrameworkElement)window.FindSetting("SettingsOverlay")!;
            var nav = (RadioButton)window.FindSetting("NavAutomation")!;
            var page = (SettingsAutomationPage)window.FindSetting("AutomationPage")!;
            var tabs = new[] { "RecipesTab", "ScheduleTab", "MachinesTab", "McpTab" }.Select(name => (RadioButton)page.FindName(name)!).ToList();
            var wasVisible = overlay.Visibility;
            var wasChecked = window.FindSetting("NavGeneral") as RadioButton;
            overlay.Visibility = Visibility.Visible;
            nav.IsChecked = true;
            var widthSets = new List<double[]>();
            var headerBottoms = new List<double>();
            try
            {
                foreach (var tab in tabs)
                {
                    tab.IsChecked = true;
                    window.UpdateLayout();
                    widthSets.Add(tabs.Select(item => Math.Round(item.ActualWidth, 2)).ToArray());

                    // Низ шапки вкладки (описание и «Добавить») — с него начинается список.
                    var header = Descendants<Grid>(page).First(grid => grid.IsVisible && grid.Height == 28);
                    headerBottoms.Add(Math.Round(header.TranslatePoint(new Point(0, header.ActualHeight), page).Y, 2));
                }
            }
            finally
            {
                tabs[0].IsChecked = true;
                wasChecked?.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                overlay.Visibility = wasVisible;
            }

            return (widthSets, headerBottoms);
        });

        Assert.All(widths, set => Assert.Equal(widths[0], set));
        Assert.All(tops, top => Assert.Equal(tops[0], top));
    }

    /// <summary>
    /// Заголовок страницы стоит в одной точке на всех страницах: иначе при переходе по
    /// пунктам навигации он прыгал бы.
    /// </summary>
    [Fact]
    public void Every_settings_page_puts_its_title_in_the_same_place()
    {
        var titles = _wpf.Ui.Invoke(() =>
        {
            var window = Shared();
            var overlay = (FrameworkElement)window.FindSetting("SettingsOverlay")!;
            var card = (FrameworkElement)window.FindSetting("SettingsCard")!;
            var wasVisible = overlay.Visibility;
            var names = SettingsNavNames.All;
            var wasChecked = names.Select(name => (RadioButton)window.FindSetting(name)!).FirstOrDefault(radio => radio.IsChecked == true);
            overlay.Visibility = Visibility.Visible;
            var found = new Dictionary<string, Point>();
            try
            {
                foreach (var name in names)
                {
                    ((RadioButton)window.FindSetting(name)!).IsChecked = true;
                    window.UpdateLayout();
                    var title = Descendants<TextBlock>(card)
                        .Where(text => text.IsVisible && text.FontSize >= 15 && text.FontWeight == FontWeights.SemiBold)
                        .OrderBy(text => LaidOutAt(text, card).Y)
                        .FirstOrDefault();
                    if (title is not null)
                    {
                        var at = LaidOutAt(title, card);
                        found[name + " «" + title.Text + "»"] = new Point(Math.Round(at.X, 1), Math.Round(at.Y, 1));
                    }
                }
            }
            finally
            {
                wasChecked?.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                overlay.Visibility = wasVisible;
            }

            return found;
        });

        Assert.Equal(SettingsNavNames.All.Length, titles.Count);
        var first = titles.Values.First();
        Assert.True(titles.Values.All(point => point == first), string.Join("; ", titles.Select(pair => pair.Key + " " + pair.Value)));

        // Фикстура держит русский язык: в 1.30 восемь заголовков из одиннадцати были английскими.
        var latin = titles.Keys.Where(key => System.Text.RegularExpressions.Regex.IsMatch(key[(key.IndexOf('«') + 1)..], "[A-Za-z]")).ToList();
        Assert.True(latin.Count == 0, "по-английски: " + string.Join("; ", latin));
    }

    /// <summary>
    /// Где элемент стоит по раскладке, без RenderTransform: показанная страница проявляется
    /// сдвигом (UiMotion), и замер посреди проявления разошёлся бы с раскладкой на доли точки.
    /// Страница, которую уже показывали, проявляется, а ещё не загруженная — нет.
    /// </summary>
    private static Point LaidOutAt(Visual element, Visual ancestor)
    {
        var at = new Vector();
        for (DependencyObject? node = element; node is Visual visual && !ReferenceEquals(visual, ancestor); node = VisualTreeHelper.GetParent(node))
        {
            at += VisualTreeHelper.GetOffset(visual);
        }

        return new Point(at.X, at.Y);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
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

    /// <summary>«Крестик прячет в трей» без значка в трее ничего бы не делал — и неактивен.</summary>
    [Fact]
    public void Close_to_tray_is_off_limits_without_the_tray_icon()
    {
        var (withoutIcon, withIcon) = WithWindow((window, services) =>
        {
            var block = new WindowsIntegrationBlock();
            services.Settings.Windows = new WindowsIntegrationSettings { ShowTrayIcon = false };
            block.Load(services);
            var nested = (FrameworkElement)((FrameworkElement)block.FindName("CloseToTrayToggle")!).Parent;
            var off = nested.IsEnabled;
            ((CheckBox)block.FindName("TrayIconToggle")!).IsChecked = true;
            return (off, nested.IsEnabled);
        });

        Assert.False(withoutIcon);
        Assert.True(withIcon);
    }

    [Fact]
    public void Minimize_to_tray_is_gone()
    {
        Assert.Null(typeof(WindowsIntegrationSettings).GetProperty("MinimizeToTray"));
    }

    // ───────────────────────── боковая панель ─────────────────────────

    /// <summary>
    /// Раскрытая папка — одна карточка: заголовок сверху, последний чат замыкает низ, и куски
    /// подложки смыкаются без зазора. Название чата в папке стоит под именем папки, а не вровень с
    /// чатами вне её. Свёрнутая папка — карточка из одной строки, чаты вне папок карточки не рисуют.
    /// </summary>
    [Fact]
    public void An_open_folder_is_one_card_with_its_chats_inside()
    {
        var result = WithWindow((window, services) =>
        {
            Save(services, "a");
            Save(services, "b");
            Save(services, "outside");
            Save(services, "shut-in");
            var open = services.Organizer.CreateFolder("Work");
            var shut = services.Organizer.CreateFolder("Old");
            services.Organizer.MoveToFolder(["a", "b"], open.Id);
            services.Organizer.MoveToFolder(["shut-in"], shut.Id);
            services.Organizer.SetCollapsed(shut.Id, true);
            Call(window, "RefreshChatList");

            // Окно не показано, и раскладки у него нет — список меряется сам, шириной колонки.
            var panel = (Panel)window.FindSetting("ChatListPanel")!;
            panel.Measure(new Size(260, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, 260, panel.DesiredSize.Height));
            var headers = panel.Children.OfType<Button>().Where(button => button.Tag is ChatFolder).ToList();
            var openHeader = headers.Single(button => ((ChatFolder)button.Tag).Id == open.Id);
            var shutHeader = headers.Single(button => ((ChatFolder)button.Tag).Id == shut.Id);
            var rows = panel.Children.OfType<Button>().Where(button => button.Tag is string).ToList();
            var inFolder = rows.Where(row => (string)row.Tag is "a" or "b").ToList();
            var first = inFolder[0];
            var last = inFolder[1];
            var outside = Row(window, "outside")!;

            // Карточка сомкнута: низ каждой строки — верх следующей.
            double Top(FrameworkElement element) => element.TranslatePoint(new Point(0, 0), panel).Y;
            double Bottom(FrameworkElement element) => Top(element) + element.ActualHeight;
            var seamless = Math.Abs(Bottom(openHeader) - Top(first)) < 0.5 && Math.Abs(Bottom(first) - Top(last)) < 0.5;

            // Где начинается текст строки: у заголовка — значок папки, у чата — название.
            double TitleX(Button row)
            {
                row.ApplyTemplate();
                var bg = (Border)row.Template.FindName("Bg", row);
                return bg.TranslatePoint(new Point(bg.Padding.Left, 0), panel).X;
            }

            return new
            {
                Bands = (ChatRowState.GetBand(openHeader), ChatRowState.GetBand(first), ChatRowState.GetBand(last)),
                Shut = ChatRowState.GetBand(shutHeader),
                Outside = ChatRowState.GetBand(outside),
                ShutChatHidden = rows.All(row => (string)row.Tag != "shut-in"),
                Seamless = seamless,
                Inside = TitleX(first),
                Folder = TitleX(openHeader),
                Plain = TitleX(outside)
            };
        });

        Assert.Equal((FolderBand.Top, FolderBand.Middle, FolderBand.Bottom), result.Bands);
        Assert.Equal(FolderBand.Single, result.Shut);
        Assert.Equal(FolderBand.None, result.Outside);
        Assert.True(result.ShutChatHidden);
        Assert.True(result.Seamless, "между кусками карточки папки зазор");
        Assert.True(result.Inside > result.Plain + 15, $"название чата в папке на {result.Inside}, вне папки на {result.Plain}");
        Assert.Equal(result.Plain, result.Folder, 1);
    }

    /// <summary>
    /// Раскрытие папки не двигает её заголовок: та же высота подсветки, то же место. Прежде
    /// свёрнутая папка была голой строкой и при раскрытии вырастала и съезжала на несколько точек.
    /// </summary>
    [Fact]
    public void Opening_a_folder_does_not_move_its_header()
    {
        var (closed, open) = WithWindow((window, services) =>
        {
            Save(services, "inside");
            var folder = services.Organizer.CreateFolder("Work");
            services.Organizer.MoveToFolder(["inside"], folder.Id);

            Rect Header()
            {
                Call(window, "RefreshChatList");
                var panel = (Panel)window.FindSetting("ChatListPanel")!;
                panel.Measure(new Size(260, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, 260, panel.DesiredSize.Height));
                var header = panel.Children.OfType<Button>().Single(button => button.Tag is ChatFolder);
                header.ApplyTemplate();
                var bg = (Border)header.Template.FindName("Bg", header);
                return new Rect(bg.TranslatePoint(new Point(0, 0), panel), new Size(bg.ActualWidth, bg.ActualHeight));
            }

            services.Organizer.SetCollapsed(folder.Id, true);
            var shut = Header();
            services.Organizer.SetCollapsed(folder.Id, false);
            return (shut, Header());
        });

        Assert.True(closed.Height > 0);
        Assert.Equal(closed, open);
    }

    /// <summary>
    /// Теги больше не занимают ряд под поиском: фильтр ставится из меню, пилюля показывает его
    /// и снимает одним щелчком.
    /// </summary>
    [Fact]
    public void The_tag_filter_is_a_menu_choice_with_a_pill_that_clears_it()
    {
        var (noRow, shown, filtered, cleared, all) = WithWindow((window, services) =>
        {
            Save(services, "tagged");
            Save(services, "plain");
            var tag = services.Organizer.CreateTag("hot", "Status.Danger");
            services.Organizer.ToggleTag(["tagged"], tag.Id);
            Call(window, "RefreshChatList");

            var pill = (FrameworkElement)window.FindSetting("TagFilterPill")!;
            Call(window, "SetTagFilter", tag.Id);
            var visible = pill.Visibility;
            var onlyTagged = Row(window, "plain") is null && Row(window, "tagged") is not null;

            Call(window, "TagFilterClear_Click", window, new RoutedEventArgs());
            return (window.FindSetting("TagFilterRow") is null, visible, onlyTagged, pill.Visibility, Row(window, "plain") is not null);
        });

        Assert.True(noRow);
        Assert.Equal(Visibility.Visible, shown);
        Assert.True(filtered);
        Assert.Equal(Visibility.Collapsed, cleared);
        Assert.True(all);
    }

    /// <summary>Шаблоны новых чатов вырезаны: рядом с «New Chat» больше нет кнопки-стрелки.</summary>
    [Fact]
    public void New_chat_has_no_template_dropdown()
    {
        var found = _wpf.Ui.Invoke(() => Shared().FindSetting("TemplatesButton"));

        Assert.Null(found);
        Assert.Null(typeof(AppServices).GetProperty("Templates", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
    }
}
