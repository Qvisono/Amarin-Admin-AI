using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>Строка шпаргалки: что делает и чем.</summary>
internal sealed record HotkeySheetRow(string Title, string Gesture);

/// <summary>
/// Шпаргалка по сочетаниям (D8): список собран из <see cref="HotkeyMap.All"/> с тем, что
/// назначено сейчас, плюс клавиши, которые не переназначаются.
/// </summary>
public partial class HotkeySheetOverlay : UserControl
{
    public HotkeySheetOverlay() => InitializeComponent();

    /// <summary>«Изменить сочетания» — хозяин открывает страницу настроек.</summary>
    internal event Action? EditRequested;

    internal static IReadOnlyList<HotkeySheetRow> BuildRows(IReadOnlyDictionary<string, string>? assignments)
    {
        var rows = HotkeyMap.All
            .Select(action => new HotkeySheetRow(
                Loc.Get(action.TitleKey),
                HotkeyMap.Effective(assignments, action.Id) is { } gesture ? HotkeyMap.Display(gesture) : "-"))
            .ToList();

        // Эти не переназначаются: Enter и Esc у поля ввода — общее правило окон, а ↑ работает
        // только в пустом поле и ничего не отнимает у набора.
        rows.Add(new HotkeySheetRow(Loc.Get("S.Hotkeys.Send"), "Enter"));
        rows.Add(new HotkeySheetRow(Loc.Get("S.Hotkeys.NewLine"), "Shift + Enter"));
        rows.Add(new HotkeySheetRow(Loc.Get("S.Hotkeys.Stop"), "Esc"));
        rows.Add(new HotkeySheetRow(Loc.Get("S.Hotkeys.LastSent"), "↑"));
        return rows;
    }

    internal void Show(IReadOnlyDictionary<string, string>? assignments)
    {
        Rows.ItemsSource = BuildRows(assignments);
        Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => CloseButton.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    internal void Close() => Visibility = Visibility.Collapsed;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
        EditRequested?.Invoke();
    }

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    private void Overlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
