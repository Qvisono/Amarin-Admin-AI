using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Настройки строятся не в конструкторе окна, а после первого кадра — в простое или по первому
/// открытию.
/// </summary>
/// <remarks>
/// Прежде весь оверлей настроек разбирался в конструкторе окна, хотя на первом кадре его не видно,
/// и это была заметная доля холодного запуска. Здесь сторожится обратное: окно до первого
/// открытия обходится без настроек, открытие раньше прогрева достраивает ровно то, что нужно, а
/// фоновые события (состояние обновлений, смена языка, перечитанный профиль) не строят страниц и
/// не теряются.
/// </remarks>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class SettingsLazyTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-settings-lazy-" + Guid.NewGuid().ToString("N"));

    public SettingsLazyTests(WpfFixture wpf) => _wpf = wpf;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка останется — не повод валить прогон.
        }
        catch (UnauthorizedAccessException)
        {
            // То же.
        }
    }

    [Fact]
    public void The_window_builds_no_settings_until_they_are_needed()
    {
        var states = _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow();
            var seen = new List<(string Step, bool Untouched)> { ("ctor", View(window) is null) };
            window.AttachServices(Services("ctor"));
            seen.Add(("attach", View(window) is null));

            // Всё, что окно зовёт из фона и при смене языка или профиля, — без открытых настроек.
            foreach (var method in (string[])
                     [
                         "LoadSettingsUi", "LoadAccountUi", "LoadUpdatesUi", "RenderUpdates", "RefreshBehaviorLinks",
                         "RefreshAppearanceLinks", "RefreshPromptLinks", "RefreshAllowedDomainsUi",
                         "PushKeysToPickers", "ShowSettingsModelSelections", "BindSettingsReasoningPickers",
                         "ShowSynGuardModelName", "UpdateTranslationEditButton"
                     ])
            {
                Call(window, method);
                seen.Add((method, View(window) is null));
            }

            Call(window, "ApplyAppearance", false);
            seen.Add(("ApplyAppearance", View(window) is null));
            seen.Add(("overlay", ((Panel)window.FindName("SettingsOverlay")!).Children.Count == 0));
            window.Close();
            return seen;
        });

        Assert.All(states, state => Assert.True(state.Untouched, state.Step + " построил настройки"));
    }

    [Fact]
    public async Task Idle_after_the_first_frame_builds_every_page_without_opening_them()
    {
        var (atFirstFrame, pages, overlay, themes, hotkeys) = await _wpf.Ui.Invoke(async () =>
        {
            var window = OffScreen(Services("idle"));
            var rendered = new TaskCompletionSource<bool>();
            window.ContentRendered += (_, _) => rendered.TrySetResult(View(window) is null);
            window.Show();
            try
            {
                var untouched = await rendered.Task;
                var watch = Stopwatch.StartNew();
                while (!Done(window) && watch.Elapsed < TimeSpan.FromSeconds(20))
                {
                    await Task.Delay(50);
                }

                var view = View(window);
                return (untouched, view?.BuiltPages.Count() ?? 0, ((FrameworkElement)window.FindName("SettingsOverlay")!).Visibility,
                    view?.Built<SettingsAppearancePage>()?.ThemeCardsHost.Children.Count ?? 0,
                    view?.Built<SettingsGeneralPage>()?.HotkeyList.Children.Count ?? 0);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(atFirstFrame, "к первому кадру настройки уже были построены");
        Assert.Equal(SettingsNavNames.All.Length, pages);
        Assert.Equal(Visibility.Collapsed, overlay);

        // Тяжёлое содержимое подстраниц прогрев достраивает своими порциями.
        Assert.Equal(ThemeCatalog.Presets.Count, themes);
        Assert.Equal(HotkeyMap.All.Count, hotkeys);
    }

    [Fact]
    public void Ctrl_comma_right_after_start_opens_settings_with_only_their_first_page_built()
    {
        var (visible, built, page, shown, rowsBefore, rowsOnSub) = _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow();
            window.AttachServices(Services("hotkey"));
            try
            {
                Assert.Null(View(window));
                var run = typeof(MainWindow).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string)])!;
                run.Invoke(window, [HotkeyMap.OpenSettings]);
                var view = View(window)!;
                var current = view.CurrentPage!;
                var general = view.Page<SettingsGeneralPage>();
                var before = general.HotkeyList.Children.Count;

                // Строки сочетаний — тринадцать полей записи — строит их подстраница, а не открытие.
                SettingsDrill.Open(general.GeneralHotkeysSub, general.HotkeysLinkRow);
                return (((FrameworkElement)window.FindName("SettingsOverlay")!).Visibility,
                    view.BuiltPages.Count(),
                    current.GetType(),
                    current.Visibility,
                    before,
                    general.HotkeyList.Children.Count);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(Visibility.Visible, visible);
        Assert.Equal(1, built);
        Assert.Equal(typeof(SettingsGeneralPage), page);
        Assert.Equal(Visibility.Visible, shown);
        Assert.Equal(0, rowsBefore);
        Assert.Equal(HotkeyMap.All.Count, rowsOnSub);
    }

    [Fact]
    public void A_page_link_opens_its_page_without_building_the_others()
    {
        var (built, page) = _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow();
            window.AttachServices(Services("link"));
            try
            {
                window.OpenSettings(window.SettingsUi.NavKey);
                var view = View(window)!;
                return (view.BuiltPages.Select(p => p.GetType()).ToList(), view.CurrentPage!.GetType());
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal([typeof(SettingsKeyPage)], built);
        Assert.Equal(typeof(SettingsKeyPage), page);
    }

    [Fact]
    public void Update_state_waits_for_the_about_page_and_shows_on_it_once_built()
    {
        var (beforeOpen, pill, sidebar) = _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow();
            window.AttachServices(Services("updates"));
            try
            {
                var release = UpdateTestKit.Release("99.0.0");
                window.Updates.Seed(_ => UpdateState.Initial with { Latest = release });
                var untouched = View(window) is null;
                window.OpenSettings(window.SettingsUi.NavAbout);
                var about = window.SettingsUi.Page<SettingsAboutPage>();
                return (untouched, about.UpdateStatePillText.Text, window.SettingsUi.SettingsVersionText.Text);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(beforeOpen, "состояние обновлений не должно строить страницу «О программе»");
        Assert.Equal(Loc.Get("S.Updates.Pill.Available"), pill);
        Assert.Contains("99.0.0", sidebar, StringComparison.Ordinal);
    }

    [Fact]
    public void Reopening_settings_shows_what_the_settings_file_says_now()
    {
        var (first, second) = _wpf.Ui.Invoke(() =>
        {
            var services = Services("reopen");
            var window = new MainWindow();
            window.AttachServices(services);
            try
            {
                window.OpenSettings();
                var toggle = window.SettingsUi.Page<SettingsGeneralPage>().AutoScrollToggle;
                var before = toggle.IsChecked;
                Call(window, "SettingsCloseButton_Click", window, new RoutedEventArgs());

                // Файл поменяли мимо страницы — как это делают импорт архива и смена профиля.
                services.Settings.AutoScroll = !services.Settings.AutoScroll;
                services.SettingsStore.Save(services.Settings);
                window.OpenSettings();
                return (before, toggle.IsChecked);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.NotNull(first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_page_built_while_settings_are_closed_is_filled_when_shown()
    {
        // Страница, построенная прогревом до того, как файл настроек поменяли, не должна
        // показать прежнее: открытие перечитывает файл, и показ освежает её.
        var (filled, shown) = _wpf.Ui.Invoke(() =>
        {
            var services = Services("stale");
            var window = new MainWindow();
            window.AttachServices(services);
            try
            {
                var view = SettingsTestKit.BuildAll(window);
                var prompts = view.Page<SettingsPromptsPage>();
                var before = prompts.MainPromptTextBox.Text;

                services.Settings.MainPrompt = "fresh main prompt";
                services.SettingsStore.Save(services.Settings);
                window.OpenSettings();
                window.OpenSettings(view.NavPrompts);
                return (before, prompts.MainPromptTextBox.Text);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.NotEqual("fresh main prompt", filled);
        Assert.Equal("fresh main prompt", shown);
    }

    [Fact]
    public void Disk_usage_is_counted_only_on_the_storage_sub_page()
    {
        // Разбивка лежит наверху «Хранения и очистки» и считается при заходе туда, а не на
        // каждое открытие «Данных»: обход читает все файлы чатов.
        var (onPage, onSub) = _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow();
            window.AttachServices(Services("usage"));
            try
            {
                window.OpenSettings(window.SettingsUi.NavData);
                var page = window.SettingsUi.Page<SettingsDataPage>();
                var idle = page.UsageSummaryText.Text;

                // Тем же путём, что щелчок по строке «›».
                page.CareLinkRow.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                return (idle, page.UsageSummaryText.Text);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(Loc.Get("S.Data.Usage.Desc"), onPage);
        Assert.NotEqual(Loc.Get("S.Data.Usage.Desc"), onSub);
    }

    private AppServices Services(string name)
    {
        var services = UiServices.Build(Path.Combine(_root, name), "k", new HttpClientHandler());

        // Проверка обновлений сходила бы в GitHub посреди теста.
        services.Settings.AutoCheckUpdates = false;
        return services;
    }

    private static MainWindow OffScreen(AppServices services)
    {
        var window = new MainWindow
        {
            Width = 1280,
            Height = 860,
            Left = -32000,
            Top = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.AttachServices(services);
        return window;
    }

    private static bool Done(MainWindow window) =>
        View(window) is { AllBuilt: true } &&
        ((Queue<Action>)typeof(MainWindow).GetField("_settingsDeferred", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Count == 0;

    private static SettingsView? View(MainWindow window) =>
        (SettingsView?)typeof(MainWindow).GetField("_settingsView", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    private static void Call(MainWindow window, string method, params object[] args) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(window, args);
}
