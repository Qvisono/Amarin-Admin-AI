using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Окно «Что нового» (1.37.0): колонка разделов из заметок, подсветка раздела под мышью,
/// переход по щелчку, язык заметок и закрытие.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class WhatsNewSheetTests : IDisposable
{
    private static readonly string Notes =
        "# 1.37.0: test\n\nIntro line.\n" +
        string.Concat(Enumerable.Range(0, 6).Select(i =>
            $"\n## Section {i}\n\n" + string.Concat(Enumerable.Range(0, 8).Select(k => $"- Point {k} of section {i} that is long enough to wrap.\n"))));

    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-whatsnew-" + Guid.NewGuid().ToString("N"));

    public WhatsNewSheetTests(WpfFixture wpf) => _wpf = wpf;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void After_closing_nothing_of_the_sheet_is_left_in_the_window() => _wpf.Ui.Invoke(() =>
    {
        // Лист показывается раз на версию: после закрытия окно работает, как до него.
        var services = UiServices.Build(_root, "k", new HttpClientHandler());
        var window = new MainWindow();
        window.AttachServices(services);
        var motion = UiMotion.Enabled;
        UiMotion.Enabled = false;
        try
        {
            window.ShowWhatsNew(ReleaseVersion.Parse("1.37.0")!.Value, Notes);
            var sheet = window.WhatsNewHost.Children.OfType<WhatsNewSheet>().Single();

            sheet.Close();

            Assert.Empty(window.WhatsNewHost.Children);
            Assert.Equal(Visibility.Collapsed, window.WhatsNewHost.Visibility);
            Assert.Null(typeof(MainWindow).GetField("_whatsNew", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            Assert.False(sheet.Curtain.Running);
        }
        finally
        {
            UiMotion.Enabled = motion;
            window.Close();
        }

        return true;
    });

    [Fact]
    public void Every_section_gets_an_item_in_the_column() => With(null, sheet =>
    {
        var titles = sheet.TocPanel.Children.OfType<RadioButton>().Select(item => (string)item.Content).ToList();

        Assert.Equal(Enumerable.Range(0, 6).Select(i => $"Section {i}"), titles);
        Assert.Equal(Visibility.Visible, sheet.Lead.Visibility);
        Assert.Equal(0, sheet.HighlightedSection);
    });

    [Fact]
    public void The_section_under_the_mouse_is_highlighted_in_the_column() => With(null, sheet =>
    {
        var third = sheet.NotesPanel.Children[3];
        sheet.HighlightAt(third.TranslatePoint(new Point(0, 4), sheet.NotesPanel).Y);

        Assert.Equal(3, sheet.HighlightedSection);
        Assert.True(sheet.TocPanel.Children.OfType<RadioButton>().ElementAt(3).IsChecked);
        Assert.False(sheet.TocPanel.Children.OfType<RadioButton>().ElementAt(0).IsChecked);
    });

    [Fact]
    public void A_column_item_scrolls_the_notes_to_its_section() => With(null, sheet =>
    {
        sheet.GoToSection(4);
        sheet.UpdateLayout();

        Assert.True(sheet.NotesScroll.VerticalOffset > 0);
        Assert.Equal(4, sheet.HighlightedSection);
    });

    [Fact]
    public void An_english_interface_has_no_language_switch() => With(null, sheet =>
        Assert.Equal(Visibility.Collapsed, sheet.LanguageSwitch.Visibility));

    [Fact]
    public void Without_a_key_the_translation_button_is_off_and_says_why() =>
        With(new WhatsNewTranslation("Русский", true, null), sheet =>
        {
            Assert.Equal(Visibility.Visible, sheet.LanguageSwitch.Visibility);
            Assert.False(sheet.NativeButton.IsEnabled);
            Assert.True(sheet.EnglishButton.IsChecked);
            Assert.Equal(Loc.Get("S.Updates.WhatsNewNeedsKey"), sheet.LanguageStatus.Text);
        });

    [Fact]
    public void The_language_button_translates_once_and_english_brings_the_original_back()
    {
        var calls = 0;
        var translation = new WhatsNewTranslation("Русский", true, (outline, _) =>
        {
            calls++;
            return Task.FromResult<ReleaseNotesOutline?>(ReleaseNotesTranslator.FromStrings(
                outline,
                new Dictionary<string, string> { ["s0.title"] = "Раздел 0" }));
        });

        With(translation, sheet =>
        {
            sheet.NativeButton.IsChecked = true;
            Assert.Equal("Раздел 0", FirstItem(sheet));

            sheet.EnglishButton.IsChecked = true;
            Assert.Equal("Section 0", FirstItem(sheet));

            sheet.NativeButton.IsChecked = true;
            Assert.Equal("Раздел 0", FirstItem(sheet));
            Assert.Equal(1, calls);
        });
    }

    [Fact]
    public void A_failed_translation_says_so_and_stays_in_english() =>
        With(new WhatsNewTranslation("Русский", true, (_, _) => Task.FromResult<ReleaseNotesOutline?>(null)), sheet =>
        {
            sheet.NativeButton.IsChecked = true;

            Assert.True(sheet.EnglishButton.IsChecked);
            Assert.Equal("Section 0", FirstItem(sheet));
            Assert.Equal(Loc.Get("S.Updates.WhatsNewTranslateFailed"), sheet.LanguageStatus.Text);
        });

    [Fact]
    public void Escape_starts_the_leave_and_the_scene_stops_drawing_when_it_ends() => With(null, sheet =>
    {
        Assert.True(sheet.Curtain.Running);
        var closed = false;
        sheet.Closed += () => closed = true;

        Escape(sheet);

        // Уход идёт: окно уже не ловит мышь, но лист ещё виден и о закрытии не сказал.
        Assert.True(sheet.Leaving);
        Assert.False(sheet.IsHitTestVisible);
        Assert.False(closed);

        // Второй Esc уход не перезапускает.
        var leaveAt = sheet.Curtain.LeaveAt;
        Escape(sheet);
        Assert.Equal(leaveAt, sheet.Curtain.LeaveAt);

        sheet.Curtain.Freeze(leaveAt!.Value + WhatsNewCurtain.LeaveTime / 2);
        Assert.InRange(sheet.Card.Opacity, 0, .01);
        Assert.False(closed);

        sheet.Curtain.Freeze(leaveAt.Value + WhatsNewCurtain.LeaveTime);
        Assert.True(closed);
        Assert.False(sheet.Curtain.Running);
        Assert.Equal(Visibility.Collapsed, sheet.Visibility);
        Assert.True(sheet.IsHitTestVisible);
    }, animations: true);

    [Fact]
    public void Without_animations_escape_closes_at_once() => With(null, sheet =>
    {
        var closed = false;
        sheet.Closed += () => closed = true;

        Escape(sheet);

        Assert.True(closed);
        Assert.Equal(Visibility.Collapsed, sheet.Visibility);
    });

    private static void Escape(WhatsNewSheet sheet) =>
        sheet.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(sheet)!, 0, Key.Escape)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent
        });

    [Fact]
    public void Without_animations_the_card_is_shown_at_once() => With(null, sheet =>
    {
        Assert.False(sheet.Curtain.Running);
        Assert.Equal(1, sheet.Card.Opacity, 3);
    });

    private static string FirstItem(WhatsNewSheet sheet) =>
        (string)sheet.TocPanel.Children.OfType<RadioButton>().First().Content;

    private void With(WhatsNewTranslation? translation, Action<WhatsNewSheet> body, bool animations = false) =>
        _wpf.Ui.Invoke(() =>
        {
            var motion = UiMotion.Enabled;
            UiMotion.Enabled = animations;
            var sheet = new WhatsNewSheet();
            var host = new Window
            {
                Width = 1382,
                Height = 697,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = sheet
            };

            try
            {
                host.Show();
                sheet.Show(ReleaseVersion.Parse("1.37.0")!.Value, Notes, translation);
                host.UpdateLayout();
                body(sheet);
            }
            finally
            {
                sheet.Close();
                host.Close();
                UiMotion.Enabled = motion;
            }

            return true;
        });
}
