using Microsoft.Win32;

namespace Amarin.Core;

/// <summary>
/// Политики организации (1.37.0): то, что администратор задал программе через реестр машины
/// (<c>HKLM\SOFTWARE\Policies\Amarin\Admin AI</c>) или шаблоном ADMX из папки <c>policies/</c>.
/// </summary>
/// <remarks>
/// <para>
/// Политика накладывается при чтении и в <c>settings.json</c> не пишется: снятая
/// администратором, она возвращает человеку его прежние настройки, а не заводские. Политика
/// машинная — одна на все профили, — поэтому в архив данных не едет и из архива не приезжает.
/// </para>
/// <para>
/// Читается один раз при запуске (<see cref="Current"/> ставит корень композиции): изменённая
/// групповая политика действует после перезапуска программы.
/// </para>
/// </remarks>
public sealed record OrganizationPolicy
{
    /// <summary>Ключ политик под HKLM.</summary>
    public const string KeyPath = @"SOFTWARE\Policies\Amarin\Admin AI";

    public const string ReadOnlyValue = "ReadOnly";
    public const string DisableComputerControlValue = "DisableComputerControl";
    public const string WindowsEventLogValue = "WindowsEventLog";
    public const string DisabledToolsKey = "DisabledTools";
    public const string AllowedProvidersKey = "AllowedProviders";
    public const string AllowedModelsKey = "AllowedModels";

    /// <summary>Слово в списке провайдеров, разрешающее свои серверы: их id у каждого человека свои.</summary>
    public const string CustomServers = "custom";

    /// <summary>Политик нет.</summary>
    public static readonly OrganizationPolicy None = new();

    /// <summary>Действующая политика процесса. Ставит <c>AppComposition.ApplyProcessWide</c>.</summary>
    public static OrganizationPolicy Current { get; set; } = None;

    /// <summary>Режим «Только чтение» принудительно, для всех профилей и без исключений.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Управление компьютером мышью запрещено.</summary>
    public bool DisableComputerControl { get; init; }

    /// <summary>Копия журнала аудита в журнал Windows включена принудительно.</summary>
    public bool WindowsEventLog { get; init; }

    /// <summary>Инструменты, выключенные организацией, — имена переключателей <see cref="ToolCatalog"/>.</summary>
    public IReadOnlyList<string> DisabledTools { get; init; } = [];

    /// <summary>Разрешённые провайдеры (<see cref="LlmProvider.Id"/>); пусто — любые.</summary>
    public IReadOnlyList<string> AllowedProviders { get; init; } = [];

    /// <summary>
    /// Разрешённые модели: id как в программе, <c>*</c> в конце — совпадение по началу; пусто — любые.
    /// </summary>
    public IReadOnlyList<string> AllowedModels { get; init; } = [];

    /// <summary>Задано ли хоть что-то: страница «Безопасность» тогда говорит об этом строкой.</summary>
    public bool Any =>
        ReadOnly || DisableComputerControl || WindowsEventLog ||
        DisabledTools.Count > 0 || AllowedProviders.Count > 0 || AllowedModels.Count > 0;

    /// <summary>Выключила ли организация инструмент (по имени переключателя).</summary>
    public bool DisablesTool(string switchName) =>
        DisabledTools.Contains(switchName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Разрешены ли свои серверы человека.</summary>
    public bool AllowsCustomServers =>
        AllowedProviders.Count == 0 || AllowedProviders.Contains(CustomServers, StringComparer.OrdinalIgnoreCase);

    /// <summary>Можно ли ходить к провайдеру.</summary>
    public bool AllowsProvider(LlmProvider provider) =>
        provider.IsCustom
            ? AllowsCustomServers
            : AllowedProviders.Count == 0 || AllowedProviders.Contains(provider.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Можно ли спрашивать модель: и её провайдер, и она сама разрешены.</summary>
    public bool AllowsModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return true;
        }

        if (!AllowsProvider(ModelRef.Of(modelId)))
        {
            return false;
        }

        var id = modelId.Trim();
        return AllowedModels.Count == 0 || AllowedModels.Any(allowed => Matches(allowed, id));
    }

    /// <summary>
    /// Политика из реестра; <paramref name="root"/> — HKLM в программе, свой ключ в тестах.
    /// </summary>
    /// <remarks>Ключа нет или он не читается — политики нет: программа работает как раньше.</remarks>
    public static OrganizationPolicy Read(RegistryKey root, string keyPath = KeyPath)
    {
        ArgumentNullException.ThrowIfNull(root);
        try
        {
            using var key = root.OpenSubKey(keyPath);
            if (key is null)
            {
                return None;
            }

            return new OrganizationPolicy
            {
                ReadOnly = Flag(key, ReadOnlyValue),
                DisableComputerControl = Flag(key, DisableComputerControlValue),
                WindowsEventLog = Flag(key, WindowsEventLogValue),
                DisabledTools = List(key, DisabledToolsKey),
                AllowedProviders = List(key, AllowedProvidersKey),
                AllowedModels = List(key, AllowedModelsKey)
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            CrashLog.Write("organization policy: " + ex.Message);
            return None;
        }
    }

    /// <summary>Политика машины. Вне Windows её нет.</summary>
    public static OrganizationPolicy ReadMachine()
    {
        if (!OperatingSystem.IsWindows())
        {
            return None;
        }

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return Read(hklm);
    }

    private static bool Matches(string allowed, string id)
    {
        var pattern = allowed.Trim();
        return pattern.EndsWith('*')
            ? id.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, id, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Flag(RegistryKey key, string name) =>
        key.GetValue(name) is int value && value != 0;

    /// <summary>
    /// Список, как его пишет элемент <c>list</c> шаблона ADMX: подключ, где каждое значение —
    /// пункт (имена 1, 2, … или любые); у многострочного значения пункт — каждая строка.
    /// </summary>
    private static string[] List(RegistryKey key, string name)
    {
        using var list = key.OpenSubKey(name);
        if (list is null)
        {
            return [];
        }

        return list.GetValueNames()
            .SelectMany(value => list.GetValue(value) switch
            {
                string text => [text],
                string[] lines => lines,
                _ => []
            })
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
