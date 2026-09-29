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
        return (((ToggleButton)window.FindName("ConfirmationAllowToggle")).Visibility,
                ((Button)window.FindName("ConfirmationAllowChatButton")).Visibility);
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
    public void The_access_mode_list_offers_all_four_modes()
    {
        var tags = _wpf.Ui.Invoke(() =>
            ((ComboBox)Window().FindName("ApprovalModeCombo")).Items
                .OfType<ComboBoxItem>()
                .Select(item => item.Tag as string)
                .ToList());

        Assert.Equal(Enum.GetNames<ApprovalMode>().Order(), tags.Order());
    }
}
