using System.Text.RegularExpressions;

namespace Amarin.Tools;

/// <summary>Скрипты Bluetooth для <see cref="BluetoothTool"/>. Без Windows — проверяются тестами.</summary>
/// <remarks>
/// <para>
/// Радио и сопряжение есть только в WinRT (Windows.Devices.Radios, Windows.Devices.Bluetooth), а
/// у этой сборки нет проекций WinRT. Windows PowerShell 5.1 их загружает сам — поэтому скрипт, и
/// заодно отдельный процесс: сбой WinRT не уронит программу.
/// </para>
/// <para>
/// Заряд берётся из свойства устройства, которое заполняет сам стек Bluetooth (так его видят
/// «Параметры»), а у устройств LE без него — из стандартной службы батареи GATT, только из кэша:
/// живое чтение будило бы спящее устройство.
/// </para>
/// </remarks>
internal static partial class BluetoothCommands
{
    /// <summary>Ключ свойства PnP «заряд батареи Bluetooth».</summary>
    internal const string BatteryProperty = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    /// <summary>Общая часть: загрузка типов WinRT и ожидание их асинхронных операций.</summary>
    internal const string Prelude = """
        $ErrorActionPreference = 'Stop'
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
          $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
        function Await($operation, [Type]$type) {
          $task = $asTask.MakeGenericMethod($type).Invoke($null, @($operation))
          if (-not $task.Wait(15000)) { throw 'WINRT_TIMEOUT' }
          $task.Result
        }
        [Windows.Devices.Radios.Radio, Windows.System.Devices, ContentType = WindowsRuntime] | Out-Null
        [Windows.Devices.Radios.RadioAccessStatus, Windows.System.Devices, ContentType = WindowsRuntime] | Out-Null
        [Windows.Devices.Radios.RadioState, Windows.System.Devices, ContentType = WindowsRuntime] | Out-Null
        [Windows.Devices.Enumeration.DeviceInformation, Windows.Devices.Enumeration, ContentType = WindowsRuntime] | Out-Null
        [Windows.Devices.Bluetooth.BluetoothDevice, Windows.Devices.Bluetooth, ContentType = WindowsRuntime] | Out-Null
        [Windows.Devices.Bluetooth.BluetoothLEDevice, Windows.Devices.Bluetooth, ContentType = WindowsRuntime] | Out-Null
        function BluetoothRadio {
          $script:access = Await ([Windows.Devices.Radios.Radio]::RequestAccessAsync()) ([Windows.Devices.Radios.RadioAccessStatus])
          $radios = Await ([Windows.Devices.Radios.Radio]::GetRadiosAsync()) ([System.Collections.Generic.IReadOnlyList[Windows.Devices.Radios.Radio]])
          $radios | Where-Object { [string]$_.Kind -eq 'Bluetooth' } | Select-Object -First 1
        }
        function Paired {
          foreach ($le in $false, $true) {
            $selector = if ($le) { [Windows.Devices.Bluetooth.BluetoothLEDevice]::GetDeviceSelectorFromPairingState($true) }
                        else { [Windows.Devices.Bluetooth.BluetoothDevice]::GetDeviceSelectorFromPairingState($true) }
            $found = Await ([Windows.Devices.Enumeration.DeviceInformation]::FindAllAsync($selector)) ([Windows.Devices.Enumeration.DeviceInformationCollection])
            foreach ($info in $found) {
              $device = if ($le) { Await ([Windows.Devices.Bluetooth.BluetoothLEDevice]::FromIdAsync($info.Id)) ([Windows.Devices.Bluetooth.BluetoothLEDevice]) }
                        else { Await ([Windows.Devices.Bluetooth.BluetoothDevice]::FromIdAsync($info.Id)) ([Windows.Devices.Bluetooth.BluetoothDevice]) }
              if (-not $device) { continue }
              [PSCustomObject]@{ Info = $info; Device = $device; LE = $le; Address = ('{0:X12}' -f $device.BluetoothAddress) }
            }
          }
        }
        """;

    // Адаптеры собираются в массив, а не выводятся последней командой: пустой Get-PnpDevice на ПК
    // без Bluetooth дал бы процессу код 1, и состояние «адаптера нет» выглядело бы как сбой.
    public const string StatusScript = Prelude + """

        $radio = BluetoothRadio
        "Radio: $(if ($radio) { $radio.State } else { 'none' }) (app access: $script:access)"
        $service = Get-Service -Name bthserv -ErrorAction SilentlyContinue
        "Bluetooth Support Service: $(if ($service) { "$($service.Status), $($service.StartType)" } else { 'not installed' })"
        'Adapters:'
        $adapters = @(Get-PnpDevice -Class Bluetooth -ErrorAction SilentlyContinue |
          Where-Object { $_.InstanceId -notmatch '^(BTHENUM|BTHLE|BTHLEDEVICE|BTHHFENUM|BTHMODEM|BTH\\)' })
        if ($adapters.Count -eq 0) { '  none' } else { $adapters | ForEach-Object { "  $($_.FriendlyName): $($_.Status)" } }
        """;

    public const string DevicesScript = Prelude + $$"""

        [Windows.Devices.Bluetooth.GenericAttributeProfile.GattDeviceService, Windows.Devices.Bluetooth, ContentType = WindowsRuntime] | Out-Null
        [Windows.Storage.Streams.DataReader, Windows.Storage.Streams, ContentType = WindowsRuntime] | Out-Null
        $pnp = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match '^(BTHENUM|BTHLE|BTHLEDEVICE)' })
        $rows = foreach ($item in @(Paired)) {
          $battery = $null
          foreach ($node in @($pnp | Where-Object { $_.InstanceId -match $item.Address })) {
            $value = (Get-PnpDeviceProperty -InstanceId $node.InstanceId -KeyName '{{BatteryProperty}}' -ErrorAction SilentlyContinue).Data
            if ($value -ne $null) { $battery = [int]$value; break }
          }
          $connected = [string]$item.Device.ConnectionStatus -eq 'Connected'
          if ($battery -eq $null -and $item.LE -and $connected) {
            try {
              $cached = [Windows.Devices.Bluetooth.BluetoothCacheMode]::Cached
              $services = Await ($item.Device.GetGattServicesForUuidAsync([Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceUuids]::Battery, $cached)) ([Windows.Devices.Bluetooth.GenericAttributeProfile.GattDeviceServicesResult])
              if ([string]$services.Status -eq 'Success' -and $services.Services.Count -gt 0) {
                $chars = Await ($services.Services[0].GetCharacteristicsForUuidAsync([Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicUuids]::BatteryLevel, $cached)) ([Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicsResult])
                if ([string]$chars.Status -eq 'Success' -and $chars.Characteristics.Count -gt 0) {
                  $read = Await ($chars.Characteristics[0].ReadValueAsync($cached)) ([Windows.Devices.Bluetooth.GenericAttributeProfile.GattReadResult])
                  if ([string]$read.Status -eq 'Success') { $battery = [int][Windows.Storage.Streams.DataReader]::FromBuffer($read.Value).ReadByte() }
                }
              }
            } catch { }
          }
          $address = ($item.Address -replace '(..)(?!$)', '$1:')
          [PSCustomObject]@{
            Name = $item.Info.Name
            Address = $address
            Type = $(if ($item.LE) { 'LE' } else { 'Classic' })
            Connected = $connected
            Battery = $(if ($battery -ne $null) { "$battery%" } else { 'not reported' })
            Id = $item.Info.Id
          }
          $item.Device.Dispose()
        }
        if (-not $rows) { 'No paired Bluetooth devices.' } else { $rows | Format-List | Out-String -Width 300 }
        """;

    public static string RadioScript(bool on) => Prelude + $$"""

        $radio = BluetoothRadio
        if (-not $radio) { "NO_RADIO $script:access"; return }
        $result = Await ($radio.SetStateAsync([Windows.Devices.Radios.RadioState]::{{(on ? "On" : "Off")}})) ([Windows.Devices.Radios.RadioAccessStatus])
        "RESULT $result"
        """;

    /// <summary>Забыть сопряжённое устройство по его id или адресу из <c>devices</c>.</summary>
    public static string ForgetScript(string device) => Prelude + $$"""

        [Windows.Devices.Enumeration.DeviceUnpairingResult, Windows.Devices.Enumeration, ContentType = WindowsRuntime] | Out-Null
        $wanted = '{{PowerShellHelper.QuoteLiteral(device)}}'
        $hex = ($wanted -replace '[:-]', '').ToUpperInvariant()
        foreach ($item in @(Paired)) {
          if ($item.Info.Id -eq $wanted -or $item.Address -eq $hex) {
            $name = $item.Info.Name
            $result = Await ($item.Info.Pairing.UnpairAsync()) ([Windows.Devices.Enumeration.DeviceUnpairingResult])
            "RESULT $($result.Status) $name"
            return
          }
        }
        'NOT_FOUND'
        """;

    /// <summary>Адрес Bluetooth (12 шестнадцатеричных, двоеточия или дефисы по желанию) или id устройства.</summary>
    public static bool IsDevice(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl) &&
        (Address().IsMatch(value.Trim()) || value.Contains('#') || value.Contains('-'));

    [GeneratedRegex("^([0-9A-Fa-f]{2}[:-]?){5}[0-9A-Fa-f]{2}$")]
    private static partial Regex Address();
}
