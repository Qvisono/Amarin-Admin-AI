using System.Windows;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Доступ тестов к контролам настроек.
/// </summary>
/// <remarks>
/// Настройки больше не часть разметки окна: оболочка и страницы — отдельные UserControl со своими
/// областями имён, и создаются они по требованию. <c>window.FindName("NavData")</c> их не видит,
/// поэтому тесты ищут контрол здесь: помощник строит оболочку и все страницы (как это сделал бы
/// прогрев в простое) и ищет имя в оболочке, затем в каждой странице. Страницы, которые прежде были
/// именованными элементами окна, отдаются по прежним именам.
/// </remarks>
internal static class SettingsTestKit
{
    /// <summary>
    /// Именованный элемент окна или, если в окне такого нет, контрол настроек; <c>null</c>, если его
    /// нет нигде. Окно спрашивается первым и настроек не строит: через этот метод ищут и элементы
    /// ленты, и контролы страниц.
    /// </summary>
    /// <remarks>
    /// Тип результата — как у <see cref="FrameworkElement.FindName"/>, вместо которого его зовут:
    /// без пометки «может быть null», чтобы приведения в тестах читались так же, как прежде.
    /// </remarks>
    public static object FindSetting(this FrameworkElement owner, string name)
    {
        if (owner.FindName(name) is { } own)
        {
            return own;
        }

        if ((owner as MainWindow ?? Window.GetWindow(owner) as MainWindow) is not { } window)
        {
            return null!;
        }

        var view = BuildAll(window);
        return name switch
        {
            "KeyPage" => view.Page<SettingsKeyPage>(),
            "SecurityPage" => view.Page<SettingsSecurityPage>(),
            "InstructionsPage" => view.Page<SettingsInstructionsPage>(),
            "AutomationPage" => view.Page<SettingsAutomationPage>(),
            _ => view.FindName(name) ?? view.BuiltPages.Select(page => page.FindName(name)).FirstOrDefault(found => found is not null)!
        };
    }

    /// <summary>Оболочка настроек окна со всеми построенными страницами и их отложенным содержимым.</summary>
    public static SettingsView BuildAll(MainWindow window)
    {
        return window.CompleteSettingsBuild();
    }
}
