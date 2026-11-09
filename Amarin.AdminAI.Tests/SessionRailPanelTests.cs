using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Проверка только раскладки концепта: демонстрационные данные к логике хода не подключены.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class SessionRailPanelTests
{
    private readonly WpfFixture _wpf;

    public SessionRailPanelTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void The_rail_appears_only_when_the_window_is_wide_enough()
    {
        Assert.False(SessionRailPanel.FitsWindow(SessionRailPanel.MinWindowWidth - 1));
        Assert.True(SessionRailPanel.FitsWindow(SessionRailPanel.MinWindowWidth));
    }

    [Fact]
    public void Cards_yield_space_from_the_bottom_and_return_when_it_is_available()
    {
        _wpf.Ui.Invoke(() =>
        {
            var panel = new SessionRailPanel { Width = 250 };

            Arrange(panel, 800);
            Assert.Equal(Visibility.Visible, panel.ContextCard.Visibility);
            Assert.Equal(Visibility.Visible, panel.SettingsCard.Visibility);
            Assert.Equal(Visibility.Visible, panel.FilesCard.Visibility);
            Assert.Equal(Visibility.Visible, panel.AgentsCard.Visibility);

            Arrange(panel, 480);
            Assert.Equal(Visibility.Visible, panel.ContextCard.Visibility);
            Assert.Equal(Visibility.Visible, panel.SettingsCard.Visibility);
            Assert.Equal(Visibility.Visible, panel.FilesCard.Visibility);
            Assert.Equal(Visibility.Collapsed, panel.AgentsCard.Visibility);

            Arrange(panel, 340);
            Assert.Equal(Visibility.Visible, panel.ContextCard.Visibility);
            Assert.Equal(Visibility.Visible, panel.SettingsCard.Visibility);
            Assert.Equal(Visibility.Collapsed, panel.FilesCard.Visibility);
            Assert.Equal(Visibility.Collapsed, panel.AgentsCard.Visibility);

            Arrange(panel, 220);
            Assert.Equal(Visibility.Visible, panel.ContextCard.Visibility);
            Assert.Equal(Visibility.Collapsed, panel.SettingsCard.Visibility);
            Assert.Equal(Visibility.Collapsed, panel.FilesCard.Visibility);
            Assert.Equal(Visibility.Collapsed, panel.AgentsCard.Visibility);

            Arrange(panel, 800);
            Assert.Equal(Visibility.Visible, panel.ContextCard.Visibility);
            Assert.Equal(Visibility.Visible, panel.SettingsCard.Visibility);
            Assert.Equal(Visibility.Visible, panel.FilesCard.Visibility);
            Assert.Equal(Visibility.Visible, panel.AgentsCard.Visibility);
            return true;
        });
    }

    [Fact]
    public void Agent_logos_come_from_the_catalog_and_status_marks_share_an_axis()
    {
        _wpf.Ui.Invoke(() =>
        {
            var panel = new SessionRailPanel { Width = 250 };
            Arrange(panel, 800);

            Assert.NotNull(panel.ResearchModelLogo.Source);
            Assert.NotNull(panel.EditorModelLogo.Source);
            Assert.NotNull(panel.ReviewModelLogo.Source);
            Assert.Same(panel.ResearchModelLogo.Source, panel.EditorModelLogo.Source);
            Assert.NotSame(panel.ResearchModelLogo.Source, panel.ReviewModelLogo.Source);
            Assert.Equal(Visibility.Collapsed, panel.ResearchModelLetter.Visibility);
            Assert.Equal(Visibility.Collapsed, panel.EditorModelLetter.Visibility);
            Assert.Equal(Visibility.Collapsed, panel.ReviewModelLetter.Visibility);

            var workingX = Center(panel, panel.ResearchActivity).X;
            Assert.Equal(workingX, Center(panel, panel.EditorActivity).X, 3);
            Assert.Equal(workingX, Center(panel, panel.ReviewDone).X, 3);
            Assert.Equal(Center(panel, panel.ResearchModelLogo).Y, Center(panel, panel.ResearchActivity).Y, 3);
            Assert.Equal(Center(panel, panel.EditorModelLogo).Y, Center(panel, panel.EditorActivity).Y, 3);
            Assert.Equal(Center(panel, panel.ReviewModelLogo).Y, Center(panel, panel.ReviewDone).Y, 3);
            return true;
        });
    }

    [Fact]
    public void Legend_dots_and_percentages_share_the_axes_of_the_card_header()
    {
        _wpf.Ui.Invoke(() =>
        {
            var panel = new SessionRailPanel { Width = 250 };
            Arrange(panel, 800);

            var title = Left(panel, panel.ContextTitle);
            Assert.Equal(title, Left(panel, panel.ConversationDot), 1);

            var percent = Right(panel, panel.ContextPercent);
            Assert.Equal(percent, Right(panel, panel.ConversationPercent), 1);

            // Точка не выше середины строки: у строчных букв центр ниже геометрического центра блока.
            var dot = Center(panel, panel.ConversationDot).Y;
            var label = Center(panel, panel.ConversationLabel).Y;
            Assert.InRange(dot - label, 0, 2);
            return true;
        });
    }

    [Fact]
    public void Session_settings_are_two_independent_switches()
    {
        _wpf.Ui.Invoke(() =>
        {
            var panel = new SessionRailPanel { Width = 250 };
            Arrange(panel, 800);

            Assert.True(panel.ComputerToggle.IsChecked);
            Assert.False(panel.BrowserToggle.IsChecked);
            Assert.Same(panel.ComputerToggle.Style, panel.BrowserToggle.Style);

            panel.BrowserToggle.IsChecked = true;
            Assert.True(panel.ComputerToggle.IsChecked);
            panel.ComputerToggle.IsChecked = false;
            Assert.True(panel.BrowserToggle.IsChecked);

            Assert.Equal(Center(panel, panel.ComputerToggle).X, Center(panel, panel.BrowserToggle).X, 3);
            return true;
        });
    }

    [Fact]
    public void Compact_screen_replaces_the_cards_and_back_returns_them()
    {
        _wpf.Ui.Invoke(() =>
        {
            var panel = new SessionRailPanel { Width = 250 };
            Arrange(panel, 800);
            Assert.False(panel.IsCompactOpen);
            Assert.Equal(Visibility.Visible, panel.CardsPanel.Visibility);

            Click(panel.CompactButton);
            Arrange(panel, 800);
            Assert.True(panel.IsCompactOpen);
            Assert.Equal(Visibility.Collapsed, panel.CardsPanel.Visibility);

            // Экран сжатия стоит в той же колонке, что и карточки, а не выезжает за панель.
            Assert.Equal(0, panel.CompactView.TranslatePoint(new Point(0, 0), panel).X, 3);
            Assert.Equal(panel.ActualWidth, panel.CompactView.ActualWidth, 3);

            Click(panel.CompactBackButton);
            Arrange(panel, 800);
            Assert.False(panel.IsCompactOpen);
            Assert.Equal(Visibility.Visible, panel.CardsPanel.Visibility);
            Assert.Equal(Visibility.Visible, panel.AgentsCard.Visibility);

            Click(panel.CompactButton);
            Click(panel.CompactRunButton);
            Assert.False(panel.IsCompactOpen);
            return true;
        });
    }

    [Fact]
    public void Compact_screen_has_the_documented_defaults_and_a_bounded_counter()
    {
        _wpf.Ui.Invoke(() =>
        {
            var panel = new SessionRailPanel { Width = 250 };
            Arrange(panel, 800);

            Assert.True(panel.CompactFirstToggle.IsChecked);
            Assert.Equal(SessionRailPanel.DefaultKeep.ToString(System.Globalization.CultureInfo.CurrentCulture), panel.KeepValue.Text);
            Assert.NotNull(panel.CompactModelLogo.Source);
            Assert.Contains((SessionRailPanel.DemoMessages - SessionRailPanel.DefaultKeep).ToString(System.Globalization.CultureInfo.CurrentCulture), panel.KeepHint.Text);

            for (var i = 0; i < 100; i++)
            {
                Click(panel.KeepLessButton);
            }

            Assert.Equal("2", panel.KeepValue.Text);
            Assert.False(panel.KeepLessButton.IsEnabled);
            Assert.True(panel.KeepMoreButton.IsEnabled);

            for (var i = 0; i < 100; i++)
            {
                Click(panel.KeepMoreButton);
            }

            Assert.Equal("40", panel.KeepValue.Text);
            Assert.False(panel.KeepMoreButton.IsEnabled);
            Assert.True(panel.KeepLessButton.IsEnabled);
            return true;
        });
    }

    [Fact]
    public void Working_indicator_draws_a_bright_head_with_a_fading_tail()
    {
        _wpf.Ui.Invoke(() =>
        {
            var indicator = new AgentWorkingIndicator();
            indicator.Measure(new Size(50, 50));
            indicator.Arrange(new Rect(0, 0, 16, 16));

            var opacity = indicator.Ring.Children.Cast<System.Windows.Shapes.Ellipse>().Select(dot => dot.Opacity).ToArray();
            Assert.Equal(8, opacity.Length);
            Assert.Equal(1.0, opacity[0], 3);
            Assert.True(opacity.Skip(1).All(value => value < 1.0));

            // Хвост тянется против хода: чем дальше точка позади головы, тем она тусклее.
            for (var i = 7; i > 3; i--)
            {
                Assert.True(opacity[i] >= opacity[i - 1], $"точка {i} не ярче точки {i - 1}");
            }

            return true;
        });
    }

    /// <summary>Снимки для визуальной проверки: <c>AMARIN_RAIL_SHOTS=&lt;папка&gt;</c>, обычный прогон файлов не создаёт.</summary>
    [Fact]
    public void Write_shots_when_requested()
    {
        var directory = Environment.GetEnvironmentVariable("AMARIN_RAIL_SHOTS");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        _wpf.Ui.Invoke(() =>
        {
            var previous = ThemeManager.Current.Theme;
            try
            {
                foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
                {
                    ThemeManager.Apply(theme);
                    var panel = new SessionRailPanel { Width = 250 };
                    Arrange(panel, 560);
                    var background = (Brush)Application.Current.Resources["Bg.Window"];
                    var name = theme.ToString().ToLowerInvariant();
                    Save(panel, Path.Combine(directory, $"rail-{name}.png"), background);

                    Click(panel.CompactButton);
                    Arrange(panel, 560);
                    Save(panel, Path.Combine(directory, $"compact-{name}.png"), background);
                }
            }
            finally
            {
                ThemeManager.Apply(previous);
            }

            return true;
        });
    }

    private static void Save(FrameworkElement element, string path, Brush background)
    {
        const double scale = 2;
        var width = element.ActualWidth;
        var height = element.ActualHeight;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var area = new Rect(0, 0, width, height);
            context.DrawRectangle(background, null, area);
            context.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, area);
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Click(System.Windows.Controls.Primitives.ButtonBase button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static double Left(SessionRailPanel panel, FrameworkElement element) =>
        element.TranslatePoint(new Point(0, 0), panel).X;

    private static double Right(SessionRailPanel panel, FrameworkElement element) =>
        element.TranslatePoint(new Point(element.ActualWidth, 0), panel).X;

    private static Point Center(SessionRailPanel panel, FrameworkElement element) =>
        element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), panel);

    private static void Arrange(SessionRailPanel panel, double height)
    {
        panel.Height = height;
        panel.Measure(new Size(250, height));
        panel.Arrange(new Rect(0, 0, 250, height));
        panel.UpdateLayout();
    }
}
