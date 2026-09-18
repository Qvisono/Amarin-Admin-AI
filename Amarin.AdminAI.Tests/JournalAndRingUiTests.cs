using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;
using Path = System.Windows.Shapes.Path;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The three new pieces of chrome, checked on a live window rather than by eye: the ring's arc,
/// the order of the model's remark against the calls it introduces, and the journal's grip on the
/// keyboard — without which the window's typing sink swallows Escape and the search box.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class JournalAndRingUiTests
{
    private readonly WpfFixture _wpf;

    public JournalAndRingUiTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() => Application.Current.Windows.OfType<MainWindow>().Single();

    private static T Field<T>(object target, string name) =>
        (T)target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target)!;

    private static T Named<T>(MainWindow window, string name) where T : class =>
        (T)window.FindName(name)!;

    private static ContextRing Ring(MainWindow window) =>
        (ContextRing)typeof(MainWindow)
            .GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    // ───────────────────────── the ring ─────────────────────────

    [Fact]
    public void An_empty_context_draws_no_arc_at_all()
    {
        var (visible, text) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Ring(window).Show(new ContextUsage(0, 128_000, true));
            window.UpdateLayout();
            return (Named<Path>(window, "ContextProgress").Visibility,
                    Named<TextBlock>(window, "ContextAmount").Text);
        });

        // A zero-length ArcSegment is undefined in WPF, so the sliver is hidden rather than drawn.
        Assert.Equal(Visibility.Collapsed, visible);
        Assert.Equal("-", text);
    }

    [Fact]
    public void A_half_full_context_draws_half_a_ring_and_says_so()
    {
        var (visible, text, geometry) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Ring(window).Show(new ContextUsage(64_000, 128_000, false));
            window.UpdateLayout();
            var progress = Named<Path>(window, "ContextProgress");
            return (progress.Visibility, Named<TextBlock>(window, "ContextAmount").Text, progress.Data);
        });

        Assert.Equal(Visibility.Visible, visible);
        Assert.Equal("64k / 128k", text);

        var figure = Assert.Single(Assert.IsType<PathGeometry>(geometry).Figures);
        var arc = Assert.IsType<ArcSegment>(Assert.Single(figure.Segments));

        // Starting at twelve o'clock and sweeping half the dial lands at the bottom of the ring.
        Assert.Equal(9, figure.StartPoint.X, 3);
        Assert.Equal(2.5, figure.StartPoint.Y, 3);
        Assert.Equal(9, arc.Point.X, 3);
        Assert.Equal(15.5, arc.Point.Y, 3);
    }

    [Fact]
    public void A_full_context_turns_the_ring_red_without_closing_it_into_a_point()
    {
        var (stroke, expected, geometry) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Ring(window).Show(new ContextUsage(128_000, 128_000, false));
            window.UpdateLayout();
            var progress = Named<Path>(window, "ContextProgress");
            return (progress.Stroke, window.TryFindResource("Status.Danger"), progress.Data);
        });

        Assert.Same(expected, stroke);

        // 360 degrees exactly would put both ends on the same point, which is the undefined case.
        var figure = Assert.Single(Assert.IsType<PathGeometry>(geometry).Figures);
        var arc = Assert.IsType<ArcSegment>(Assert.Single(figure.Segments));
        Assert.NotEqual(figure.StartPoint, arc.Point);
    }

    [Fact]
    public void The_ring_warns_in_amber_before_it_warns_in_red()
    {
        var (amber, accent, expectedWarning, expectedAccent) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var progress = Named<Path>(window, "ContextProgress");
            Ring(window).Show(new ContextUsage(85_000, 100_000, false));
            var warn = progress.Stroke;
            Ring(window).Show(new ContextUsage(10_000, 100_000, false));
            return (warn, progress.Stroke,
                    window.TryFindResource("Status.Warning"), window.TryFindResource("Accent.Fill"));
        });

        Assert.Same(expectedWarning, amber);
        Assert.Same(expectedAccent, accent);
    }

    // ───────────────────────── the remark ─────────────────────────

    [Fact]
    public void The_models_remark_is_drawn_above_the_calls_it_introduces()
    {
        var texts = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var message = new ChatDisplayMessage
            {
                Role = "assistant",
                Id = "m1",
                Text = "готово",
                ToolRounds =
                [
                    new ToolRound
                    {
                        ModelNote = "Хм, загляну в реестр.",
                        Calls =
                        [
                            new ToolCallRecord
                            {
                                Id = "c1",
                                Name = "registry",
                                ArgumentsJson = "{}",
                                Status = ToolCallStatus.Done,
                                Success = true,
                                ResultPreview = "ок"
                            }
                        ]
                    }
                ]
            };

            var view = ChatMessageViews.CreateAssistant(window, message);
            view.UpdateTools(message);
            window.UpdateLayout();

            var expander = (Expander)view.ToolsHost.Children[0];
            expander.IsExpanded = true;
            var body = (StackPanel)expander.Content;
            return body.Children.OfType<Grid>()
                .Select(row => string.Join(" ", row.Children.OfType<TextBlock>().Select(t => t.Text)))
                .ToList();
        });

        // The remark must come first: it is what the model said *before* acting, and below the
        // calls it would read as a comment on results it had not seen yet.
        Assert.Contains("Хм, загляну в реестр.", texts[0], StringComparison.Ordinal);
        Assert.Contains("registry", texts[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_round_with_nothing_to_say_draws_no_remark_row()
    {
        var rows = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var message = new ChatDisplayMessage
            {
                Role = "assistant",
                Id = "m2",
                ToolRounds =
                [
                    new ToolRound
                    {
                        Calls =
                        [
                            new ToolCallRecord
                            {
                                Id = "c1",
                                Name = "registry",
                                ArgumentsJson = "{}",
                                Status = ToolCallStatus.Done,
                                Success = true
                            }
                        ]
                    }
                ]
            };

            var view = ChatMessageViews.CreateAssistant(window, message);
            view.UpdateTools(message);
            var expander = (Expander)view.ToolsHost.Children[0];
            expander.IsExpanded = true;
            var body = (StackPanel)expander.Content;
            return body.Children.OfType<Grid>()
                .SelectMany(row => row.Children.OfType<TextBlock>())
                .Count(block => block.Text == "“");
        });

        Assert.Equal(0, rows);
    }

    // ───────────────────────── the journal ─────────────────────────

    [Fact]
    public void The_journal_opens_scoped_to_the_open_chat_and_closes_on_escape()
    {
        var (openedVisible, scopeIsChat, closedVisible) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Invoke(window, "OpenJournal");
            var overlay = Named<Grid>(window, "JournalOverlay");
            var opened = overlay.Visibility;
            var chatScope = Named<ToggleButton>(window, "JournalScopeChat").IsChecked == true;

            var args = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window) ?? PresentationSource.FromVisual(overlay),
                0,
                Key.Escape)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent
            };
            overlay.RaiseEvent(args);

            return (opened, chatScope, overlay.Visibility);
        });

        Assert.Equal(Visibility.Visible, openedVisible);
        Assert.True(scopeIsChat);
        Assert.Equal(Visibility.Collapsed, closedVisible);
    }

    [Fact]
    public void While_the_journal_is_up_the_window_stops_stealing_the_keyboard()
    {
        var (whileOpen, whileClosed, modifiers) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var guard = typeof(MainWindow)
                .GetMethod("ShouldKeepKeyboardFocus", BindingFlags.Instance | BindingFlags.NonPublic)!;

            // Focus is cleared rather than moved onto the overlay: this window is shared with
            // every other UI test, and giving it real keyboard focus activates it, which closes
            // popups the next test is about to open.
            var previous = Keyboard.FocusedElement;
            try
            {
                Invoke(window, "OpenJournal");
                Keyboard.ClearFocus();
                var open = (bool)guard.Invoke(window, null)!;

                Invoke(window, "CloseJournal");
                Keyboard.ClearFocus();
                var closed = (bool)guard.Invoke(window, null)!;
                return (open, closed, Keyboard.Modifiers);
            }
            finally
            {
                Keyboard.Focus(previous);
            }
        });

        // With nothing focused at all, only the journal's own branch can answer true - which is
        // the case that used to fall through to the composer and swallow Escape.
        Assert.True(whileOpen);

        // The guard answers true outright while Ctrl or Alt is down, and Keyboard.Modifiers reads
        // the real keyboard, not the test's. A stray modifier held anywhere on the machine made
        // this half fail roughly once in ten full runs and said nothing about the journal.
        if (modifiers == ModifierKeys.None)
        {
            Assert.False(whileClosed);
        }
    }

    [Fact]
    public void Closing_the_journal_hands_the_chat_back_its_clicks()
    {
        var hitTestable = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Invoke(window, "OpenJournal");
            var blocked = Named<Grid>(window, "Chat").IsHitTestVisible;
            Invoke(window, "CloseJournal");
            return (blocked, Named<Grid>(window, "Chat").IsHitTestVisible);
        });

        Assert.False(hitTestable.blocked);
        Assert.True(hitTestable.Item2);
    }

    [Fact]
    public void The_journal_icon_exists_in_both_themes()
    {
        var (dark, light) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var original = ThemeManager.Current.Theme;
            try
            {
                ThemeManager.Apply(AppTheme.Dark);
                var inDark = window.TryFindResource("Journal") as ImageSource;
                ThemeManager.Apply(AppTheme.Light);
                var inLight = window.TryFindResource("Journal") as ImageSource;
                return (inDark, inLight);
            }
            finally
            {
                ThemeManager.Apply(original);
            }
        });

        // A key present in one dictionary and missing from the other turns the button blank on
        // exactly one theme, which is the kind of thing nobody notices until they switch.
        Assert.NotNull(dark);
        Assert.NotNull(light);
    }

    private static void Invoke(MainWindow window, string method) =>
        typeof(MainWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);
}
