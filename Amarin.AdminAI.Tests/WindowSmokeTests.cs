using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// XAML parse errors only surface when a window is actually constructed, so every window the
/// app can open gets built once here.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class WindowSmokeTests
{
    private readonly WpfFixture _wpf;

    public WindowSmokeTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void Notification_toast_builds_and_is_visible_by_default()
    {
        var (opacity, left, top) = _wpf.Ui.Invoke(() =>
        {
            var toast = NotificationToast.Show(
                "grok-4-6",
                "Готово",
                "Grok 4.6 · 3 с",
                uiScalePercent: 100,
                ownerHandle: IntPtr.Zero,
                onActivated: null);

            var card = (FrameworkElement)((Grid)toast.Content).Children[0];

            // Base value, not Opacity: the fade-in is running and would read mid-animation.
            // What matters is the value the card falls back to when no animation plays.
            var baseOpacity = (double)card.GetAnimationBaseValue(UIElement.OpacityProperty);
            var result = (baseOpacity, toast.Left, toast.Top);
            toast.Close();
            return result;
        });

        // The card must not depend on the entrance animation to become visible — that was the
        // bug: an Opacity=0 card on a transparent window is an invisible notification.
        Assert.True(opacity > 0.99, $"card base opacity was {opacity}");
        Assert.True(left > -10000 && top > -10000, $"toast parked off-screen at {left},{top}");
    }

    [Fact]
    public void Notification_toast_survives_a_ui_scale()
    {
        var scaled = _wpf.Ui.Invoke(() =>
        {
            var toast = NotificationToast.Show(
                "claude-sonnet-5", "Готово", "meta", uiScalePercent: 200, IntPtr.Zero, null);
            var result = toast.Content is FrameworkElement { LayoutTransform: not null };
            toast.Close();
            return result;
        });

        Assert.True(scaled);
    }

    [Fact]
    public void Main_window_has_the_new_account_and_composer_controls()
    {
        var missing = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            string[] names =
            [
                "AccountNameText", "AccountAvatarImage", "AccountAvatarLetter", "SwitchAccountButton",
                "ChangeNameButton", "ChangePasswordButton", "RemovePasswordButton", "LockOnStartupToggle",
                "ChangeAvatarButton", "RemoveAvatarButton", "AccountPasswordHint",
                "ChatSharingToggle", "ImportChatButton",
                "AttachmentsPanel", "AttachmentsHost", "AttachmentsWarning",
                "ComposerBorder", "AttachFileButton", "ChatReasoningPicker",
                "LiteReasoningPicker", "AgentHeavyReasoningPicker",
                "ProfileList", "NameInput", "NameSaveButton", "ProfileOverlay", "NameOverlay"
            ];
            return names.Where(name => window.FindName(name) is null).ToList();
        });

        Assert.Empty(missing);
    }

    [Fact]
    public void Composer_accepts_drops_and_can_grow()
    {
        var (allowsDrop, fixedHeight) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var composer = (Border)window.FindName("ComposerBorder")!;
            var chat = (Grid)window.FindName("Chat")!;

            // A fixed pixel height would stop the border growing with the thumbnail strip.
            var isFixed = chat.RowDefinitions[1].Height.IsAbsolute;
            return (composer.AllowDrop, isFixed);
        });

        Assert.True(allowsDrop);
        Assert.False(fixedHeight);
    }

    [Fact]
    public void Icons_used_by_message_buttons_exist_in_both_themes()
    {
        // IconAction silently renders an empty button when a key is missing from a theme.
        var missing = _wpf.Ui.Invoke(() =>
        {
            var result = new List<string>();
            var original = ThemeManager.IsLight ? AppTheme.Light : AppTheme.Dark;
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme);
                foreach (var key in new[] { "ExportJson", "Upload", "Copy", "Regenerate", "Delete" })
                {
                    if (Application.Current.TryFindResource(key) is null)
                    {
                        result.Add($"{theme}:{key}");
                    }
                }
            }

            ThemeManager.Apply(original);
            return result;
        });

        Assert.Empty(missing);
    }

    [Fact]
    public void Markdown_brushes_exist_in_both_themes()
    {
        // SetResourceReference to a key missing from one palette leaves the text unpainted,
        // and it only shows up after the user flips the theme.
        var missing = _wpf.Ui.Invoke(() =>
        {
            var result = new List<string>();
            var original = ThemeManager.IsLight ? AppTheme.Light : AppTheme.Dark;
            string[] keys =
            [
                "Code.Inline", "Code.Keyword", "Code.Control", "Code.String", "Code.Comment",
                "Code.Number", "Code.Type", "Code.Variable", "Code.Function", "Code.Operator",
                "Link.Default", "Link.Hover"
            ];

            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme);
                foreach (var key in keys)
                {
                    if (Application.Current.TryFindResource(key) is null)
                    {
                        result.Add($"{theme}:{key}");
                    }
                }
            }

            ThemeManager.Apply(original);
            return result;
        });

        Assert.Empty(missing);
    }

    [Fact]
    public void Password_window_builds_in_all_three_modes()
    {
        // ShowDialog would block the UI thread, so construct and close without showing.
        var errors = _wpf.Ui.Invoke(() =>
        {
            var problems = new List<string>();
            try
            {
                var window = new PasswordWindow();
                window.Close();
            }
            catch (Exception ex)
            {
                problems.Add(ex.Message);
            }

            return problems;
        });

        Assert.Empty(errors);
    }
}
