namespace Amarin.AdminAI.Tests;

/// <summary>Пункты навигации настроек — один список на все тесты.</summary>
/// <remarks>
/// Прежде каждый тест перечислял пункты сам, и при смене состава навигации тесты расходились
/// с ней по одному: забытый пункт молча выпадал из проверки «заголовок в одной точке».
/// </remarks>
internal static class SettingsNavNames
{
    /// <summary>В порядке колонки навигации.</summary>
    public static readonly string[] All =
    [
        "NavGeneral", "NavAppearance", "NavProfile",
        "NavModels", "NavPrompts", "NavInstructions", "NavAutomation",
        "NavSecurity", "NavKey", "NavData", "NavAbout"
    ];
}
