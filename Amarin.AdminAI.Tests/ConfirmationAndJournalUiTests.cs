using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The two screens a person actually reads before allowing something: the confirmation dialog and
/// the journal's details view. Checked on a live window, because both are laid out inside a
/// Viewbox with a fixed width — the place where this kind of thing breaks silently.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ConfirmationAndJournalUiTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly WpfFixture _wpf;

    public ConfirmationAndJournalUiTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() => Application.Current.Windows.OfType<MainWindow>().Single();

    private static T Named<T>(MainWindow window, string name) where T : class => (T)window.FindName(name)!;

    private static void Fill(MainWindow window, DangerousActionInfo info) =>
        typeof(MainWindow).GetMethod("FillConfirmationBody", Hidden)!.Invoke(window, [info]);

    private static DangerousActionInfo Info(string code = "", string language = "", string explanation = "") =>
        new("filesystem", "Запись в файл: C:/a.bat", "Инструмент: filesystem\npath: C:/a.bat",
            DangerousRiskLevel.Medium, explanation, code, language);

    // ───────────────────────── the dialog ─────────────────────────

    [Fact]
    public void The_whole_script_is_on_screen_not_a_truncated_line_of_it()
    {
        var script = "@echo off\r\n" + string.Join("\r\n", Enumerable.Range(0, 40).Select(i => $"echo line {i}"));

        var shown = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Fill(window, Info(script, "bat"));
            window.UpdateLayout();

            var host = Named<ContentControl>(window, "ConfirmationCodeHost");
            var expander = (Expander)host.Content;
            return (host.Visibility, expander.IsExpanded, Text(expander));
        });

        Assert.Equal(Visibility.Visible, shown.Visibility);

        // Open on arrival: the point of the block is that nobody approves a script unseen.
        Assert.True(shown.IsExpanded);
        Assert.Contains("echo line 39", shown.Item3, StringComparison.Ordinal);
    }

    [Fact]
    public void A_call_with_nothing_to_run_shows_no_empty_block()
    {
        var visibility = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Fill(window, Info());
            window.UpdateLayout();
            return Named<ContentControl>(window, "ConfirmationCodeHost").Visibility;
        });

        Assert.Equal(Visibility.Collapsed, visibility);
    }

    [Fact]
    public void A_long_script_does_not_stretch_the_card_out_of_shape()
    {
        // The card lives in a Viewbox with Stretch="Uniform": anything that makes it taller or
        // wider than its limits shrinks the entire dialog, buttons included, until it is unreadable.
        var script = string.Join("\r\n", Enumerable.Range(0, 200).Select(i => $"echo {new string('x', 300)} {i}"));

        var size = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Fill(window, Info(script, "bat"));
            var card = Named<Border>(window, "ConfirmationCard");
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return card.DesiredSize;
        });

        Assert.True(size.Height <= 560, $"карточка выросла до {size.Height}");
        Assert.True(size.Width <= 460, $"карточка расширилась до {size.Width}");
    }

    [Fact]
    public void The_explanation_line_disappears_instead_of_holding_a_technical_dump()
    {
        var (withWords, without) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();

            Fill(window, Info(explanation: "Создаю bat для проверки импортов."));
            var spoken = Named<TextBlock>(window, "ConfirmationExplanationText").Visibility;

            // Before, an empty explanation put the multi-line Details block in this line.
            Fill(window, Info());
            var silent = Named<TextBlock>(window, "ConfirmationExplanationText");
            return (spoken, (silent.Visibility, silent.Text));
        });

        Assert.Equal(Visibility.Visible, withWords);
        Assert.Equal(Visibility.Collapsed, without.Visibility);
        Assert.Equal("", without.Text);
    }

    [Fact]
    public void The_technical_breakdown_moves_into_its_own_folded_block()
    {
        var (visible, expanded) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Fill(window, Info());
            window.UpdateLayout();
            var host = Named<ContentControl>(window, "ConfirmationDetailsHost");
            return (host.Visibility, ((Expander)host.Content).IsExpanded);
        });

        Assert.Equal(Visibility.Visible, visible);
        Assert.False(expanded);
    }

    // ───────────────────────── the journal ─────────────────────────

    [Fact]
    public void Clicking_a_row_opens_it_and_back_returns_to_the_list()
    {
        var state = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            typeof(MainWindow).GetMethod("OpenJournal", Hidden)!.Invoke(window, null);
            typeof(MainWindow).GetMethod("ShowJournalDetails", Hidden)!.Invoke(window, [Row()]);
            window.UpdateLayout();

            var opened = (
                Named<ScrollViewer>(window, "JournalDetails").Visibility,
                Named<ItemsControl>(window, "JournalList").Visibility,
                Named<Button>(window, "JournalBackButton").Visibility,
                Named<Grid>(window, "JournalTabsRow").Visibility,
                Named<TextBlock>(window, "JournalDetailTitle").Text);

            typeof(MainWindow).GetMethod("ShowJournalList", Hidden)!.Invoke(window, null);
            var closed = Named<ScrollViewer>(window, "JournalDetails").Visibility;

            typeof(MainWindow).GetMethod("CloseJournal", Hidden)!.Invoke(window, null);
            return (opened, closed);
        });

        Assert.Equal(Visibility.Visible, state.opened.Item1);
        Assert.Equal(Visibility.Collapsed, state.opened.Item2);
        Assert.Equal(Visibility.Visible, state.opened.Item3);

        // Tabs and scope belong to the list; leaving them up on the details screen would offer
        // filters that change nothing visible.
        Assert.Equal(Visibility.Collapsed, state.opened.Item4);
        Assert.Contains("registry", state.opened.Item5, StringComparison.Ordinal);
        Assert.Equal(Visibility.Collapsed, state.closed);
    }

    [Fact]
    public void Escape_steps_back_to_the_list_before_it_closes_the_journal()
    {
        var (afterFirst, afterSecond) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var overlay = Named<Grid>(window, "JournalOverlay");
            typeof(MainWindow).GetMethod("OpenJournal", Hidden)!.Invoke(window, null);
            typeof(MainWindow).GetMethod("ShowJournalDetails", Hidden)!.Invoke(window, [Row()]);

            Escape(window, overlay);
            var back = (Named<ScrollViewer>(window, "JournalDetails").Visibility, overlay.Visibility);

            Escape(window, overlay);
            return (back, overlay.Visibility);
        });

        Assert.Equal(Visibility.Collapsed, afterFirst.Item1);
        Assert.Equal(Visibility.Visible, afterFirst.Item2);
        Assert.Equal(Visibility.Collapsed, afterSecond);
    }

    [Fact]
    public void A_restore_point_is_not_offered_an_explanation_it_cannot_get()
    {
        var visibility = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            typeof(MainWindow).GetMethod("OpenJournal", Hidden)!.Invoke(window, null);
            typeof(MainWindow).GetMethod("ShowJournalDetails", Hidden)!.Invoke(window, [SnapshotRow()]);
            var result = Named<Button>(window, "JournalAskButton").Visibility;
            typeof(MainWindow).GetMethod("CloseJournal", Hidden)!.Invoke(window, null);
            return result;
        });

        Assert.Equal(Visibility.Collapsed, visibility);
    }

    // ───────────────────────── helpers ─────────────────────────

    private static object Row() => JournalView.ToRow(
        new JournalEntry(
            "chat-1", "Разговор", new DateTime(2026, 3, 1, 12, 0, 0), TimeSpan.FromMilliseconds(340),
            "registry", """{"action":"write","path":"HKLM\\SOFTWARE\\X"}""", "готово", "готово, ключ записан",
            true, ToolCallStatus.Done, null),
        showChat: true);

    private static object SnapshotRow() => JournalView.ToRow(
        new SnapshotEntry("20260301_120000", new DateTime(2026, 3, 1, 12, 0, 0), "перед чисткой", "PC", @"C:\snap"));

    private static void Escape(MainWindow window, Grid overlay)
    {
        var args = new KeyEventArgs(
            Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(window) ?? PresentationSource.FromVisual(overlay),
            0,
            Key.Escape)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent
        };
        overlay.RaiseEvent(args);
    }

    /// <summary>Flattens whatever the code block built into plain text.</summary>
    private static string Text(DependencyObject root)
    {
        if (root is RichTextBox rich)
        {
            return new System.Windows.Documents.TextRange(
                rich.Document.ContentStart, rich.Document.ContentEnd).Text;
        }

        var text = "";
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            text += Text(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
        }

        return text;
    }
}
