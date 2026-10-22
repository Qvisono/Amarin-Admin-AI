using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Amarin.Tools;

/// <summary>Чьи переменные: текущего пользователя или всей машины.</summary>
internal enum EnvironmentScope
{
    User,
    Machine
}

/// <summary>Переменная так, как она лежит в реестре: сырое значение и раскрывается ли оно.</summary>
/// <param name="Expandable">REG_EXPAND_SZ: <c>%SystemRoot%</c> внутри раскрывается при входе.</param>
internal sealed record EnvironmentVariable(string Name, string Value, bool Expandable);

/// <summary>
/// Переменные среды пользователя и машины — там, где их хранит Windows: в разделах реестра.
/// </summary>
/// <remarks>
/// <para>
/// Не через <see cref="Environment.SetEnvironmentVariable(string, string, EnvironmentVariableTarget)"/>:
/// он пишет любое значение как REG_SZ, и PATH с <c>%SystemRoot%</c> переставал бы раскрываться —
/// у человека пропадали бы system32 и всё, что за ней. Здесь вид значения сохраняется.
/// </para>
/// <para>
/// После записи Windows рассылается WM_SETTINGCHANGE («Environment»): Проводник перечитывает
/// окружение, и программы, запущенные из него, видят новое. Окружение своего процесса
/// обновляется тоже: агент проверяет сделанное через PowerShell, а тот наследует окружение
/// программы — без обновления проверка видела бы старое значение и считала правку неудачной.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class EnvironmentStore
{
    public const string UserKey = @"HKCU\Environment";
    public const string MachineKey = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>Предел значения одной переменной в Windows, знаков.</summary>
    public const int MaxValueLength = 32_767;

    private readonly string _userKey;
    private readonly string _machineKey;
    private readonly Action<EnvironmentStore, IReadOnlyCollection<string>> _changed;

    public EnvironmentStore()
        : this(UserKey, MachineKey, static (store, names) => Announce(store, names))
    {
    }

    /// <param name="changed">
    /// Что сделать после записи. Тесты пишут в свой раздел и подставляют сюда запись имён: рассылка
    /// и обновление окружения процесса тестового значения разнесли бы его по настоящей системе.
    /// </param>
    internal EnvironmentStore(string userKey, string machineKey, Action<EnvironmentStore, IReadOnlyCollection<string>> changed)
    {
        _userKey = userKey;
        _machineKey = machineKey;
        _changed = changed;
    }

    public string KeyOf(EnvironmentScope scope) => scope == EnvironmentScope.User ? _userKey : _machineKey;

    /// <summary>Все переменные области по имени; раздела нет — пусто.</summary>
    public IReadOnlyList<EnvironmentVariable> Read(EnvironmentScope scope)
    {
        using var key = Open(scope, writable: false);
        if (key is null)
        {
            return [];
        }

        var result = new List<EnvironmentVariable>();
        foreach (var name in key.GetValueNames().Where(name => name.Length > 0).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (ReadValue(key, name) is { } variable)
            {
                result.Add(variable);
            }
        }

        return result;
    }

    public EnvironmentVariable? Get(EnvironmentScope scope, string name)
    {
        using var key = Open(scope, writable: false);
        return key is null ? null : ReadValue(key, name);
    }

    /// <exception cref="UnauthorizedAccessException">Нет прав на раздел (машинный — без администратора).</exception>
    public void Write(EnvironmentScope scope, string name, string value, bool expandable)
    {
        using (var key = Open(scope, writable: true) ?? throw new UnauthorizedAccessException(KeyOf(scope)))
        {
            key.SetValue(name, value, expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
        }

        _changed(this, [name]);
    }

    /// <returns>false — такой переменной не было.</returns>
    public bool Delete(EnvironmentScope scope, string name)
    {
        using (var key = Open(scope, writable: true) ?? throw new UnauthorizedAccessException(KeyOf(scope)))
        {
            if (key.GetValue(name) is null)
            {
                return false;
            }

            key.DeleteValue(name, throwOnMissingValue: false);
        }

        _changed(this, [name]);
        return true;
    }

    /// <summary>
    /// Откат вернул значения разделов окружения — оповестить так же, как после правки, иначе
    /// откат лёг бы в реестр, а Проводник и программа продолжали бы жить с отменённым.
    /// </summary>
    public static void AfterRestore(IEnumerable<RegistryChange> changes)
    {
        var names = RestoredNames(changes);
        if (names.Count > 0)
        {
            Announce(new EnvironmentStore(), names);
        }
    }

    /// <summary>Имена переменных, которые вернул откат: значения разделов окружения среди шагов реестра.</summary>
    internal static IReadOnlyList<string> RestoredNames(IEnumerable<RegistryChange> changes) =>
        changes
            .Where(change => IsEnvironmentKey(change.KeyPath) && !string.IsNullOrEmpty(change.ValueName))
            .Select(change => change.ValueName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsEnvironmentKey(string keyPath) =>
        keyPath.Equals(UserKey, StringComparison.OrdinalIgnoreCase) ||
        keyPath.Equals(MachineKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Значение для окружения процесса так, как его собирает Windows при входе: PATH — машинный и
    /// за ним пользовательский, остальное — пользовательское поверх машинного.
    /// </summary>
    internal static string? Effective(string name, EnvironmentVariable? machine, EnvironmentVariable? user)
    {
        static string? Expanded(EnvironmentVariable? variable) =>
            variable is null ? null : variable.Expandable ? Environment.ExpandEnvironmentVariables(variable.Value) : variable.Value;

        if (name.Equals("Path", StringComparison.OrdinalIgnoreCase))
        {
            var parts = new[] { Expanded(machine), Expanded(user) }.Where(part => !string.IsNullOrEmpty(part)).ToList();
            return parts.Count == 0 ? null : string.Join(';', parts);
        }

        return Expanded(user) ?? Expanded(machine);
    }

    private static void Announce(EnvironmentStore store, IReadOnlyCollection<string> names)
    {
        foreach (var name in names)
        {
            try
            {
                Environment.SetEnvironmentVariable(
                    name, Effective(name, store.Get(EnvironmentScope.Machine, name), store.Get(EnvironmentScope.User, name)));
            }
            catch (ArgumentException)
            {
                // Имя, которое процесс не принимает (слишком длинное значение), — окружение
                // процесса останется прежним; реестр и рассылка своё уже сделали.
            }
        }

        _ = SendMessageTimeout(HwndBroadcast, WmSettingChange, UIntPtr.Zero, "Environment", SmtoAbortIfHung, 1000, out _);
    }

    private RegistryKey? Open(EnvironmentScope scope, bool writable)
    {
        if (!RegistryPath.TryParse(KeyOf(scope), out var path))
        {
            return null;
        }

        using var root = RegistryStateIo.OpenRoot(path.Root);
        if (root is null)
        {
            return null;
        }

        try
        {
            // Раздел пользователя есть всегда, а тестовый может ещё не существовать.
            return writable ? root.CreateSubKey(path.SubKey, writable: true) : root.OpenSubKey(path.SubKey, writable: false);
        }
        catch (System.Security.SecurityException ex)
        {
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
    }

    private static EnvironmentVariable? ReadValue(RegistryKey key, string name)
    {
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
        {
            return null;
        }

        var kind = key.GetValueKind(name);
        return new EnvironmentVariable(name, value as string ?? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            kind == RegistryValueKind.ExpandString);
    }

    private static readonly IntPtr HwndBroadcast = new(0xffff);
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
}
