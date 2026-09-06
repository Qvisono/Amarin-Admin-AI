using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Folding the composer must be reversible down to the pixel. It animates the toolbar's height,
/// and an animation that hands back the wrong resting value is invisible on the way out and
/// permanent afterwards — the strip keeps the wrong size for the rest of the session.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ComposerCompactLayoutTests
{
    private readonly WpfFixture _wpf;

    public ComposerCompactLayoutTests(WpfFixture wpf) => _wpf = wpf;

    private static ComposerCompactMode Mode(MainWindow window) =>
        (ComposerCompactMode)typeof(MainWindow)
            .GetField("_compact", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    /// <summary>Drives a full fold/unfold without animation, then reads the layout back.</summary>
    private T AfterFoldCycle<T>(Func<MainWindow, T> read) => _wpf.Ui.Invoke(() =>
    {
        var window = Application.Current.Windows.OfType<MainWindow>().Single();
        var compact = Mode(window);
        var collapse = typeof(ComposerCompactMode).GetMethod(
            "Collapse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var expand = typeof(ComposerCompactMode).GetMethod(
            "Expand", BindingFlags.Instance | BindingFlags.NonPublic)!;

        try
        {
            collapse.Invoke(compact, [false]);
            window.UpdateLayout();
            expand.Invoke(compact, [false]);
            window.UpdateLayout();
            return read(window);
        }
        finally
        {
            expand.Invoke(compact, [false]);
            window.UpdateLayout();
        }
    });

    [Fact]
    public void The_toolbar_comes_back_to_the_height_the_markup_declares()
    {
        // It used to be handed back as NaN, i.e. "size to content". Content is a row of 32px
        // buttons, so the strip settled four pixels shorter than the declared 36 and everything
        // in it shifted -- which is what the send button changing size actually was.
        var (height, actual) = AfterFoldCycle(window =>
        {
            var toolbar = (Grid)window.FindName("ComposerToolbar");
            return (toolbar.Height, toolbar.ActualHeight);
        });

        Assert.False(double.IsNaN(height), "the toolbar was left sizing to its content");
        Assert.Equal(36, height);
        Assert.Equal(36, actual, 1);
    }

    /// <summary>Where the send button sits inside the composer, and how big it is.</summary>
    private static (double W, double H, double X, double Y) SendBox(MainWindow window)
    {
        var send = (FrameworkElement)window.FindName("SendButton");
        var composer = (FrameworkElement)window.FindName("ComposerBorder");
        var origin = send.TransformToAncestor(composer).Transform(new Point(0, 0));
        return (send.ActualWidth, send.ActualHeight, origin.X, origin.Y);
    }

    [Fact]
    public void The_send_button_lands_back_where_it_was()
    {
        // The button's own width and height are fixed, so it never resized -- what the user saw
        // was it sliding as the strip around it lost height. Position is the assertion that
        // catches it.
        var before = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            window.UpdateLayout();
            return SendBox(window);
        });

        var after = AfterFoldCycle(SendBox);

        Assert.Equal(before, after);
    }

    [Fact]
    public void The_balance_plate_keeps_its_size_across_a_fold()
    {
        // It is the one control in the strip with no fixed height of its own to fall back on,
        // so a shorter toolbar used to shrink it outright.
        var before = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var plate = (FrameworkElement)window.FindName("BalanceBadge");
            plate.Visibility = Visibility.Visible;
            window.UpdateLayout();
            return (plate.ActualWidth, plate.ActualHeight);
        });

        var after = AfterFoldCycle(window =>
        {
            var plate = (FrameworkElement)window.FindName("BalanceBadge");
            return (plate.ActualWidth, plate.ActualHeight);
        });

        _wpf.Ui.Invoke<bool>(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            ((FrameworkElement)window.FindName("BalanceBadge")).Visibility = Visibility.Collapsed;
            return true;
        });

        Assert.Equal(28, before.ActualHeight);
        Assert.Equal(before, after);
    }

    [Fact]
    public void The_composer_comes_back_to_its_full_width_and_row_height()
    {
        var (maxWidth, rowMinHeight) = AfterFoldCycle(window =>
        {
            var composer = (FrameworkElement)window.FindName("ComposerBorder");
            var row = (RowDefinition)window.FindName("ComposerRow");
            return (composer.MaxWidth, row.MinHeight);
        });

        Assert.Equal(1070, maxWidth);
        Assert.Equal(125, rowMinHeight);
    }

    [Fact]
    public void A_focused_but_empty_composer_still_folds() =>
        Assert.True(ComposerCompactMode.ShouldCollapse(
            enabled: true, isEmpty: true, toolbarFocused: false, hasAttachments: false, pointerNear: false));

    [Fact]
    public void Text_in_the_box_holds_it_open() =>
        Assert.False(ComposerCompactMode.ShouldCollapse(
            enabled: true, isEmpty: false, toolbarFocused: false, hasAttachments: false, pointerNear: false));
}
