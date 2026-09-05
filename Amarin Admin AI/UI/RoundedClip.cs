using System.Windows;
using System.Windows.Media;

namespace Amarin.UI;

/// <summary>
/// Clips an element to a rounded rectangle.
/// <para>
/// <c>ClipToBounds</c> on a <see cref="System.Windows.Controls.Border"/> does not do this: it
/// clips to the element's <em>rectangular</em> layout rect, while <c>CornerRadius</c> only
/// affects what the Border paints itself — its own background and stroke. A child
/// <c>Image</c> therefore paints square corners straight over the rounded background, which is
/// why an avatar chip looks rounded until a photo is set and square immediately after.
/// </para>
/// <para>
/// The geometry is rebuilt on every size change rather than set once, because the app fakes DPI
/// for UI scaling (see <see cref="UiScale"/>) and elements are re-measured when the scale moves.
/// </para>
/// </summary>
public static class RoundedClip
{
    /// <summary>Corner radius to clip to. Match the container's <c>CornerRadius</c>.</summary>
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.RegisterAttached(
            "Radius",
            typeof(double),
            typeof(RoundedClip),
            new PropertyMetadata(0.0, OnRadiusChanged));

    public static void SetRadius(DependencyObject element, double value) =>
        element.SetValue(RadiusProperty, value);

    public static double GetRadius(DependencyObject element) =>
        (double)element.GetValue(RadiusProperty);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.SizeChanged -= OnSizeChanged;
        if (e.NewValue is not double radius || radius <= 0)
        {
            element.Clip = null;
            return;
        }

        element.SizeChanged += OnSizeChanged;
        Apply(element, radius);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Apply(element, GetRadius(element));
        }
    }

    private static void Apply(FrameworkElement element, double radius)
    {
        var size = element.RenderSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            // Not laid out yet — the SizeChanged handler will come back once it is.
            element.Clip = null;
            return;
        }

        // Never let the radius exceed half the shortest side, or WPF draws a pinched shape.
        var limit = Math.Min(size.Width, size.Height) / 2;
        var corner = Math.Min(radius, limit);

        var clip = new RectangleGeometry(new Rect(size), corner, corner);
        clip.Freeze();
        element.Clip = clip;
    }
}
