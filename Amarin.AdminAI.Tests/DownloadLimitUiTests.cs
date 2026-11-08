using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Настройка размера сохраняет прежнее число и откатывает неверный ввод без изменения профиля.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class DownloadLimitUiTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-download-ui-" + Guid.NewGuid().ToString("N"));

    public DownloadLimitUiTests(WpfFixture wpf) => _wpf = wpf;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void An_old_profile_shows_the_configured_limit_without_writing_an_override()
    {
        WithPage((page, services) =>
        {
            Assert.True(Toggle(page).IsChecked);
            Assert.Equal("911", Box(page).Text);
            CommitFocus(Box(page));
            Assert.Null(services.Settings.DownloadMaxSizeMb);
            Assert.Null(services.SettingsStore.Load().DownloadMaxSizeMb);
        });
    }

    [Fact]
    public void Turning_the_limit_off_and_on_keeps_the_number_and_survives_a_reload()
    {
        WithPage((page, services) =>
        {
            var box = Box(page);
            box.Text = "2048";
            CommitFocus(box);
            var toggle = Toggle(page);
            toggle.IsChecked = false;

            Assert.False(services.Settings.DownloadSizeLimitEnabled);
            Assert.Equal(2048, services.Settings.DownloadMaxSizeMb);
            Assert.False(box.IsEnabled);

            page.Load(services.SettingsStore.Load());
            Assert.Equal("2048", box.Text);
            Assert.False(toggle.IsChecked);

            toggle.IsChecked = true;
            Assert.True(box.IsEnabled);
            Assert.Equal(2048, services.SettingsStore.Load().DownloadMaxSizeMb);
            Assert.True(services.SettingsStore.Load().DownloadSizeLimitEnabled);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("abc")]
    public void Invalid_input_restores_the_saved_number(string text)
    {
        WithPage((page, services) =>
        {
            Box(page).Text = "2048";
            CommitFocus(Box(page));
            Box(page).Text = text;
            CommitFocus(Box(page));

            Assert.Equal("2048", Box(page).Text);
            Assert.Equal(2048, services.SettingsStore.Load().DownloadMaxSizeMb);
        });
    }

    [Fact]
    public void Enter_commits_the_number_and_does_not_bubble()
    {
        WithPage((page, services) =>
        {
            var box = Box(page);
            box.Text = " 4096 ";
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, Key.Enter)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            };

            box.RaiseEvent(args);

            Assert.True(args.Handled);
            Assert.Equal(4096, services.SettingsStore.Load().DownloadMaxSizeMb);
            Assert.Equal("4096", box.Text);
        });
    }

    [Fact]
    public void Loading_another_profile_replaces_the_number_and_the_toggle()
    {
        WithPage((page, services) =>
        {
            var otherRoot = Path.Combine(_root, "other");
            var other = AppSettings.CreateDefault();
            other.DownloadMaxSizeMb = 3072;
            other.DownloadSizeLimitEnabled = false;
            new AppSettingsStore(otherRoot).Save(other);

            services.UseProfile(otherRoot);
            page.Load(services.Settings);

            Assert.Equal("3072", Box(page).Text);
            Assert.False(Toggle(page).IsChecked);
            Assert.False(Box(page).IsEnabled);
        });
    }

    [Fact]
    public void The_largest_supported_size_is_kept_as_a_positive_integer()
    {
        WithPage((page, services) =>
        {
            Box(page).Text = int.MaxValue.ToString(CultureInfo.InvariantCulture);
            CommitFocus(Box(page));

            Assert.Equal(int.MaxValue, services.SettingsStore.Load().DownloadMaxSizeMb);
        });
    }

    private void WithPage(Action<SettingsSecurityPage, AppServices> body) => _wpf.Ui.Invoke(() =>
    {
        using var http = new HttpClient(new HttpClientHandler());
        using var services = UiServices.Build(_root, new AgentOptions
        {
            Download = new DownloadOptions { MaxSizeBytes = 911L * 1024 * 1024 }
        }, http);
        var page = new SettingsSecurityPage();
        page.Attach(services);
        page.Load(services.Settings);
        body(page, services);
        return true;
    });

    private static CheckBox Toggle(SettingsSecurityPage page) => (CheckBox)page.FindName("DownloadSizeLimitToggle");

    private static TextBox Box(SettingsSecurityPage page) => (TextBox)page.FindName("DownloadMaxSizeBox");

    private static void CommitFocus(TextBox box) => box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, box));

    private sealed class FakeSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }

        public override bool IsDisposed => false;

        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
