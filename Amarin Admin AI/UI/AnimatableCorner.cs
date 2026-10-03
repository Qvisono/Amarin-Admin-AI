using System.Windows;
using System.Windows.Controls;

namespace Amarin.UI;

/// <summary>
/// Присоединённое свойство <see cref="double"/>, которое пишется в <see cref="Border.CornerRadius"/>.
/// <para>
/// <c>CornerRadiusAnimation</c> в WPF нет, а <see cref="CornerRadius"/> — структура, поэтому
/// анимировать скругление можно лишь через число. Устроено как <see cref="RoundedClip"/>, который
/// так же решает соседнюю задачу.
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
