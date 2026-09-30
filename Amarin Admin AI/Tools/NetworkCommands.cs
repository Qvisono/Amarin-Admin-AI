namespace Amarin.Tools;

/// <summary>
/// Сборка команд сетевых инструментов — отдельно от исполнения, чтобы её можно было проверить
/// тестами, ничего не запуская.
/// </summary>
/// <remarks>
/// Все значения от модели проходят здесь через перечень допустимых или через проверку формы и
/// уходят программе списком аргументов (<see cref="NativeProcess"/>), а не строкой.
/// </remarks>
internal static class NetworkCommands
{
    private static readonly string[] FirewallProfiles = ["domain", "private", "public", "all"];

    /// <summary>Профиль брандмауэра из перечня схемы; «all» — у netsh своё слово <c>allprofiles</c>.</summary>
    public static bool TryFirewallProfile(string? value, out string netshProfile)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        netshProfile = normalized switch
        {
            "all" => "allprofiles",
            _ when normalized is not null && FirewallProfiles.Contains(normalized) => normalized + "profile",
            _ => ""
        };
        return netshProfile.Length > 0;
    }

    public static IReadOnlyList<string> FirewallState(string netshProfile, bool enabled) =>
        ["advfirewall", "set", netshProfile, "state", enabled ? "on" : "off"];
}
