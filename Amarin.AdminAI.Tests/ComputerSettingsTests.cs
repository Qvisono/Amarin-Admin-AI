using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Галочка управления компьютером в «Безопасности» (1.36.0): включается сразу, без вопроса, и
/// одно уведомление говорит, куда уходят снимки экрана; выключение молчит.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ComputerSettingsTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-computer-settings-" + Guid.NewGuid().ToString("N"));

    public ComputerSettingsTests(WpfFixture wpf)
    {
        _wpf = wpf;
        Directory.CreateDirectory(_root);
    }

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
    public void The_switch_turns_on_at_once_with_one_notice_and_off_silently()
    {
        MainWindow? window = null;
        try
        {
            var (on, noticeShown, title, off, noticeAgain) = _wpf.Ui.Invoke(() =>
            {
                window = new MainWindow();
                var services = UiServices.Build(_root, "k", new HttpClientHandler());
                window.AttachServices(services);
                var security = (SettingsSecurityPage)window.FindSetting("SecurityPage");

                // Так страницу наполняет её показ (NavSecurity_Checked).
                security.Attach(services);
                security.Load(services.Settings);
                var toggle = (CheckBox)security.FindName("ComputerControlToggle")!;
                var overlay = (FrameworkElement)window.FindName("NoticeOverlay")!;

                toggle.IsChecked = true;
                var turnedOn = services.Settings.ComputerControl;
                var shown = overlay.Visibility;
                var heading = ((TextBlock)window.FindName("NoticeTitle")!).Text;

                overlay.Visibility = Visibility.Collapsed;
                toggle.IsChecked = false;
                return (turnedOn, shown, heading, services.Settings.ComputerControl, overlay.Visibility);
            });

            Assert.True(on);
            Assert.Equal(Visibility.Visible, noticeShown);
            Assert.Equal(Loc.Get("S.Security.ComputerNoticeTitle"), title);
            Assert.False(off);
            Assert.Equal(Visibility.Collapsed, noticeAgain);
        }
        finally
        {
            _wpf.Ui.Invoke(() =>
            {
                window?.Close();
                return 0;
            });
        }
    }
}
