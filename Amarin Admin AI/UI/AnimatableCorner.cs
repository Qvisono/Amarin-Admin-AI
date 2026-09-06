using System.Windows;
using System.Windows.Controls;

namespace Amarin.UI;

/// <summary>
/// A <see cref="double"/> attached property that writes through to <see cref="Border.CornerRadius"/>.
/// <para>
/// WPF has no <c>CornerRadiusAnimation</c> and <see cref="CornerRadius"/> is a struct, so the only
/// way to animate a rounding is to animate a double and project it. Shaped after
/// <see cref="RoundedClip"/>, which solves the neighbouring problem the same way.
/// </para>
/// </summary>
public static class AnimatableCorner
{
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.RegisterAttached(
            "Radius",
            typeof(double),
            typeof(AnimatableCorner),
            new PropertyMetadata(double.NaN, OnRadiusChanged));

    public static void SetRadius(DependencyObject element, double value) =>
        element.SetValue(RadiusProperty, value);

    public static double GetRadius(DependencyObject element) =>
        (double)element.GetValue(RadiusProperty);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border || e.NewValue is not double radius || double.IsNaN(radius))
        {
            return;
        }

        border.CornerRadius = new CornerRadius(Math.Max(0, radius));
    }
}
