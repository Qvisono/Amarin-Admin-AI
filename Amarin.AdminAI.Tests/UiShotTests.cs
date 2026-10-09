using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Снимки интерфейса для глаз: каждая страница и подстраница настроек, боковая панель, панель
/// «Состояние ПК» — в PNG, в двух языках и двух темах.
/// </summary>
/// <remarks>
/// Работает только при <c>AMARIN_UI_SHOTS=&lt;папка&gt;</c>, обычный прогон сразу выходит: снимок —
/// это проверка глазами, а не утверждение, и сотни картинок на каждом прогоне никому не нужны.
/// Окно своё, на временной папке, и стоит за краем экрана — поверх работы человека оно не вылезет.
/// </remarks>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class UiShotTests : IDisposable
{
    private const double Scale = 1.5;

    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-shots-" + Guid.NewGuid().ToString("N"));

    public UiShotTests(WpfFixture wpf) => _wpf = wpf;

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

    private static readonly string[] NavItems = SettingsNavNames.All;

    [Fact]
    public async Task Shots()
    {
        var dir = Environment.GetEnvironmentVariable("AMARIN_UI_SHOTS");
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        var only = Environment.GetEnvironmentVariable("AMARIN_UI_SHOTS_ONLY");

        // С фильтром снимается один вариант (английский, тёмный), если не попросили все.
        var everyVariant = Environment.GetEnvironmentVariable("AMARIN_UI_SHOTS_ALL") == "1";
        await _wpf.Ui.Invoke(async () =>
        {
            var previousTheme = ThemeManager.Current.Theme;
            try
            {
                foreach (var (language, theme, small) in Variants())
                {
                    var folder = Path.Combine(dir, language + "-" + theme.ToString().ToLowerInvariant() + (small ? "-min" : ""));
                    Directory.CreateDirectory(folder);
                    await ShootAll(folder, language, theme, small, only);
                    if (!string.IsNullOrEmpty(only) && !everyVariant && Palettes() is null)
                    {
                        break;
                    }
                }
            }
            finally
            {
                LanguageManager.Apply(LanguageManager.DefaultCode);
                ThemeManager.Apply(previousTheme);
            }

            return true;
        });
    }

    /// <summary>
    /// Варианты съёмки: язык, тема и маленькое окно. По умолчанию — оба языка в тёмной и светлой
    /// теме. <c>AMARIN_UI_SHOTS_THEMES=Contrast,EdgeAmber</c> (или <c>all</c> — все палитры)
    /// снимает русский интерфейс в перечисленных палитрах, <c>AMARIN_UI_SHOTS_MIN=1</c> добавляет
    /// окно минимального размера 850×535.
    /// </summary>
    private static IEnumerable<(string Language, AppTheme Theme, bool Small)> Variants()
    {
        if (Palettes() is { } palettes)
        {
            foreach (var palette in palettes)
            {
                yield return ("ru", palette, false);
            }

            yield break;
        }

        yield return ("en", AppTheme.Dark, false);
        yield return ("ru", AppTheme.Dark, false);
        yield return ("en", AppTheme.Light, false);
        yield return ("ru", AppTheme.Light, false);
        if (Environment.GetEnvironmentVariable("AMARIN_UI_SHOTS_MIN") == "1")
        {
            yield return ("ru", AppTheme.Dark, true);
            yield return ("en", AppTheme.Light, true);
        }
    }

    private static IReadOnlyList<AppTheme>? Palettes()
    {
        var value = Environment.GetEnvironmentVariable("AMARIN_UI_SHOTS_THEMES");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim() == "all"
            ? Enum.GetValues<AppTheme>().Where(theme => theme != AppTheme.System).ToList()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => Enum.Parse<AppTheme>(name, ignoreCase: true)).ToList();
    }

    private async Task ShootAll(string folder, string language, AppTheme theme, bool small, string? only)
    {
        var services = UiServices.Build(Path.Combine(_root, language + theme + (small ? "-min" : "")), "k", new HttpClientHandler());
        services.Settings.Theme = theme;
        services.Settings.LanguageCode = language;

        // На диск, как у настоящей программы: открытие настроек перечитывает settings.json, и
        // тема с языком, оставленные только в памяти, сменились бы на заводские.
        services.SettingsStore.Save(services.Settings);
        Seed(services);

        // Минимальный размер окна — тот, что держит MainWindow (MinWidth/MinHeight): на нём
        // проверяется, что настройки и лента не ужимаются и не обрезаются.
        var window = new MainWindow
        {
            Width = small ? 850 : 1280,
            Height = small ? 535 : 860,
            Left = -32000,
            Top = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.AttachServices(services);
        window.Show();
        ThemeManager.Apply(theme);
        LanguageManager.Apply(language);
        try
        {
            Call(window, "RefreshChatList");

            // Открытый разговор: без него лента пустая, и снимок не показывает ни края ленты
            // относительно поля ввода, ни кнопок под ответом, ни того, что висит над лентой.
            Call(window, "OpenChat", "c7");
            await Settle(400);

            if (Wanted(only, "window"))
            {
                Save((FrameworkElement)window.Content, Path.Combine(folder, "00-window.png"), null);
                if (window.FindSetting("SideBarScrollViewer") is FrameworkElement list)
                {
                    // Открытый чат — внутри папки: так видно подсветку строки в карточке папки.
                    var open = ((Panel)window.FindSetting("ChatListPanel")).Children.OfType<Button>()
                        .FirstOrDefault(button => button.Tag as string == "f1");
                    if (open is not null)
                    {
                        ChatRowState.SetIsActive(open, true);
                    }

                    await Settle(100);
                    Save(list, Path.Combine(folder, "00-sidebar.png"), (Brush)window.FindResource("Bg.Sidebar"));
                    if (open is not null)
                    {
                        ChatRowState.SetIsActive(open, false);
                    }
                }

                // Меню ⇅ и действия тега из его «⋯». Меню живёт в своём всплывающем окне, и снимок
                // окна его не видит — снимается сам открытый ContextMenu.
                Call(window, "ListOptionsButton_Click", window, new RoutedEventArgs());
                await Settle(300);
                if (OpenMenu() is { } sortMenu)
                {
                    Save(sortMenu, Path.Combine(folder, "00-menu-sort.png"), null);
                    var more = Descendants<Button>(sortMenu).FirstOrDefault();
                    more?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, more));
                    await Settle(300);
                    if (OpenMenu() is { } actions)
                    {
                        Save(actions, Path.Combine(folder, "00-menu-tag-actions.png"), null);
                        actions.IsOpen = false;
                    }

                    sortMenu.IsOpen = false;
                }

                // Меню «⋯» строки чата — со значками у каждого пункта; у закреплённой — «Открепить».
                // Второй профиль — на время снимков меню: без него «Отправить | Переместить»
                // приглушены, а выбор профиля не открывается (1.33.0).
                var panel = (Panel)window.FindSetting("ChatListPanel");
                var otherProfile = new UserProfile { Id = "shot-profile", Name = language == "ru" ? "Работа" : "Work" };
                services.ProfileRegistry.Profiles.Add(otherProfile);
                try
                {
                    foreach (var (pinned, file) in new[] { (false, "00-menu-chat.png"), (true, "00-menu-chat-pinned.png") })
                    {
                        var row = panel.Children.OfType<Button>()
                            .FirstOrDefault(button => button.Tag is string && ChatRowState.GetIsPinned(button) == pinned);
                        if (row is null)
                        {
                            continue;
                        }

                        Call(window, "OpenChatActionsMenu", row, (string)row.Tag, PlacementMode.Bottom);
                        await Settle(300);
                        if (OpenMenu() is { } chatMenu)
                        {
                            Save(chatMenu, Path.Combine(folder, file), null);
                            chatMenu.IsOpen = false;
                        }

                        if (!pinned)
                        {
                            Call(window, "OpenTransferMenu", row, (IReadOnlyList<string>)[(string)row.Tag], PlacementMode.Bottom, true);
                            await Settle(300);
                            if (OpenMenu() is { } transferMenu)
                            {
                                Save(transferMenu, Path.Combine(folder, "00-menu-chat-move.png"), null);
                                transferMenu.IsOpen = false;
                            }
                        }
                    }
                }
                finally
                {
                    services.ProfileRegistry.Profiles.Remove(otherProfile);
                }

                await ShootSelection(window, panel, folder);

                // Боковая панель с включённым фильтром по тегу.
                var tag = services.Organizer.Snapshot().Tags.First();
                Call(window, "SetTagFilter", tag.Id);
                await Settle(250);
                if (window.FindSetting("SideBarScrollViewer") is FrameworkElement sidebar)
                {
                    Save(sidebar, Path.Combine(folder, "00-sidebar-filter.png"), (Brush)window.FindResource("Bg.Sidebar"));
                }

                Call(window, "SetTagFilter", [null]);
                await Settle(150);
            }

            if (Wanted(only, "autoscroll"))
            {
                await ShootAutoScroll(window, folder);
            }

            if (Wanted(only, "health"))
            {
                await ShootHealth(window, folder);
            }

            if (Wanted(only, "dialogs"))
            {
                await ShootDialogs(window, services, folder);
            }

            Call(window, "SettingsButton_Click", window, new RoutedEventArgs());
            await Settle(500);
            var card = (FrameworkElement)window.FindSetting("SettingsCard");
            var index = 0;
            foreach (var name in NavItems)
            {
                index++;
                if (window.FindSetting(name) is not RadioButton nav || !Wanted(only, name))
                {
                    continue;
                }

                nav.IsChecked = true;
                await Settle(450);
                var prefix = $"{index:00}-{name[3..].ToLowerInvariant()}";
                if (name == "NavKey" && window.FindSetting("KeyPage") is SettingsKeyPage keyPage)
                {
                    keyPage.ShowForShot(SampleReport(), SampleKeys());
                    await Settle(200);
                }

                Save(card, Path.Combine(folder, prefix + ".png"), null);

                // Меню ключа (1.32.0) и выбор профиля для «Отправить»: второй профиль — на время снимка.
                if (name == "NavKey" && window.FindSetting("KeyPage") is SettingsKeyPage menuPage &&
                    Descendants<Button>(menuPage).FirstOrDefault(button => Convert.ToString(button.ToolTip) == Loc.Get("S.Chat.RowActions")) is { } more)
                {
                    var other = new UserProfile { Id = "shot-profile", Name = language == "ru" ? "Работа" : "Work" };
                    services.ProfileRegistry.Profiles.Add(other);
                    try
                    {
                        more.Opacity = 1;
                        menuPage.OpenKeyMenu(more, SampleKeys()[1], PlacementMode.Bottom);
                        await Settle(300);
                        if (OpenMenu() is { } keyMenu)
                        {
                            Save(keyMenu, Path.Combine(folder, prefix + "-key-menu.png"), null);
                            keyMenu.IsOpen = false;
                        }

                        Call(menuPage, "OpenSendMenu", more, SampleKeys()[1], PlacementMode.Bottom);
                        await Settle(300);
                        if (OpenMenu() is { } sendMenu)
                        {
                            Save(sendMenu, Path.Combine(folder, prefix + "-key-send.png"), null);
                            sendMenu.IsOpen = false;
                        }
                    }
                    finally
                    {
                        services.ProfileRegistry.Profiles.Remove(other);
                    }
                }

                // Раскрытая выпадашка: у неё своё окно, снимок страницы её не видит.
                if (name == "NavGeneral" &&
                    Descendants<ComboBox>(card).FirstOrDefault(combo => combo.IsVisible) is { } dropdown)
                {
                    dropdown.IsDropDownOpen = true;
                    await Settle(350);
                    if (Descendants<Popup>(dropdown).FirstOrDefault() is { Child: FrameworkElement list })
                    {
                        Save(list, Path.Combine(folder, prefix + "-dropdown.png"), null);
                    }

                    dropdown.IsDropDownOpen = false;
                    await Settle(200);
                }

                if (name == "NavInstructions" && window.FindSetting("InstructionsPage") is SettingsInstructionsPage instructions)
                {
                    Call(instructions, "OpenEditor", [null]);
                    await Settle(300);
                    Save(card, Path.Combine(folder, prefix + "-editor.png"), null);
                    Call(instructions, "CloseEditor");
                    await Settle(200);
                }

                if (name == "NavAutomation")
                {
                    foreach (var tab in new[] { "ScheduleTab", "MachinesTab", "McpTab" })
                    {
                        if (Descendants<RadioButton>(card).FirstOrDefault(r => r.Name == tab) is { } radio)
                        {
                            radio.IsChecked = true;
                            await Settle(300);
                            var short_ = tab.Replace("Tab", "").ToLowerInvariant();
                            Save(card, Path.Combine(folder, $"{prefix}-{short_}.png"), null);

                            // Редактор вкладки: заголовок и вкладки страницы над ним уходят.
                            var (panelName, open) = tab switch
                            {
                                "ScheduleTab" => ("SchedulePane", "Create_Click"),
                                "MachinesTab" => ("MachinesPane", "Add_Click"),
                                _ => ("McpPane", "Add_Click")
                            };
                            if (Descendants<UserControl>(card).FirstOrDefault(c => c.Name == panelName) is { } panel)
                            {
                                Call(panel, open, panel, new RoutedEventArgs());
                                await Settle(300);
                                Save(card, Path.Combine(folder, $"{prefix}-{short_}-editor.png"), null);
                                Call(panel, "Back_Click", panel, new RoutedEventArgs());
                                await Settle(200);
                            }
                        }
                    }

                    if (window.FindSetting("AutomationPage") is SettingsAutomationPage automation)
                    {
                        if (Descendants<RadioButton>(card).FirstOrDefault(r => r.Name == "RecipesTab") is { } first)
                        {
                            first.IsChecked = true;
                        }

                        Call(automation, "Create_Click", automation, new RoutedEventArgs());
                        await Settle(300);
                        Save(card, Path.Combine(folder, $"{prefix}-recipe-editor.png"), null);
                        Call(automation, "Back_Click", automation, new RoutedEventArgs());
                        await Settle(200);
                    }

                    if (Descendants<RadioButton>(card).FirstOrDefault(r => r.Name == "RecipesTab") is { } recipes)
                    {
                        recipes.IsChecked = true;
                    }
                }

                var page = VisiblePage(card);
                if (page is not null)
                {
                    ShootFull(page, Path.Combine(folder, prefix + "-full.png"));
                    var subIndex = 0;
                    foreach (var link in Descendants<ButtonBase>(page).Where(b => b.IsVisible && SettingsDrillOpens(b) is not null).ToList())
                    {
                        subIndex++;
                        link.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, link));
                        await Settle(350);

                        // Подстраница переводит фокус на «назад», и без настоящего щелчка мышью WPF
                        // рисует вокруг неё рамку клавиатурного фокуса — у человека её нет.
                        Keyboard.ClearFocus();
                        await Settle(50);
                        Save(card, Path.Combine(folder, $"{prefix}-sub{subIndex}.png"), null);
                        ShootFull(page, Path.Combine(folder, $"{prefix}-sub{subIndex}-full.png"));
                        SettingsDrillBack(card);
                        await Settle(250);
                    }
                }
            }

            if (Wanted(only, "icons"))
            {
                ShootIcons(window, folder);
            }

            // Последними: съёмка подставляет автомату свои состояния, а настройки уже пройдены.
            if (Wanted(only, "updates"))
            {
                await ShootUpdates(window, folder);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Выделение рамкой (1.32.0): рамка посреди жеста — от подписи «Папки» вниз через папку и пару
    /// чатов, — выбранные строки с заголовком папки после отпускания и меню всего выбора.
    /// </summary>
    private async Task ShootSelection(MainWindow window, Panel panel, string folder)
    {
        if (window.FindSetting("SideBarScrollViewer") is not FrameworkElement sidebar ||
            typeof(MainWindow).GetProperty("Marquee", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) is not ChatListMarquee marquee)
        {
            return;
        }

        var title = panel.Children.OfType<TextBlock>().FirstOrDefault(block => block.Text == Loc.Get("S.ChatList.Folders"));
        var last = panel.Children.OfType<Button>().FirstOrDefault(button => button.Tag as string == "y1");
        if (title is null || last is null)
        {
            return;
        }

        marquee.RealMouse = false;
        try
        {
            marquee.Press(title.TranslatePoint(new Point(36, 4), panel), additive: false, onHeader: false);
            marquee.MoveTo(last.TranslatePoint(new Point(last.ActualWidth - 40, last.ActualHeight / 2), panel));
            await Settle(150);

            // Рамка рисуется на своём слое рядом со списком — снимается колонка целиком.
            var column = VisualTreeHelper.GetParent(sidebar) as FrameworkElement ?? sidebar;
            Save(column, Path.Combine(folder, "00-sidebar-marquee.png"), (Brush)window.FindResource("Bg.Sidebar"));

            marquee.Release();
            await Settle(200);
            Save(sidebar, Path.Combine(folder, "00-sidebar-selected.png"), (Brush)window.FindResource("Bg.Sidebar"));

            Call(window, "OpenSelectionMenu", last, PlacementMode.Bottom);
            await Settle(300);
            if (OpenMenu() is { } menu)
            {
                Save(menu, Path.Combine(folder, "00-menu-selection.png"), null);
                menu.IsOpen = false;
            }
        }
        finally
        {
            marquee.RealMouse = true;
            Call(window, "ClearChatSelection");
            await Settle(100);
        }
    }

    private static bool Wanted(string? only, string name) =>
        string.IsNullOrEmpty(only) || only.Split(',').Any(part => name.Contains(part.Trim(), StringComparison.OrdinalIgnoreCase));

    private static FrameworkElement? SettingsDrillOpens(DependencyObject button) =>
        typeof(MainWindow).Assembly.GetType("Amarin.UI.SettingsDrill") is { } drill
            ? drill.GetMethod("GetOpens", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, [button]) as FrameworkElement
            : null;

    private static void SettingsDrillBack(DependencyObject scope) =>
        typeof(MainWindow).Assembly.GetType("Amarin.UI.SettingsDrill")?
            .GetMethod("TryBackIn", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, [scope]);

    /// <summary>
    /// Метка автопрокрутки средней кнопкой (1.33.0) посреди ленты: в покое и когда лента едет вниз.
    /// Снимается корень шаблона окна — метка живёт в его слое украшений, а не в содержимом.
    /// </summary>
    private static async Task ShootAutoScroll(MainWindow window, string folder)
    {
        var feed = (ScrollViewer)window.FindSetting("ChatScrollViewer")!;
        if (VisualTreeHelper.GetChild(window, 0) is not FrameworkElement root)
        {
            return;
        }

        // Разговор снимков короче окна, и листать ленте нечего: на время снимка окно ниже.
        var height = window.Height;
        window.Height = 420;
        await Settle(300);
        var origin = new Point(feed.ActualWidth / 2, feed.ActualHeight / 2);
        if (!SmoothScroll.BeginAutoScroll(feed, origin))
        {
            window.Height = height;
            return;
        }

        try
        {
            // Кадр с подставленной мышью — прямо перед снимком: между снимками крутится настоящий
            // цикл кадров, а у него мышь настоящая, и где она относительно окна за краем экрана —
            // не угадать.
            var corner = feed.TranslatePoint(new Point(origin.X - 90, origin.Y - 70), root);
            var region = new Rect(corner, new Size(180, 140));
            await Settle(150);
            SmoothScroll.AutoScrollFrame(feed, origin, 1.0 / 60);
            root.UpdateLayout();
            Save(root, Path.Combine(folder, "00-autoscroll-idle.png"), null, region, scale: 3);
            SmoothScroll.AutoScrollFrame(feed, new Point(origin.X, origin.Y + 90), 1.0 / 60);
            root.UpdateLayout();
            Save(root, Path.Combine(folder, "00-autoscroll-down.png"), null, region, scale: 3);
        }
        finally
        {
            SmoothScroll.EndAutoScroll(feed);
            window.Height = height;
            await Settle(200);
        }
    }

    private async Task ShootHealth(MainWindow window, string folder)
    {
        if (window.FindSetting("HealthOverlay") is not HealthPanel panel)
        {
            return;
        }

        var slow = new TaskCompletionSource<HealthCard>();
        panel.Probes =
        [
            _ => Task.FromResult(HealthRules.Disks(
                [new DriveHealth("C:", 499_000_000_000, 120_000_000_000), new DriveHealth("D:", 3_740_000_000_000, 1_940_000_000_000)],
                [new PhysicalDiskHealth("Samsung SSD 980", "Healthy", "OK"), new PhysicalDiskHealth("WD Blue", "Healthy", "OK")])),
            _ => Task.FromResult(HealthRules.System(new SystemHealth(59, 49, TimeSpan.FromDays(1.3)))),
            _ => Task.FromResult(HealthRules.Security(new SecurityHealth(
                new DefenderHealth(true, true, true, 1), [new FirewallHealth("Domain", true), new FirewallHealth("Private", true), new FirewallHealth("Public", true)]))),
            _ => Task.FromResult(HealthRules.Stability(new EventHealth(0, 260, 0))),
            _ => slow.Task
        ];

        panel.Visibility = Visibility.Visible;
        Call(panel, "Refresh");
        await Settle(400);
        Save((FrameworkElement)window.Content, Path.Combine(folder, "00-health-checking.png"), null);

        slow.SetResult(HealthRules.Updates(new UpdateHealth(false, 3)));
        await Settle(400);
        Save((FrameworkElement)window.Content, Path.Combine(folder, "00-health.png"), null);
        panel.Cancel();
        panel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Всплывающие окна — каждое открывается тем же путём, что в программе, снимается и
    /// закрывается. Сбой одного окна записывается в dialogs-errors.txt и не останавливает остальные.
    /// </summary>
    private async Task ShootDialogs(MainWindow window, AppServices services, string folder)
    {
        var errors = new List<string>();
        var content = (FrameworkElement)window.Content;

        async Task Shot(string name, Func<Task> open, Action close)
        {
            try
            {
                await open();
                await Settle(300);
                Save(content, Path.Combine(folder, "dlg-" + name + ".png"), null);
            }
            catch (Exception ex)
            {
                errors.Add(name + ": " + (ex.InnerException ?? ex).Message);
            }
            finally
            {
                try
                {
                    close();
                }
                catch (Exception ex)
                {
                    errors.Add(name + " (close): " + (ex.InnerException ?? ex).Message);
                }

                await Settle(120);
            }
        }

        var args = new RoutedEventArgs();
        await Shot("rename-chat", () => { Call(window, "RenameChats", new List<string> { "y1" }); return Task.CompletedTask; },
            () => Call(window, "NameCancelButton_Click", window, args));
        await Shot("new-tag", () => { Call(window, "CreateTag", [null]); return Task.CompletedTask; },
            () => Call(window, "NameCancelButton_Click", window, args));
        await Shot("key-add", () => { Call(window, "OpenKeyDialog"); return Task.CompletedTask; },
            () => Call(window, "KeyDialogCancelButton_Click", window, args));
        await Shot("key-remove", () => { window.AskKeyRemoval("OpenRouter", "S.Key.DeleteDesc", () => { }); return Task.CompletedTask; },
            () => Call(window, "KeyRemoveNoButton_Click", window, args));
        await Shot("key-rename", () => { window.AskKeyRename("OpenRouter", _ => { }); return Task.CompletedTask; },
            () => Call(window, "KeyRenameCancelButton_Click", window, args));
        await Shot("delete-chat", () =>
            {
                _ = window.ShowNoticeAsync(
                    Loc.Get("S.ChatList.DeleteTitle"),
                    Loc.Format("S.ChatList.DeleteConfirm", "«Волк и Леопард»"),
                    Loc.Get("S.Common.Delete"),
                    Loc.Get("S.Common.Cancel"),
                    NoticeTone.Danger);
                return Task.CompletedTask;
            },
            () => Call(window, "NoticeSecondaryButton_Click", window, args));
        await Shot("domain", () => { Call(window, "OpenDomainDialog", "download.example.com"); return Task.CompletedTask; },
            () => Call(window, "DomainCancelButton_Click", window, args));
        await Shot("confirm-powershell", () =>
            {
                using var json = System.Text.Json.JsonDocument.Parse(
                    """{"command":"Get-Service -Name Spooler | Restart-Service -Force\nGet-Service -Name Spooler","explanation":"Перезапускаю службу печати, чтобы снять зависшую очередь."}""");
                var info = DangerousActionGuard.DescribeDetailed("run_powershell", json.RootElement.Clone());
                _ = services.Confirmations.ConfirmDetailedAsync("Агент", info, "y1");
                return Task.CompletedTask;
            },
            () => services.Confirmations.CancelAll());
        await Shot("plan", () =>
            {
                var plan = new AgentPlan
                {
                    Summary = "Освободить место на диске C:",
                    Steps =
                    [
                        new PlanStep("Посмотреть, что занимает место в папке Temp", "run_powershell", Script: "Get-ChildItem $env:TEMP -Recurse | Measure-Object Length -Sum"),
                        new PlanStep("Удалить временные файлы старше 7 дней", "run_powershell", Script: "Get-ChildItem $env:TEMP | Where LastWriteTime -lt (Get-Date).AddDays(-7) | Remove-Item -Recurse", ChangesSystem: true),
                        new PlanStep("Очистить корзину", "run_powershell", Script: "Clear-RecycleBin -Force", ChangesSystem: true)
                    ]
                };
                _ = services.PlanReviews.ReviewAsync("Агент", plan, "y1", CancellationToken.None);
                return Task.CompletedTask;
            },
            () => services.PlanReviews.CancelAll());
        await Shot("profile", () => { Call(window, "SwitchAccountButton_Click", window, args); return Task.CompletedTask; },
            () => ((FrameworkElement)window.FindSetting("ProfileOverlay")).Visibility = Visibility.Collapsed);
        await Shot("export", () => (Task)Call(window, "OpenExportAsync")!,
            () => ((FrameworkElement)window.FindSetting("DataExportOverlay")).Visibility = Visibility.Collapsed);
        await Shot("chat-settings", () => { window.OpenChatSettings(); return Task.CompletedTask; },
            () => ((FrameworkElement)window.FindSetting("ChatSettings")!).Visibility = Visibility.Collapsed);
        await Shot("prompt-preset", () => { Call(window, "OpenPromptEditor", [null]); return Task.CompletedTask; },
            () => ((FrameworkElement)window.FindSetting("PromptPresetOverlay")).Visibility = Visibility.Collapsed);

        // Окно пароля — отдельное окно, а не слой: снимается его карточка.
        try
        {
            var password = new PasswordWindow(null, confirmTwice: true)
            {
                Left = -32000,
                Top = 0,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            password.HeadingText.Text = Loc.Get("S.Password.NewTitle");
            password.SubtitleText.Text = Loc.Get("S.Password.NewDesc");
            password.OkButton.Content = Loc.Get("S.Common.Save");
            password.Show();
            await Settle(250);
            Save((FrameworkElement)password.FindName("Card"), Path.Combine(folder, "dlg-password-new.png"), null);
            password.Close();
        }
        catch (Exception ex)
        {
            errors.Add("password: " + (ex.InnerException ?? ex).Message);
        }

        if (errors.Count > 0)
        {
            File.WriteAllLines(Path.Combine(folder, "dialogs-errors.txt"), errors);
        }
    }

    private static void Seed(AppServices services)
    {
        var now = DateTime.Now;
        void Chat(string id, string title, double daysAgo, bool pinned = false)
        {
            services.ChatStore.Save(new ChatSession
            {
                Id = id,
                Title = title,
                CreatedAt = now.AddDays(-daysAgo),
                UpdatedAt = now.AddDays(-daysAgo)
            });
            if (pinned)
            {
                services.ChatStore.SetPinned(id, true);
            }
        }

        Chat("p1", "ファイル内容の質問整理", 3, pinned: true);
        Chat("p2", "Синонимы, антонимы и паронимы", 4, pinned: true);
        Chat("f1", "Папки и файлы", 0.1);
        Chat("f2", "Очистка диска D:", 2);
        Chat("y1", "Волк и Леопард", 1);
        Chat("e1", "Драйвер Wi-Fi после обновления", 6);
        Chat("e2", "New chat", 9);
        Chat("e3", "Почему тормозит браузер", 12);
        services.ChatStore.Save(SampleConversation(now));
        services.ChatStore.Flush();

        var folder = services.Organizer.CreateFolder("77");
        services.Organizer.MoveToFolder(["f1", "f2"], folder.Id);
        var shut = services.Organizer.CreateFolder("Работа");
        services.Organizer.MoveToFolder(["e3"], shut.Id);
        services.Organizer.SetCollapsed(shut.Id, true);
        var test = services.Organizer.CreateTag("Тестовый", "Status.Danger");
        var furry = services.Organizer.CreateTag("Фурри промпты", "Status.Warning");
        services.Organizer.ToggleTag(["y1"], furry.Id);
        services.Organizer.ToggleTag(["e1"], test.Id);
    }

    /// <summary>Вопрос человека и ответ модели с раундом инструментов и блоком кода.</summary>
    private static ChatSession SampleConversation(DateTime now)
    {
        var session = new ChatSession
        {
            Id = "c7",
            Title = "Установка 7-Zip",
            CreatedAt = now.AddMinutes(-3),
            UpdatedAt = now.AddMinutes(-2)
        };
        session.Messages.Add(new ChatDisplayMessage
        {
            Role = "user",
            Id = "u1",
            CreatedAt = now.AddMinutes(-3),
            Text = "Поставь 7-Zip и проверь, что архивы открываются"
        });
        session.Messages.Add(new ChatDisplayMessage
        {
            Role = "assistant",
            Id = "a1",
            CreatedAt = now.AddMinutes(-2),
            ResolvedModelId = "claude-sonnet-5",
            Duration = TimeSpan.FromSeconds(96),
            Cost = new VeniceCost { Usd = 0.0263m, HasData = true },
            Status = AssistantStatus.Complete,
            ToolRounds =
            [
                new ToolRound
                {
                    InfoLine = EngineLines.ToolsDone,
                    Calls =
                    [
                        new ToolCallRecord { Id = "t1", Name = "download_file", ArgumentsJson = "{}", Success = true, Status = ToolCallStatus.Done },
                        new ToolCallRecord { Id = "t2", Name = "run_powershell", ArgumentsJson = "{}", Success = true, Status = ToolCallStatus.Done },
                        new ToolCallRecord { Id = "t3", Name = "run_powershell", ArgumentsJson = "{}", Success = true, Status = ToolCallStatus.Done }
                    ]
                }
            ],
            Text = """
                   Готово — **7-Zip 25.01 (x64)** установлен, архивы `.7z` и `.zip` открываются.

                   1. Скачал установщик с 7-zip.org, сверил сумму и поставил без окон в `C:\Program Files\7-Zip`.
                   2. Проверил распаковку тестового архива:

                   ```powershell
                   PS> & "C:\Program Files\7-Zip\7z.exe" t .\test.7z
                   Everything is Ok
                   Files: 3    Size: 48 214
                   ```
                   """
        });
        return session;
    }

    private static ContextMenu? OpenMenu() =>
        PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>()
            .Select(source => source.RootVisual)
            .OfType<DependencyObject>()
            .SelectMany(root => root is ContextMenu menu ? [menu] : Descendants<ContextMenu>(root))
            .LastOrDefault(menu => menu.IsOpen);

    private static FrameworkElement? VisiblePage(FrameworkElement card)
    {
        // Страница — видимый ScrollViewer или страница-UserControl во второй колонке карточки.
        var content = Descendants<Grid>(card).FirstOrDefault(grid => Grid.GetColumn(grid) == 1);
        return content?.Children.OfType<FrameworkElement>()
            .Where(child => child.Visibility == Visibility.Visible && child is not Button)
            .LastOrDefault();
    }

    private static void ShootFull(FrameworkElement page, string path)
    {
        var scroll = page as ScrollViewer ?? Descendants<ScrollViewer>(page).FirstOrDefault(s => s.IsVisible);
        if (scroll?.Content is not FrameworkElement content || content.ActualHeight <= 0)
        {
            return;
        }

        // Длинная страница — кусками по 900 точек: картинку в несколько тысяч точек высотой
        // при просмотре ужимают так, что текст уже не прочесть.
        const double Slice = 900;
        // Подложка — та же, на которой страница лежит в окне настроек: карточки групп (Bg.Panel)
        // на ней видны так же, как на экране.
        var background = (Brush)page.FindResource("Bg.Window");
        if (content.ActualHeight <= Slice * 1.3)
        {
            Save(content, path, background);
            return;
        }

        for (var top = 0.0; top < content.ActualHeight; top += Slice)
        {
            var part = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-" + (int)(top / Slice + 1) + ".png");
            Save(content, part, background, new Rect(0, top, content.ActualWidth, Math.Min(Slice, content.ActualHeight - top)));
        }
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

            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
            {
                stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    /// <summary>
    /// Обновления во всех состояниях: значок в шапке, его попап и карточка на странице данных.
    /// Состояние подаётся прямо в автомат — сеть и файлы не нужны.
    /// </summary>
    private static async Task ShootUpdates(MainWindow window, string folder)
    {
        var release = UpdateTestKit.Release("99.0.0");
        var before = window.Updates.State;
        var title = (FrameworkElement)((FrameworkElement)window.FindSetting("TitleText")).Parent;
        var badge = (System.Windows.Controls.Primitives.ToggleButton)window.FindSetting("UpdateBadgeButton");
        var popup = (Popup)window.FindSetting("UpdateBadgePopup");
        var states = new (string Name, UpdateState State)[]
        {
            ("none", UpdateState.Initial with { LastSuccessUtc = DateTime.UtcNow }),
            ("found", UpdateState.Initial with { Latest = release }),
            ("background", UpdateState.Initial with
            {
                Latest = release,
                Download = new UpdateDownload(release, UpdateDownloadOrigin.Background, false, false, 0.42, 1, null),
                DownloadGeneration = 1
            }),
            ("joined", UpdateState.Initial with
            {
                Latest = release,
                Download = new UpdateDownload(release, UpdateDownloadOrigin.Background, true, false, 0.42, 1, null),
                DownloadGeneration = 1
            }),
            ("postponed", UpdateState.Initial with { Latest = release, Postponed = release.Release }),
            ("ready", UpdateState.Initial with { Latest = release, Staged = UpdateTestKit.Staged(release) }),
            ("installing", UpdateState.Initial with { Latest = release, Installing = UpdateTestKit.Staged(release) }),
            ("failed", UpdateState.Initial with { Latest = release, Failure = new(release.Release, "Контрольная сумма не совпала") })
        };

        try
        {
            foreach (var (name, state) in states)
            {
                window.Updates.Seed(_ => state);
                await Settle(150);
                Save(title, Path.Combine(folder, $"upd-title-{name}.png"), (Brush)window.FindResource("Bg.Window"),
                    new Rect(Math.Max(0, title.ActualWidth - 240), 0, Math.Min(240, title.ActualWidth), title.ActualHeight));
                if (badge.Visibility == Visibility.Visible)
                {
                    badge.IsChecked = true;
                    await Settle(250);
                    if (popup.Child is FrameworkElement card)
                    {
                        Save(card, Path.Combine(folder, $"upd-popup-{name}.png"), null);
                    }

                    badge.IsChecked = false;
                    await Settle(100);
                }
            }

            // Карточка в настройках — те же состояния.
            Call(window, "SettingsButton_Click", window, new RoutedEventArgs());
            ((RadioButton)window.FindSetting("NavData")).IsChecked = true;
            await Settle(450);
            var updateCard = Ancestor<Border>((DependencyObject)window.FindSetting("UpdateVersionText"), border => border.BorderThickness.Left >= 1);
            foreach (var (name, state) in states)
            {
                window.Updates.Seed(_ => state);
                await Settle(150);
                if (updateCard is not null)
                {
                    Save(updateCard, Path.Combine(folder, $"upd-card-{name}.png"), (Brush)window.FindResource("Bg.Panel"));
                }
            }

            Call(window, "SettingsCloseButton_Click", window, new RoutedEventArgs());
        }
        finally
        {
            window.Updates.Seed(_ => before);
        }
    }

    private static T? Ancestor<T>(DependencyObject start, Func<T, bool> match) where T : DependencyObject
    {
        for (var node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is T found && match(found))
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Лист значков навигации: каждый крупно (×4) и в настоящем размере — тем же пером и на той
    /// же плитке, что у пункта, — при 100 % и 150 %, на подложке окна настроек этой палитры. На
    /// нём видно то, чего не видно в колонке: слипшиеся в пятно штрихи и знак не по центру.
    /// </summary>
    private static void ShootIcons(MainWindow window, string folder)
    {
        static Border Tile(RadioButton nav, double tile, double icon, double pen) => new()
        {
            Width = tile,
            Height = tile,
            CornerRadius = new CornerRadius(tile / 2),
            Background = nav.Background,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new System.Windows.Shapes.Path
            {
                Data = (Geometry)nav.Tag,
                Width = icon,
                Height = icon,
                Stretch = Stretch.Uniform,
                Stroke = Brushes.White,
                StrokeThickness = pen,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var sheet = new StackPanel { Margin = new Thickness(16) };
        foreach (var name in NavItems)
        {
            var nav = (RadioButton)window.FindSetting(name);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            row.Children.Add(Tile(nav, 80, 48, 1.3 * 4));
            row.Children.Add(Tile(nav, 20, 12, 1.3));
            ((FrameworkElement)row.Children[1]).Margin = new Thickness(20, 0, 12, 0);
            row.Children.Add(new TextBlock
            {
                Text = nav.Content as string,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)window.FindResource("Text.Secondary")
            });
            sheet.Children.Add(row);
        }

        var background = (Brush)window.FindResource("Bg.Window");
        var host = new Border { Child = sheet, Background = background };
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        Save(host, Path.Combine(folder, "icons-150.png"), background);
        Save(host, Path.Combine(folder, "icons-100.png"), background, scale: 1.0);
    }

    private static void Save(FrameworkElement element, string path, Brush? background, Rect? region = null, double scale = Scale)
    {
        var source = region ?? new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var width = source.Width;
        var height = source.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var area = new Rect(0, 0, width, height);
            if (background is not null)
            {
                context.DrawRectangle(background, null, area);
            }

            context.DrawRectangle(
                new VisualBrush(element)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                    ViewboxUnits = BrushMappingMode.Absolute,
                    // Кисть рисует элемент вместе с его смещением (полями): окно снимка сдвигаем
                    // на то же смещение, иначе страница уезжает вправо и обрезается.
                    Viewbox = new Rect(source.X + VisualTreeHelper.GetOffset(element).X, source.Y + VisualTreeHelper.GetOffset(element).Y, source.Width, source.Height),
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = area
                },
                null,
                area);
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task Settle(int milliseconds) => await Task.Delay(milliseconds);

    private static object? Call(object target, string method, params object?[] args) =>
        target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(target, args);

    private static SpendReport SampleReport()
    {
        var today = DateTime.Now.Date;
        decimal[] daily = [0.021m, 0.134m, 0.068m, 0.312m, 0.095m, 0.186m, 0.043m];
        return new SpendReport
        {
            Status = SpendStatus.Local,
            Period = SpendPeriod.Week,
            Points = daily.Select((value, i) => new SpendPoint(today.AddDays(i - 6), value, 0m, 3)).ToList(),
            TotalUsd = daily.Sum(),
            Models =
            [
                new SpendModelRow("Grok 4.6", "grok-4-6", 0.512m, 0m, 14),
                new SpendModelRow("Claude Sonnet 5", "claude-sonnet-5", 0.221m, 0m, 6)
            ]
        };
    }

    private static IReadOnlyList<ApiKeyEntry> SampleKeys() =>
    [
        new("environment", "VENICE_API_KEY", "VENabcdefghijklmnopqrstuvIyuC", ApiKeySource.Environment, true),
        new("k2", "OpenRouter", "sk-or-second-key-abcdefghijkl3F7q", ApiKeySource.Stored, false, LlmProvider.OpenRouter)
    ];
}
