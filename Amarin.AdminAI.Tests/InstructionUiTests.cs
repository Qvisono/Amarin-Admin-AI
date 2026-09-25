using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Интерфейс 1.27.0 на живом WPF: логотип колонки, страница «Инструкции», отметка «по
/// инструкции» под ответом и подсказки плашки моделей.
/// </summary>
/// <remarks>
/// Всё, что пишет на диск, работает со своим окном и своей временной папкой: у общего окна
/// тестов службы смотрят в настоящий <c>%APPDATA%</c>, а файлы человека тесты не трогают.
/// Своё окно не показывается и закрывается в finally — иначе соседние тесты увидели бы два.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class InstructionUiTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly List<string> _roots = [];

    public InstructionUiTests(WpfFixture wpf) => _wpf = wpf;

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ───────────────────────── оснастка ─────────────────────────

    private static MainWindow SharedWindow() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    private AppServices Services()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-instr-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "chats"));
        _roots.Add(root);

        var options = new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            MaxToolRounds = 2
        };

        var http = new HttpClient { BaseAddress = new Uri("https://example.invalid/") };
        var download = new HttpClient { BaseAddress = new Uri("https://example.invalid/") };
        var venice = new VeniceClient(http, options);
        var settingsStore = new AppSettingsStore(root);
        var settings = settingsStore.Load();

        return new AppServices
        {
            Options = options,
            SettingsStore = settingsStore,
            Settings = settings,
            ChatStore = new ChatStore(root),
            Prompts = new PromptLibrary(root),
            Instructions = new InstructionLibrary(root),
            KeyStore = new ApiKeyStore(root),
            Ledger = new SpendLedger(root),
            Keys = new ApiKeyProvider(),
            EnvironmentKey = "",
            Profiles = new ProfileStore(),
            ProfileRegistry = new ProfileRegistry(),
            Http = http,
            DownloadHttp = download,
            Venice = venice,
            Models = new VeniceModelListCache(venice),
            Balances = new BalanceBook(),
            Chat = new ChatEngine(venice, options, () => settings, new ToolRegistry([])),
            Titles = new ChatTitleGenerator(http, options, () => settings),
            Summaries = new ChatSummaryGenerator(http, options, () => settings),
            Confirmations = new ConfirmationQueue(() => settings)
        };
    }

    private T WithWindow<T>(Func<MainWindow, T> body) => _wpf.Ui.Invoke(() =>
    {
        var window = new MainWindow();
        window.AttachServices(Services());
        try
        {
            return body(window);
        }
        finally
        {
            window.Close();
        }
    });

    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(window, args);

    private static void Click(ButtonBase button) =>
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    private static SettingsInstructionsPage Page(AppServices services)
    {
        var page = new SettingsInstructionsPage();
        page.Attach(services);
        page.Activate();
        Layout(page);
        return page;
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(520, 460));
        element.Arrange(new Rect(0, 0, 520, 460));
        element.UpdateLayout();
    }

    // ───────────────────────── логотип колонки ─────────────────────────

    [Fact]
    public void The_logo_of_an_open_sidebar_starts_a_new_chat()
    {
        var (before, after) = WithWindow(window =>
        {
            var before = Field<ChatSession>(window, "_session");
            Click((Button)window.FindName("SidebarLogoButton"));
            return (before, Field<ChatSession>(window, "_session"));
        });

        Assert.NotSame(before, after);
        Assert.NotEqual(before.Id, after.Id);
    }

    [Fact]
    public void The_logo_of_a_folded_sidebar_only_unfolds_it()
    {
        var (sameChat, folded, tipFolded, tipOpen) = WithWindow(window =>
        {
            Call(window, "SetSidebarCollapsed", true);
            var logo = (Button)window.FindName("SidebarLogoButton");
            var tipFolded = logo.ToolTip as string;
            var before = Field<ChatSession>(window, "_session");

            Click(logo);

            return (ReferenceEquals(before, Field<ChatSession>(window, "_session")),
                    Field<bool>(window, "_sidebarCollapsed"),
                    tipFolded,
                    logo.ToolTip as string);
        });

        Assert.True(sameChat, "разворот колонки не должен заводить чат");
        Assert.False(folded);
        Assert.Equal(Loc.Get("S.Sidebar.Expand"), tipFolded);
        Assert.Equal(Loc.Get("S.ChatList.NewChat"), tipOpen);
    }

    // ───────────────────────── навигация настроек ─────────────────────────

    [Fact]
    public void Instructions_sit_in_the_model_group_after_customize()
    {
        var order = _wpf.Ui.Invoke(() =>
        {
            var nav = (Panel)LogicalTreeHelper.GetParent((DependencyObject)SharedWindow().FindName("NavInstructions"));
            return nav.Children.OfType<FrameworkElement>()
                .Select(child => child.Name)
                .Where(name => name.Length > 0)
                .ToList();
        });

        Assert.Equal(order.IndexOf("NavCustomize") + 1, order.IndexOf("NavInstructions"));
        Assert.True(order.IndexOf("NavInstructions") < order.IndexOf("NavKey"));
    }

    /// <summary>
    /// С восьмым пунктом колонка стала длиннее, а плашка версии с надписью об обновлении
    /// переносится в две строки. Наезд на неё виден только тогда, когда обновление и правда
    /// есть, — поэтому меряем с этой надписью.
    /// </summary>
    [Fact]
    public void The_last_navigation_item_stays_clear_of_the_version_card()
    {
        var (itemBottom, cardTop) = _wpf.Ui.Invoke(() =>
        {
            var window = SharedWindow();
            var overlay = (FrameworkElement)window.FindName("SettingsOverlay");
            var version = (TextBlock)window.FindName("SettingsVersionText");
            var card = (FrameworkElement)window.FindName("SettingsAboutCard");
            var last = (FrameworkElement)window.FindName("NavInfo");
            var restore = version.Text;

            overlay.Visibility = Visibility.Visible;
            version.Text = Loc.Format("S.Updates.SidebarBadge", "1.27.10");
            window.UpdateLayout();
            try
            {
                var bottom = last.TranslatePoint(new Point(0, last.ActualHeight), overlay).Y;
                var top = card.TranslatePoint(new Point(0, 0), overlay).Y;
                return (bottom, top);
            }
            finally
            {
                version.Text = restore;
                overlay.Visibility = Visibility.Collapsed;
            }
        });

        Assert.True(itemBottom <= cardTop, $"пункт кончается на {itemBottom}, плашка начинается на {cardTop}");
    }

    // ───────────────────────── страница «Инструкции» ─────────────────────────

    /// <summary>
    /// Страница копирует стили настроек к себе, и опечатка в ключе видна только при разборе —
    /// поэтому собираем и список, и редактор.
    /// </summary>
    [Fact]
    public void The_page_builds_in_both_states() =>
        _wpf.Ui.Invoke(() =>
        {
            var page = Page(Services());
            Click(page.CreateButton);
            Layout(page);
            Assert.True(page.IsEditing);
            return true;
        });

    [Fact]
    public void An_empty_library_shows_the_invitation_and_no_cards()
    {
        var (empty, items, search) = _wpf.Ui.Invoke(() =>
        {
            var page = Page(Services());
            return (page.EmptyState.Visibility, page.InstructionItems.Items.Count, page.SearchFrame.Visibility);
        });

        Assert.Equal(Visibility.Visible, empty);
        Assert.Equal(0, items);
        Assert.Equal(Visibility.Collapsed, search);
    }

    [Fact]
    public void Search_appears_once_the_list_grows()
    {
        var (few, many) = _wpf.Ui.Invoke(() =>
        {
            var services = Services();
            for (var i = 1; i < SettingsInstructionsPage.SearchThreshold; i++)
            {
                services.Instructions.Save(new Instruction { Name = "Инструкция " + i, Text = "t" });
            }

            var few = Page(services).SearchFrame.Visibility;
            services.Instructions.Save(new Instruction { Name = "Ещё одна", Text = "t" });
            return (few, Page(services).SearchFrame.Visibility);
        });

        Assert.Equal(Visibility.Collapsed, few);
        Assert.Equal(Visibility.Visible, many);
    }

    /// <summary>
    /// Ловушка из памятки: у <c>StackPanel</c> подвал уезжает ниже края на длинном тексте.
    /// Редактор — <c>Grid</c> со строкой «*», и «Сохранить» обязан остаться на странице.
    /// </summary>
    [Fact]
    public void The_save_button_stays_on_the_page_with_the_longest_text()
    {
        var (bottom, height) = _wpf.Ui.Invoke(() =>
        {
            var page = Page(Services());
            Click(page.CreateButton);
            page.BodyBox.Text = string.Join("\n", Enumerable.Repeat(new string('ж', 90), 110));
            Layout(page);
            var point = page.SaveButton.TranslatePoint(new Point(0, page.SaveButton.ActualHeight), page);
            return (point.Y, page.ActualHeight);
        });

        Assert.True(bottom <= height, $"низ кнопки {bottom} ниже края страницы {height}");
    }

    [Fact]
    public void Saving_checks_the_draft_and_then_writes_the_file()
    {
        var result = _wpf.Ui.Invoke(() =>
        {
            var services = Services();
            var page = Page(services);
            Click(page.CreateButton);

            var refused = page.Save();
            var error = page.ErrorText.Text;

            page.NameBox.Text = "Wallpaper Engine";
            page.BodyBox.Text = "Искать по ID в Workshop.";
            page.AddTriggers(["обои"]);
            var saved = page.Save();

            return (refused, error, saved, page.IsEditing,
                    services.Instructions.Snapshot(), page.InstructionItems.Items.Count);
        });

        Assert.False(result.refused);
        Assert.Equal(Loc.Get("S.Instructions.NameEmpty"), result.error);
        Assert.True(result.saved);
        Assert.False(result.IsEditing);
        var stored = Assert.Single(result.Item5);
        Assert.Equal("Wallpaper Engine", stored.Name);
        Assert.Equal(["обои"], stored.Triggers);
        Assert.Equal(1, result.Item6);
    }

    [Fact]
    public void A_name_that_is_already_taken_is_refused()
    {
        var (saved, error) = _wpf.Ui.Invoke(() =>
        {
            var services = Services();
            services.Instructions.Save(new Instruction { Name = "Сеть", Text = "t" });
            var page = Page(services);
            Click(page.CreateButton);
            page.NameBox.Text = "СЕТЬ";
            page.BodyBox.Text = "другая";
            return (page.Save(), page.ErrorText.Text);
        });

        Assert.False(saved);
        Assert.Equal(Loc.Get("S.Instructions.NameTaken"), error);
    }

    [Fact]
    public void A_comma_closes_a_trigger_and_repeats_are_ignored()
    {
        var (triggers, rest) = _wpf.Ui.Invoke(() =>
        {
            var page = Page(Services());
            Click(page.CreateButton);
            page.TriggerInput.Text = "обои, Workshop,";
            page.TriggerInput.Text += "ОБОИ,";
            page.TriggerInput.Text += "стим";
            return (page.DraftTriggers.ToList(), page.TriggerInput.Text);
        });

        Assert.Equal(["обои", "Workshop"], triggers);
        Assert.Equal("стим", rest);
    }

    [Fact]
    public void Cancelling_an_untouched_draft_needs_no_question()
    {
        var (dirty, editing) = _wpf.Ui.Invoke(() =>
        {
            var page = Page(Services());
            Click(page.CreateButton);
            var dirty = page.IsDirty;
            Click(page.CancelButton);
            return (dirty, page.IsEditing);
        });

        Assert.False(dirty);
        Assert.False(editing);
    }

    [Fact]
    public void Opening_an_instruction_by_id_fills_the_editor()
    {
        var (editingId, name, savedId) = _wpf.Ui.Invoke(() =>
        {
            var services = Services();
            var saved = services.Instructions.Save(new Instruction { Name = "По ссылке", Text = "t" })!;
            var page = Page(services);
            page.OpenInstruction(saved.Id);
            return (page.EditingId, page.NameBox.Text, saved.Id);
        });

        Assert.Equal(savedId, editingId);
        Assert.Equal("По ссылке", name);
    }

    [Theory]
    [InlineData("Сеть: DNS / VPN?", "Сеть_ DNS _ VPN_")]
    [InlineData("...", "instruction")]
    public void An_exported_file_name_has_nothing_windows_refuses(string name, string expected) =>
        Assert.Equal(expected, SettingsInstructionsPage.SafeFileName(name));

    // ───────────────────────── отметка в чате ─────────────────────────

    private static ChatDisplayMessage Answer(params InstructionRef?[] read) => new()
    {
        Role = "assistant",
        Id = "a1",
        ResolvedModelId = "grok-4-6",
        Status = AssistantStatus.Complete,
        Text = "Готово.",
        ToolRounds =
        [
            new ToolRound
            {
                Calls = [.. read.Select((instruction, i) => new ToolCallRecord
                {
                    Id = "c" + i,
                    Name = ReadInstructionTool.ToolName,
                    ArgumentsJson = "{}",
                    Status = instruction is null ? ToolCallStatus.Failed : ToolCallStatus.Done,
                    Success = instruction is not null,
                    Instruction = instruction
                })]
            }
        ]
    };

    [Fact]
    public void An_answer_that_read_an_instruction_is_marked_once_per_instruction()
    {
        var wallpaper = new InstructionRef("a1b2c3d4", "Wallpaper Engine");
        var (shown, chips, opened) = _wpf.Ui.Invoke(() =>
        {
            string? opened = null;
            var actions = new MessageActions
            {
                OpenInstruction = id => opened = id,
                InstructionExists = _ => true
            };

            var view = ChatMessageViews.CreateAssistant(SharedWindow(), Answer(wallpaper, wallpaper, null), actions);
            var strip = (Panel)view.InstructionsHost.Children[0];
            var chips = strip.Children.OfType<Button>().ToList();
            Click(chips[0]);
            return (view.InstructionsHost.Visibility, chips.Count, opened);
        });

        Assert.Equal(Visibility.Visible, shown);
        Assert.Equal(1, chips);
        Assert.Equal("a1b2c3d4", opened);
    }

    [Fact]
    public void A_deleted_instruction_leaves_a_mark_that_leads_nowhere()
    {
        var (enabled, tip) = _wpf.Ui.Invoke(() =>
        {
            var actions = new MessageActions
            {
                OpenInstruction = _ => throw new InvalidOperationException("открывать нечего"),
                InstructionExists = _ => false
            };

            var view = ChatMessageViews.CreateAssistant(
                SharedWindow(), Answer(new InstructionRef("gone", "Удалённая")), actions);
            var chip = ((Panel)view.InstructionsHost.Children[0]).Children.OfType<Button>().Single();
            return (chip.IsEnabled, chip.ToolTip as string);
        });

        Assert.False(enabled);
        Assert.Equal(Loc.Get("S.Instructions.UsedGone"), tip);
    }

    [Fact]
    public void An_answer_without_instructions_has_no_mark() =>
        Assert.Equal(
            Visibility.Collapsed,
            _wpf.Ui.Invoke(() => ChatMessageViews.CreateAssistant(SharedWindow(), Answer([null])).InstructionsHost.Visibility));

    // ───────────────────────── подсказки плашки моделей ─────────────────────────

    [Fact]
    public void Model_rows_wait_before_their_tip_and_do_not_chain_it()
    {
        var (initial, between, placement, hasCallback) = _wpf.Ui.Invoke(() =>
        {
            var panel = new ModelPickerPanel();
            var auto = (Button)panel.FindName("AutoItem");
            typeof(ModelPickerPanel)
                .GetMethod("ModelItem_ToolTipOpening", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(panel, [auto, null]);
            var tip = (ToolTip)auto.ToolTip;
            return (ToolTipService.GetInitialShowDelay(auto),
                    ToolTipService.GetBetweenShowDelay(auto),
                    tip.Placement,
                    tip.CustomPopupPlacementCallback is not null);
        });

        Assert.Equal(700, initial);
        Assert.Equal(0, between);
        Assert.Equal(PlacementMode.Custom, placement);
        Assert.True(hasCallback);
    }

    /// <summary>
    /// Подсказка встаёт справа от плашки, а если там нет экрана — слева; и в обоих случаях
    /// не перекрывает саму плашку. При масштабе 150 % сдвиги растут вместе с пикселями.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void The_tip_goes_beside_the_panel_not_over_it(double scale)
    {
        var popup = new Size(200 * scale, 40 * scale);
        var target = new Size(300 * scale, 28 * scale);

        var places = ModelPickerPanel.PlaceBeside(popup, target, rowLeft: 120, rowWidth: 300, frameWidth: 548);

        Assert.Equal(2, places.Length);
        Assert.Equal((548 - 120 + 8) * scale, places[0].Point.X, 6);
        Assert.Equal((28 - 40) / 2.0 * scale, places[0].Point.Y, 6);
        Assert.Equal(-(120 + 8) * scale - popup.Width, places[1].Point.X, 6);

        // Правый край левой подсказки — левее левого края плашки.
        Assert.True(places[1].Point.X + popup.Width <= -120 * scale);
    }
}
