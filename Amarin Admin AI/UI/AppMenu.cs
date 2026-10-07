using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Amarin.UI;

/// <summary>
/// Меню программы из общих стилей <c>Resources.xaml</c>: <c>AppContextMenu</c>, <c>AppMenuItem</c>,
/// <c>DangerMenuItem</c>, <c>AppMenuSeparator</c>.
/// </summary>
/// <remarks>
/// Прежде это были закрытые методы окна, и страница настроек, которой понадобилось своё меню
/// (ключи, 1.32.0), собрала бы его вторым кодом — а второй код рано или поздно расходится с
/// первым видом. Стили ищутся от <c>scope</c>: словарь подключён ресурсами главного окна, поэтому
/// элемент обязан стоять в нём — у страниц настроек так и есть.
/// </remarks>
internal static class AppMenu
{
    /// <summary>Меню у элемента: под ним (<see cref="PlacementMode.Bottom"/>) или у курсора.</summary>
    public static ContextMenu At(FrameworkElement anchor, PlacementMode placement = PlacementMode.Bottom) => new()
    {
        PlacementTarget = anchor,
        Placement = placement,
        Style = (Style)anchor.FindResource("AppContextMenu")
    };

    /// <param name="icon">Ключ геометрии значка из <c>Resources.xaml</c> (<c>Icon.Menu.*</c>).</param>
    /// <param name="enabled">Неактивный пункт стоит на месте приглушённым: меню не меняет вида.</param>
    public static MenuItem Item(
        FrameworkElement scope,
        string header,
        Action invoke,
        bool danger = false,
        string? icon = null,
        bool enabled = true)
    {
        var entry = new MenuItem
        {
            Header = header,
            Style = (Style)scope.FindResource(danger ? "DangerMenuItem" : "AppMenuItem"),
            IsEnabled = enabled
        };
        if (icon is not null)
        {
            MenuIcon.SetData(entry, (Geometry)scope.FindResource(icon));
        }

        entry.Click += (_, _) => invoke();
        return entry;
    }

    public static Separator Divider(FrameworkElement scope) => new() { Style = (Style)scope.FindResource("AppMenuSeparator") };

    /// <summary>Подпись раздела в меню: не пункт, а заголовок над группой пунктов.</summary>
    public static MenuItem Section(FrameworkElement scope, string text)
    {
        var label = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
        return new MenuItem
        {
            Header = label,
            Height = 24,
            Style = (Style)scope.FindResource("AppMenuItem"),
            IsHitTestVisible = false,
            Focusable = false
        };
    }
}
