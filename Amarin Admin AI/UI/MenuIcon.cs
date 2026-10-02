using System.Windows;
using System.Windows.Media;

namespace Amarin.UI;

/// <summary>
/// Значок пункта меню. Шаблоны <c>AppMenuItem</c> и <c>DangerMenuItem</c> рисуют его обводкой
/// цвета текста, а пункт без значка остаётся прежним — колонка под значок схлопывается.
/// </summary>
/// <remarks>
/// Своё свойство, а не <c>MenuItem.Icon</c>: тому нужен отдельный элемент на каждый пункт, а
/// геометрия одна на все меню и заморожена.
/// </remarks>
public static class MenuIcon
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.RegisterAttached("Data", typeof(Geometry), typeof(MenuIcon), new PropertyMetadata(null));

    public static Geometry? GetData(DependencyObject element) => (Geometry?)element.GetValue(DataProperty);

    public static void SetData(DependencyObject element, Geometry? value) => element.SetValue(DataProperty, value);
}
