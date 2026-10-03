namespace Amarin.Tools;

/// <summary>Сборка команд для служб.</summary>
internal static class ServiceCommands
{
    /// <summary>Тип запуска от модели → слово <c>sc.exe config start=</c>.</summary>
    /// <remarks>
    /// Через sc.exe, а не <c>Set-Service</c>: у Windows PowerShell 5.1 нет «автоматически
    /// (отложенный запуск)», а он нужен — ровно так выключают тяжёлые службы из старта, не
    /// ломая их.
    /// </remarks>
    public static bool TryStartType(string? value, out string scValue)
    {
        scValue = (value ?? "").Trim().ToLowerInvariant() switch
        {
            "automatic" => "auto",
            "automatic_delayed" => "delayed-auto",
            "manual" => "demand",
            "disabled" => "disabled",
            _ => ""
        };
        return scValue.Length > 0;
    }

    /// <summary>Аргументы <c>sc.exe</c>: «start=» — отдельный аргумент, как этого требует sc.</summary>
    public static IReadOnlyList<string> ConfigArguments(string serviceName, string scValue) =>
        ["config", serviceName, "start=", scValue];

    /// <summary>Раздел службы в реестре — снимок отката берёт его значения Start и DelayedAutostart.</summary>
    public static string RegistryKey(string serviceName) =>
        @"HKLM\SYSTEM\CurrentControlSet\Services\" + serviceName;
}
