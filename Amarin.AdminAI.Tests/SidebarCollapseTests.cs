using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The collapsed sidebar is a 42px rail holding a 30px button, so its width budget has almost no
/// slack. Overshoot it and WPF does not complain — it silently applies a square layout clip, which
/// shears the rounded corner off one side of the hover plate and looks like a rendering glitch.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class SidebarCollapseTests
{
    private readonly WpfFixture _wpf;

    public SidebarCollapseTests(WpfFixture wpf) => _wpf = wpf;

    /// <summary>Drives the real collapse path, then restores the sidebar however the assert goes.</summary>
    private T WithCollapsedSidebar<T>(Func<MainWindow, T> read)
    {
        return _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var setCollapsed = typeof(MainWindow).GetMethod(
                "SetSidebarCollapsed",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            try
            {
                setCollapsed.Invoke(window, [true]);
                window.UpdateLayout();
                return read(window);
            }
            finally
            {
                setCollapsed.Invoke(window, [false]);
                window.UpdateLayout();
            }
        });
    }

    [Fact]
    public void Expand_button_fits_inside_the_collapsed_rail()
    {
        var (needed, available) = WithCollapsedSidebar(window =>
        {
            var button = (FrameworkElement)window.FindName("SidebarLogoButton");
            var grid = (FrameworkElement)button.Parent;
            var rail = (FrameworkElement)window.FindName("SideBarScrollViewer");

            // Measured values cannot see this: WPF clamps both DesiredSize and ActualWidth to
            // the space on offer, so an element that does not fit still reports as if it did.
            // The budget has to be added up from the declared width and margins instead.
            var required = button.Width
                           + button.Margin.Left + button.Margin.Right
                           + grid.Margin.Left + grid.Margin.Right;

            return (required, rail.ActualWidth);
        });

        Assert.True(
            needed <= available + 0.01,
            $"the expand button and its margins need {needed} of a {available}-wide rail — " +
            "the overhang gets clipped square, shearing the corner off the hover plate");
    }

    [Fact]
    public void Expand_button_is_hit_testable_all_the_way_to_its_right_edge()
    {
        // The symptom as the user meets it: the right slice of the plate is neither painted
        // nor clickable, even though layout still reports the button at full width.
        var reached = WithCollapsedSidebar(window =>
        {
            var button = (FrameworkElement)window.FindName("SidebarLogoButton");
            var rail = (FrameworkElement)window.FindName("SideBarScrollViewer");
            var origin = button.TransformToAncestor(rail).Transform(new Point(0, 0));

            // Two pixels inside the far corner, clear of the corner radius.
            var probe = new Point(
                origin.X + button.ActualWidth - 2,
                origin.Y + (button.ActualHeight / 2));

            var hit = false;
            VisualTreeHelper.HitTest(
                rail,
                null,
                result =>
                {
                    for (DependencyObject? node = result.VisualHit; node is not null;
                         node = VisualTreeHelper.GetParent(node))
                    {
                        if (ReferenceEquals(node, button))
                        {
                            hit = true;
                            return HitTestResultBehavior.Stop;
                        }
                    }

                    return HitTestResultBehavior.Continue;
                },
                new PointHitTestParameters(probe));

            return hit;
        });

        Assert.True(reached, "the right edge of the expand button is clipped away");
    }

    [Fact]
    public void Expand_button_sits_centred_in_the_collapsed_rail()
    {
        var (left, right, railWidth) = WithCollapsedSidebar(window =>
        {
            var button = (FrameworkElement)window.FindName("SidebarLogoButton");
            var rail = (FrameworkElement)window.FindName("SideBarScrollViewer");
            var origin = button.TransformToAncestor(rail).Transform(new Point(0, 0));
            return (origin.X, rail.ActualWidth - (origin.X + button.ActualWidth), rail.ActualWidth);
        });

        Assert.True(left > 0.5, $"no gap on the left ({left}) in a {railWidth}-wide rail");
        Assert.True(right > 0.5, $"no gap on the right ({right}) in a {railWidth}-wide rail");
        Assert.True(
            Math.Abs(left - right) <= 1.5,
            $"the button is off-centre in the rail: {left} left vs {right} right");
    }
}
