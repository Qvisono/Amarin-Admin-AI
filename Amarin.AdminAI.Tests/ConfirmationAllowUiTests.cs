using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// «Разрешить…» в окне подтверждения: есть там, где разрешение впрок имеет смысл, и не
/// предлагает PowerShell на весь чат.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ConfirmationAllowUiTests
{
    private readonly WpfFixture _wpf;

    public ConfirmationAllowUiTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() => Application.Current.Windows.OfType<MainWindow>().Single();

    private static ConfirmationRequest Request(string tool, string? sessionId, bool alwaysAsk = false) => new()
    {
        AgentLabel = "Чат",
        Info = new DangerousActionInfo(tool, "summary", "details", DangerousRiskLevel.Medium, AlwaysAsk: alwaysAsk),
        Completion = new TaskCompletionSource<ConfirmationAnswer>(),
        SessionId = sessionId
    };

    private static (Visibility Toggle, Visibility Chat) Show(MainWindow window, ConfirmationRequest request)
    {
        typeof(MainWindow)
            .GetMethod("ShowConfirmationAllow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [request]);
        return (((ToggleButton)window.FindSetting("ConfirmationAllowToggle")).Visibility,
                ((Button)window.FindSetting("ConfirmationAllowChatButton")).Visibility);
    }

    [Fact]
    public void The_allow_menu_appears_only_where_it_means_something()
    {
        var (file, powershell, guard, noChat) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            return (
                Show(window, Request("write_file", "chat-1")),
                Show(window, Request("run_powershell", "chat-1")),
                Show(window, Request("write_file", "chat-1", alwaysAsk: true)),
                Show(window, Request("write_file", null)));
        });

        Assert.Equal((Visibility.Visible, Visibility.Visible), file);
        Assert.Equal((Visibility.Visible, Visibility.Collapsed), powershell);
        Assert.Equal(Visibility.Collapsed, guard.Toggle);
        Assert.Equal(Visibility.Collapsed, noChat.Toggle);
    }

    [Fact]
    public void The_security_page_offers_all_four_access_modes()
    {
        var tags = _wpf.Ui.Invoke(() =>
            ((SettingsSecurityPage)Window().FindSetting("SecurityPage")).ModeTags.ToList());

        Assert.Equal(Enum.GetNames<ApprovalMode>().Order(), tags.Order());
    }
}
