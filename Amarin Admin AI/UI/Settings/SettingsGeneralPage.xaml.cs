using System.Windows.Controls;

namespace Amarin.UI;

/// <summary>Страница настроек «Общие».</summary>
/// <remarks>
/// Только разметка: контролы ведёт главное окно (тематические partial-файлы), а подписывает их
/// на обработчики MainWindow.SettingsHost.cs при создании страницы.
/// </remarks>
public sealed partial class SettingsGeneralPage : UserControl
{
    public SettingsGeneralPage() => InitializeComponent();
}