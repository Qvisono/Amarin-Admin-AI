using System.Text.RegularExpressions;

namespace Amarin.Tools;

/// <summary>Сборка команд для устройств: включить, отключить, узнать состояние.</summary>
internal static partial class DeviceCommands
{
    /// <summary>
    /// Классы устройств, которые не отключаются никогда: без них машина не загрузится, не будет
    /// ни клавиатуры, ни мыши, чтобы включить обратно, или пропадёт диск с самой системой.
    /// </summary>
    internal static readonly string[] ProtectedClasses =
    [
        "System", "Computer", "Processor", "DiskDrive", "HDC", "SCSIAdapter", "Volume",
        "VolumeSnapshot", "Keyboard", "Mouse", "SoftwareDevice", "FirmwareResources"
    ];

    /// <summary>
    /// Идентификатор экземпляра (<c>USB\VID_046D&amp;PID_C52B\5&amp;…</c>): без подстановочных знаков
    /// и кавычек. Со звёздочкой «отключи это устройство» отключило бы все подходящие.
    /// </summary>
    public static bool IsInstanceId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 400 && InstanceId().IsMatch(value);

    /// <summary>
    /// Включение или отключение. Перед отключением класс сверяется со списком защищённых — в
    /// самом скрипте, потому что класс знает только система.
    /// </summary>
    public static string SetEnabledScript(string instanceId, bool enable)
    {
        var id = PowerShellHelper.QuoteLiteral(instanceId);
        var protectedList = string.Join(",", ProtectedClasses.Select(name => "'" + name + "'"));
        return enable
            ? $$"""
                $ErrorActionPreference = 'Stop'
                Enable-PnpDevice -InstanceId '{{id}}' -Confirm:$false
                Get-PnpDevice -InstanceId '{{id}}' | Select-Object Status, Class, FriendlyName, InstanceId | Format-List
                """
            : $$"""
                $ErrorActionPreference = 'Stop'
                $device = Get-PnpDevice -InstanceId '{{id}}'
                if (@({{protectedList}}) -contains $device.Class) {
                  throw "PROTECTED_DEVICE_CLASS: $($device.Class) devices are never disabled - the machine could lose its disk, keyboard or mouse."
                }
                Disable-PnpDevice -InstanceId '{{id}}' -Confirm:$false
                Get-PnpDevice -InstanceId '{{id}}' | Select-Object Status, Class, FriendlyName, InstanceId | Format-List
                """;
    }

    /// <summary>«enabled» или «disabled»: код ошибки 22 диспетчера устройств — «отключено».</summary>
    public static string StateScript(string instanceId) => $$"""
        $ErrorActionPreference = 'Stop'
        $code = (Get-CimInstance Win32_PnPEntity -Filter "PNPDeviceID='{{WmiLiteral(instanceId)}}'").ConfigManagerErrorCode
        if ($code -eq 22) { 'disabled' } else { 'enabled' }
        """;

    /// <summary>
    /// Значение для фильтра WQL в двойных кавычках PowerShell: обратная косая удваивается (WQL),
    /// одинарная кавычка экранируется для WQL, а знаки подстановки PowerShell (<c>$</c>, <c>`</c>,
    /// <c>"</c>) в идентификатор не пропускает <see cref="IsInstanceId"/>.
    /// </summary>
    internal static string WmiLiteral(string instanceId) =>
        instanceId.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("'", @"\'", StringComparison.Ordinal);

    [GeneratedRegex(@"^[^\p{C}*?\[\]`""$]+$")]
    private static partial Regex InstanceId();
}
