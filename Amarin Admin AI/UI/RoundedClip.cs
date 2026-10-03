using System.Windows;
using System.Windows.Media;

namespace Amarin.UI;

/// <summary>
/// Обрезает элемент по скруглённому прямоугольнику.
/// <para>
/// <c>ClipToBounds</c> у <see cref="System.Windows.Controls.Border"/> этого не делает: он режет по
/// <em>прямоугольнику</em> раскладки, а <c>CornerRadius</c> влияет лишь на то, что рисует сам
/// Border, — фон и обводку. Дочерний <c>Image</c> рисует квадратные углы прямо поверх скруглённого
/// фона, и плашка аватара круглая, пока нет фото, и квадратная сразу после.
/// </para>
/// <para>
/// Геометрия пересобирается при каждой смене размера: масштаб интерфейса — поддельный DPI (см.
/// <see cref="UiScale"/>), и при его смене элементы меряются заново.
/// </para>
/// </summary>
public static class RoundedClip
{
    /// <summary>Радиус обрезки. Должен совпадать с <c>CornerRadius</c> контейнера.</summary>
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
            // Раскладки ещё нет — вернёмся по SizeChanged.
            element.Clip = null;
            return;
        }

        // Радиус не больше половины короткой стороны, иначе WPF рисует сплющенную фигуру.
        var limit = Math.Min(size.Width, size.Height) / 2;
        var corner = Math.Min(radius, limit);

        var clip = new RectangleGeometry(new Rect(size), corner, corner);
        clip.Freeze();
        element.Clip = clip;
    }
}
