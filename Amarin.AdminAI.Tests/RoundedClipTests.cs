using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// A Border's ClipToBounds clips to its rectangular bounds, not its CornerRadius, so a child
/// Image paints square corners straight over the rounded background. These pin the replacement.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class RoundedClipTests
{
    private readonly WpfFixture _wpf;

    public RoundedClipTests(WpfFixture wpf) => _wpf = wpf;

    /// <summary>Lays the element out for real — the clip is built from RenderSize.</summary>
    private (double RadiusX, double RadiusY, double Width, double Height)? ClipOf(double size, double radius) =>
        _wpf.Ui.Invoke(() =>
        {
            var border = new Border { Width = size, Height = size };
            RoundedClip.SetRadius(border, radius);
            border.Measure(new Size(size, size));
            border.Arrange(new Rect(0, 0, size, size));
            border.UpdateLayout();

            return border.Clip is RectangleGeometry geometry
                ? ((double, double, double, double)?)(
                    geometry.RadiusX, geometry.RadiusY, geometry.Rect.Width, geometry.Rect.Height)
                : null;
        });

    [Fact]
    public void Sets_a_rounded_geometry_matching_the_element()
    {
        var clip = ClipOf(24, 6);

        Assert.NotNull(clip);
        Assert.Equal(6, clip!.Value.RadiusX, 3);
        Assert.Equal(6, clip.Value.RadiusY, 3);
        Assert.Equal(24, clip.Value.Width, 3);
        Assert.Equal(24, clip.Value.Height, 3);
    }

    [Fact]
    public void Follows_the_element_when_it_is_resized()
    {
        var sizes = _wpf.Ui.Invoke(() =>
        {
            var border = new Border { Width = 24, Height = 24 };
            RoundedClip.SetRadius(border, 6);
            border.Measure(new Size(24, 24));
            border.Arrange(new Rect(0, 0, 24, 24));
            border.UpdateLayout();
            var first = ((RectangleGeometry)border.Clip).Rect.Width;

            // UI scaling re-measures the tree, so a clip built once would be the wrong size.
            border.Width = 48;
            border.Height = 48;
            border.Measure(new Size(48, 48));
            border.Arrange(new Rect(0, 0, 48, 48));
            border.UpdateLayout();
            var second = ((RectangleGeometry)border.Clip).Rect.Width;

            return (first, second);
        });

        Assert.Equal(24, sizes.first, 3);
        Assert.Equal(48, sizes.second, 3);
    }

    [Fact]
    public void Never_exceeds_half_the_shortest_side()
    {
        // A radius larger than half the side pinches the shape in WPF; it must be capped.
        var clip = ClipOf(20, 40);

        Assert.NotNull(clip);
        Assert.Equal(10, clip!.Value.RadiusX, 3);
    }

    [Fact]
    public void A_zero_radius_clears_the_clip()
    {
        var cleared = _wpf.Ui.Invoke(() =>
        {
            var border = new Border { Width = 24, Height = 24 };
            RoundedClip.SetRadius(border, 6);
            border.Measure(new Size(24, 24));
            border.Arrange(new Rect(0, 0, 24, 24));
            border.UpdateLayout();

            RoundedClip.SetRadius(border, 0);
            return border.Clip is null;
        });

        Assert.True(cleared);
    }
}
