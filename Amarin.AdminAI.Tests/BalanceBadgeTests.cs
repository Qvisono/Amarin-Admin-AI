using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The balance plate mixes its own colours out of the palette instead of declaring them, so the
/// thing that can silently break is a palette that does not answer — the plate would fall back to
/// grey on that theme alone and nobody would notice until they switched to it.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class BalanceBadgeTests
{
    private readonly WpfFixture _wpf;

    public BalanceBadgeTests(WpfFixture wpf) => _wpf = wpf;

    private static BalanceBadge Badge(MainWindow window) =>
        (BalanceBadge)typeof(MainWindow)
            .GetField("_balance", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static VeniceBalance Usd(decimal amount) => new() { CanConsume = true, Usd = amount };

    /// <summary>Shows a balance under a theme, reads the plate back, then restores the theme.</summary>
    private T Under<T>(AppTheme theme, VeniceBalance balance, Func<Border, TextBlock, T> read) =>
        _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var original = ThemeManager.Current.Theme;
            try
            {
                ThemeManager.Apply(theme);
                Badge(window).ShowForShot(balance.Usd, balance.Diem);
                window.UpdateLayout();
                return read(
                    (Border)window.FindName("BalanceBadge"),
                    (TextBlock)window.FindName("BalanceAmount"));
            }
            finally
            {
                ThemeManager.Apply(original);
                ((Border)window.FindName("BalanceBadge")).Visibility = Visibility.Collapsed;
            }
        });

    public static TheoryData<AppTheme> AllThemes()
    {
        var data = new TheoryData<AppTheme>();
        foreach (var preset in ThemeCatalog.Presets)
        {
            data.Add(preset.Theme);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Every_theme_gives_the_plate_a_real_colour(AppTheme theme)
    {
        var (text, background, border) = Under(theme, Usd(12.34m), (plate, amount) => (
            (amount.Foreground as SolidColorBrush)?.Color,
            (plate.Background as SolidColorBrush)?.Color,
            (plate.BorderBrush as SolidColorBrush)?.Color));

        Assert.NotNull(text);
        Assert.NotNull(background);
        Assert.NotNull(border);

        // Grey is the "no such resource" fallback inside the badge; hitting it means the palette
        // is missing Text.Muted.
        Assert.NotEqual(Colors.Gray, text!.Value);

        // The plate is a wash of its own text colour, not an opaque surface.
        Assert.Equal(text.Value.R, background!.Value.R);
        Assert.Equal(text.Value.G, background.Value.G);
        Assert.Equal(text.Value.B, background.Value.B);
        Assert.True(background.Value.A < 0x60, "the wash is too solid to read as a tint");
        Assert.True(border!.Value.A > background.Value.A, "the rim should be firmer than the fill");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void The_coin_is_always_the_same_colour_as_the_number(AppTheme theme)
    {
        var same = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var original = ThemeManager.Current.Theme;
            try
            {
                ThemeManager.Apply(theme);
                Badge(window).ShowForShot(5m);
                var coin = (System.Windows.Shapes.Path)window.FindName("BalanceCoin");
                var amount = (TextBlock)window.FindName("BalanceAmount");
                return ReferenceEquals(coin.Fill, amount.Foreground);
            }
            finally
            {
                ThemeManager.Apply(original);
                ((Border)window.FindName("BalanceBadge")).Visibility = Visibility.Collapsed;
            }
        });

        Assert.True(same, "the coin drifted away from the amount");
    }

    [Fact]
    public void The_plate_keeps_one_colour_whatever_the_balance_says()
    {
        // It is a readout, not an alarm. A number that turned red as it fell made the composer
        // flash for something the user could already read off the plate.
        Color Read(decimal amount) =>
            Under(AppTheme.Dark, Usd(amount), (_, text) => ((SolidColorBrush)text.Foreground).Color);

        var healthy = Read(20m);

        Assert.Equal(healthy, Read(0.50m));
        Assert.Equal(healthy, Read(0.05m));
    }

    [Fact]
    public void A_balance_that_is_running_out_still_says_so_in_the_tooltip()
    {
        var tooltip = Under(AppTheme.Dark, Usd(0.05m), (plate, _) => plate.ToolTip as string);

        Assert.NotNull(tooltip);
        Assert.Contains("почти закончились", tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_plate_shows_a_dash_rather_than_hiding_when_no_balance_arrived()
    {
        // The plate is part of the composer's furniture, so it stays put. "$0.00" would read as
        // "you are out of money", hence the dash; a figure appears from the cache or the first
        // answer.
        var (visible, text) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var plate = (Border)window.FindName("BalanceBadge");
            var badge = Badge(window);
            // Ни одна цифра не известна: ни своей, ни в книге — на плашке обязан стоять прочерк.
            badge.ShowForShot(usd: null);
            return (plate.Visibility, ((TextBlock)window.FindName("BalanceAmount")).Text);
        });

        Assert.Equal(Visibility.Visible, visible);
        Assert.Equal("-", text);
    }

    [Fact]
    public void A_response_that_carries_no_balance_leaves_the_last_figure_alone()
    {
        var text = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var badge = Badge(window);
            badge.ShowForShot(7.5m);
            var amount = ((TextBlock)window.FindName("BalanceAmount")).Text;
            ((Border)window.FindName("BalanceBadge")).Visibility = Visibility.Collapsed;
            return amount;
        });

        Assert.Equal("$7,50", text);
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData(0.0, "$0,00")]
    [InlineData(0.5, "$0,50")]
    [InlineData(9.999, "$10,00")]
    [InlineData(12.34, "$12,34")]
    [InlineData(1500.0, "$1 500")]
    public void The_amount_reads_the_way_a_price_should(double? usd, string expected) =>
        Assert.Equal(expected, BalanceBadge.FormatUsd(usd is null ? null : (decimal)usd.Value));
}
