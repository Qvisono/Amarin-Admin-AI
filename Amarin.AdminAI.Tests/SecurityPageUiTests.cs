using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Страница «Безопасность»: карточка режима и выключатель инструмента пишут в настройки ровно то,
/// что на них видно, и страница, открытая заново, показывает то, что лежит в файле.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class SecurityPageUiTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-security-ui-" + Guid.NewGuid().ToString("N"));

    public SecurityPageUiTests(WpfFixture wpf) => _wpf = wpf;

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
    }

    private T WithPage<T>(Func<MainWindow, SettingsSecurityPage, AppServices, T> body) => _wpf.Ui.Invoke(() =>
    {
        var services = UiServices.Build(_root, "test", new HttpClientHandler());
        var window = new MainWindow();
        window.AttachServices(services);
        try
        {
            var page = (SettingsSecurityPage)window.FindName("SecurityPage")!;
            page.Attach(services);
            page.Load(services.Settings);
            return body(window, page, services);
        }
        finally
        {
            window.Close();
        }
    });

    private static CheckBox Toggle(SettingsSecurityPage page, string tool) =>
        ((Dictionary<string, CheckBox>)typeof(SettingsSecurityPage)
            .GetField("_toolToggles", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(page)!)[tool];

    [Fact]
    public void Choosing_a_card_saves_the_access_mode()
    {
        var saved = WithPage((window, page, services) =>
        {
            ((RadioButton)page.FindName("ModeReadOnly")!).IsChecked = true;
            return new AppSettingsStore(_root).Load().ApprovalMode;
        });

        Assert.Equal(ApprovalMode.ReadOnly, saved);
    }

    [Fact]
    public void Switching_a_tool_off_saves_it_and_switching_it_on_clears_the_list()
    {
        var (off, on) = WithPage((window, page, services) =>
        {
            Toggle(page, "run_powershell").IsChecked = false;
            var afterOff = new AppSettingsStore(_root).Load().DisabledTools?.ToList();

            Toggle(page, "run_powershell").IsChecked = true;
            var afterOn = new AppSettingsStore(_root).Load().DisabledTools;
            return (afterOff, afterOn);
        });

        Assert.Equal(["run_powershell"], off);
        Assert.Null(on);
    }

    [Fact]
    public void Loading_shows_what_the_settings_say_without_saving_anything()
    {
        var (checkedCard, powershellOn, writeOn, touched) = WithPage((window, page, services) =>
        {
            services.Settings.ApprovalMode = ApprovalMode.AskAll;
            services.Settings.DisabledTools = ["write_file"];
            var before = File.Exists(Path.Combine(_root, "settings.json"))
                ? File.GetLastWriteTimeUtc(Path.Combine(_root, "settings.json"))
                : DateTime.MinValue;

            page.Load(services.Settings);

            var after = File.Exists(Path.Combine(_root, "settings.json"))
                ? File.GetLastWriteTimeUtc(Path.Combine(_root, "settings.json"))
                : DateTime.MinValue;
            return (
                ((RadioButton)page.FindName("ModeAskAll")!).IsChecked == true,
                Toggle(page, "run_powershell").IsChecked == true,
                Toggle(page, "write_file").IsChecked == true,
                before != after);
        });

        Assert.True(checkedCard);
        Assert.True(powershellOn);
        Assert.False(writeOn);
        Assert.False(touched, "наполнение страницы записало настройки");
    }
}
