using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Окно «Что нового»: колонка разделов из заметок, подсветка раздела под мышью, переход по щелчку,
/// язык заметок и закрытие; с 1.37.1 — сцена «Капля», палитра по теме Windows, чёткость карточки и
/// перевод, который нельзя оплатить дважды.
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
            Assert.False(sheet.Scene.Running);
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
        Assert.True(sheet.Scene.Running);
        var closed = false;
        sheet.Closed += () => closed = true;

        Escape(sheet);

        // Уход идёт: окно уже не ловит мышь, но лист ещё виден и о закрытии не сказал.
        Assert.True(sheet.Leaving);
        Assert.False(sheet.IsHitTestVisible);
        Assert.False(closed);

        // Второй Esc уход не перезапускает.
        var leaveAt = sheet.Scene.LeaveAt;
        Escape(sheet);
        Assert.Equal(leaveAt, sheet.Scene.LeaveAt);

        sheet.Scene.Freeze(leaveAt!.Value + WhatsNewDrop.LeaveTime / 2);
        Assert.InRange(sheet.Card.Opacity, 0, .01);
        Assert.False(closed);

        sheet.Scene.Freeze(leaveAt.Value + WhatsNewDrop.LeaveTime);
        Assert.True(closed);
        Assert.False(sheet.Scene.Running);
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
        Assert.False(sheet.Scene.Running);
        Assert.Equal(1, sheet.Card.Opacity, 3);
    });

    /// <summary>
    /// 1.37.1: окно было мыльным — карточка шла растровым кэшем поверх стекла. Теперь она рисуется
    /// вживую, без кэша и эффектов, и в покое её масштаб и сдвиг ровно единичные.
    /// </summary>
    [Fact]
    public void The_card_is_drawn_live_and_rests_exactly_on_the_pixel_grid() => With(null, sheet =>
    {
        sheet.Scene.Freeze(5);

        Assert.Null(sheet.Card.CacheMode);
        Assert.Null(sheet.Card.Effect);
        Assert.True(sheet.Card.UseLayoutRounding);
        Assert.Equal(1, sheet.Card.Opacity);
        Assert.Equal(1, sheet.CardScale.ScaleX);
        Assert.Equal(1, sheet.CardScale.ScaleY);
        Assert.Equal(0, sheet.CardShift.Y);
    }, animations: true);

    /// <summary>1.37.1: окно должно появляться сразу — карточка видна меньше чем через полсекунды.</summary>
    [Fact]
    public void The_card_appears_within_half_a_second() => With(null, sheet =>
    {
        sheet.Scene.Freeze(0);
        Assert.Equal(0, sheet.Card.Opacity, 3);

        sheet.Scene.Freeze(.3);
        Assert.InRange(sheet.Card.Opacity, .5, 1);

        sheet.Scene.Freeze(.52);
        Assert.Equal(1, sheet.Card.Opacity);
    }, animations: true);

    [Theory]
    [InlineData(false, "#FFFFFFFF", "#FF141416")]
    [InlineData(true, "#FF111114", "#FFF2F2F5")]
    public void The_sheet_takes_the_light_or_dark_palette_of_the_concept(bool dark, string back, string feather) =>
        With(null, sheet =>
        {
            Assert.Equal(dark, sheet.Dark);
            Assert.Equal(back, ((SolidColorBrush)sheet.Scene.Fill!).Color.ToString());
            Assert.Equal(feather, ((SolidColorBrush)sheet.Scene.Feather!).Color.ToString());
        }, dark: dark);

    [Fact]
    public void Both_palettes_have_the_same_keys() => _wpf.Ui.Invoke(() =>
    {
        var light = Palette("Light");
        var dark = Palette("Dark");

        Assert.Equal(light.Keys.Cast<string>().Order(), dark.Keys.Cast<string>().Order());
        Assert.All(light.Keys.Cast<string>(), key => Assert.StartsWith("WhatsNew.", key, StringComparison.Ordinal));
        return true;
    });

    /// <summary>
    /// 1.37.1: пока перевод идёт, язык не переключить — ни мышью, ни клавиатурой, — и второй запрос
    /// не уходит. До этого пробел на кнопке языка отменял уже оплаченный запрос и отправлял новый.
    /// </summary>
    [Fact]
    public void A_translation_in_progress_is_paid_once_whatever_is_pressed()
    {
        var calls = 0;
        var answer = new TaskCompletionSource<ReleaseNotesOutline?>();
        var translation = new WhatsNewTranslation("Русский", true, (outline, _) =>
        {
            calls++;
            return answer.Task.ContinueWith(
                _ => (ReleaseNotesOutline?)ReleaseNotesTranslator.FromStrings(
                    outline, new Dictionary<string, string> { ["s0.title"] = "Раздел 0" }),
                TaskScheduler.Default);
        });

        With(translation, sheet =>
        {
            sheet.NativeButton.IsChecked = true;
            for (var i = 0; i < 5; i++)
            {
                // Клавиатура мимо заблокированной мыши: английский возвращается к «Русскому».
                sheet.EnglishButton.IsChecked = true;
                Assert.True(sheet.NativeButton.IsChecked);
                sheet.NativeButton.IsChecked = true;
            }

            Assert.Equal(1, calls);
            Assert.Equal("Section 0", FirstItem(sheet));
            Assert.False(sheet.LanguageSwitch.IsHitTestVisible);

            answer.SetResult(null);
            PumpUntil(() => FirstItem(sheet) == "Раздел 0");

            // Готовый перевод заново не запрашивается.
            sheet.EnglishButton.IsChecked = true;
            sheet.NativeButton.IsChecked = true;
            Assert.Equal("Раздел 0", FirstItem(sheet));
            Assert.Equal(1, calls);
            Assert.True(sheet.LanguageSwitch.IsHitTestVisible);
        });
    }

    private static ResourceDictionary Palette(string name) => new()
    {
        Source = new Uri(
            "pack://application:,,,/" + Uri.EscapeDataString(typeof(WhatsNewSheet).Assembly.GetName().Name!) +
            ";component/UI/WhatsNew." + name + ".xaml",
            UriKind.Absolute)
    };

    /// <summary>Крутит очередь окна, пока ответ перевода не вернётся на её поток.</summary>
    private static void PumpUntil(Func<bool> done)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(watch.ElapsedMilliseconds < 10000, "перевод не вернулся");
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    private static string FirstItem(WhatsNewSheet sheet) =>
        (string)sheet.TocPanel.Children.OfType<RadioButton>().First().Content;

    private void With(WhatsNewTranslation? translation, Action<WhatsNewSheet> body, bool animations = false, bool dark = false) =>
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
                sheet.Show(ReleaseVersion.Parse("1.37.0")!.Value, Notes, translation, dark);
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
