using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amarin.Tools;

/// <summary>
/// Disk/volume diagnostics: physical disks, volumes, SMART, chkdsk scan/fix, BitLocker status (no keys).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiskManagementTool : ITool
{
    private static readonly Regex DriveLetterPattern = new(@"^[A-Za-z]$", RegexOptions.Compiled);

    public string Name => "disk_management";

    public string Description =>
        "Disk and volume diagnostics: list disks/volumes, SMART reliability, chkdsk scan/fix, " +
        "BitLocker status (no recovery keys). Prefer this over wmi_query/run_powershell for disk health.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": [
                "list_disks", "list_volumes", "smart_status",
                "chkdsk_scan", "chkdsk_fix", "bitlocker_status"
              ],
              "description": "Disk management action"
            },
            "drive_letter": {
              "type": "string",
              "description": "Drive letter without colon, e.g. C (required for chkdsk_*, optional for bitlocker_status)"
            }
          },
          "required": ["action"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!arguments.TryGetProperty("action", out var actionProp))
            {
                return Task.FromResult(ToolResult.Fail("Missing required parameter: action"));
            }

            var action = actionProp.GetString()?.ToLowerInvariant();
            var drive = TryGetDriveLetter(arguments, out var driveError);
            if (driveError is not null)
            {
                return Task.FromResult(ToolResult.Fail(driveError));
            }

            if ((action is "chkdsk_scan" or "chkdsk_fix") && drive is null)
            {
                return Task.FromResult(ToolResult.Fail(
                    "drive_letter is required for chkdsk_scan/chkdsk_fix (e.g. \"C\")"));
            }

            return action switch
            {
                "list_disks" => Task.FromResult(PowerShellHelper.Run(ListDisksScript(), 120, maxOutput: 4000)),
                "list_volumes" => Task.FromResult(PowerShellHelper.Run(ListVolumesScript(), 120, maxOutput: 4000)),
                "smart_status" => Task.FromResult(PowerShellHelper.Run(SmartStatusScript(), 180, maxOutput: 4000)),
                "chkdsk_scan" => Task.FromResult(PowerShellHelper.Run(ChkdskScanScript(drive!), 600, maxOutput: 4000)),
                "chkdsk_fix" => Task.FromResult(PowerShellHelper.Run(ChkdskFixScript(drive!), 600, maxOutput: 4000)),
                "bitlocker_status" => Task.FromResult(PowerShellHelper.Run(BitLockerStatusScript(drive), 120, maxOutput: 4000)),
                _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Disk management error: {ex.Message}"));
        }
    }

    /// <summary>Returns validated single letter or null. driveError set when present but invalid.</summary>
    private static string? TryGetDriveLetter(JsonElement arguments, out string? driveError)
    {
        driveError = null;
        if (!arguments.TryGetProperty("drive_letter", out var prop) ||
            prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var raw = (prop.GetString() ?? string.Empty).Trim().TrimEnd(':').TrimEnd('\\');
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (raw.Length == 2 && raw[1] == ':')
        {
            raw = raw[..1];
        }

        if (!DriveLetterPattern.IsMatch(raw))
        {
            driveError = "drive_letter must be a single letter, e.g. \"C\"";
            return null;
        }

        return char.ToUpperInvariant(raw[0]).ToString();
    }

    private static string ListDisksScript() => """
        Write-Output '=== Physical disks (Get-PhysicalDisk) ==='
        try {
          Get-PhysicalDisk -ErrorAction SilentlyContinue |
            Select-Object DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus, OperationalStatus, SerialNumber |
            Format-Table -AutoSize -Wrap | Out-String -Width 200
        } catch {
          Write-Output "Get-PhysicalDisk: $($_.Exception.Message)"
        }

        Write-Output '=== Disks (Get-Disk) ==='
        try {
          Get-Disk -ErrorAction SilentlyContinue |
            Select-Object Number, FriendlyName, SerialNumber, PartitionStyle, ProvisioningType, OperationalStatus, HealthStatus, Size, AllocatedSize, IsBoot, IsSystem |
            Format-Table -AutoSize -Wrap | Out-String -Width 200
        } catch {
          Write-Output "Get-Disk: $($_.Exception.Message)"
        }
        exit 0
        """;

    private static string ListVolumesScript() => """
        Write-Output '=== Volumes (Get-Volume) ==='
        try {
          Get-Volume -ErrorAction SilentlyContinue |
            Select-Object DriveLetter, FileSystemLabel, FileSystem, DriveType, HealthStatus, OperationalStatus,
              @{n='SizeGB';e={ if ($_.Size) { [math]::Round($_.Size/1GB, 2) } else { $null } }},
              @{n='FreeGB';e={ if ($_.SizeRemaining) { [math]::Round($_.SizeRemaining/1GB, 2) } else { $null } }},
              @{n='UsedPct';e={
                if ($_.Size -and $_.Size -gt 0) {
                  [math]::Round(100.0 * ($_.Size - $_.SizeRemaining) / $_.Size, 1)
                } else { $null }
              }} |
            Sort-Object DriveLetter |
            Format-Table -AutoSize -Wrap | Out-String -Width 200
        } catch {
          Write-Output "Get-Volume: $($_.Exception.Message)"
        }

        Write-Output '=== Partitions (Get-Partition) ==='
        try {
          Get-Partition -ErrorAction SilentlyContinue |
            Select-Object DiskNumber, PartitionNumber, DriveLetter, Type, GptType,
              @{n='SizeGB';e={ if ($_.Size) { [math]::Round($_.Size/1GB, 2) } else { $null } }},
              IsBoot, IsSystem, IsActive |
            Sort-Object DiskNumber, PartitionNumber |
            Format-Table -AutoSize -Wrap | Out-String -Width 200
        } catch {
          Write-Output "Get-Partition: $($_.Exception.Message)"
        }
        exit 0
        """;

    private static string SmartStatusScript() => """
        Write-Output '=== SMART / storage reliability ==='
        $disks = @(Get-PhysicalDisk -ErrorAction SilentlyContinue)
        if ($disks.Count -eq 0) {
          Write-Output 'Physical disks not found (Storage module / admin may be required).'
          exit 0
        }

        foreach ($d in $disks) {
          Write-Output "--- Disk DeviceId=$($d.DeviceId) $($d.FriendlyName) Health=$($d.HealthStatus) ---"
          try {
            $c = $d | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
            if ($null -eq $c) {
              Write-Output '  Reliability counters unavailable for this disk.'
              continue
            }
            $c | Select-Object DeviceId, Temperature, TemperatureMax, Wear, ReadErrorsTotal, ReadErrorsCorrected,
              ReadErrorsUncorrected, WriteErrorsTotal, WriteErrorsCorrected, WriteErrorsUncorrected,
              ManufactureDate, StartStopCycleCount, PowerOnHours, LoadUnloadCycleCount |
              Format-List | Out-String -Width 200
          } catch {
            Write-Output "  Get-StorageReliabilityCounter: $($_.Exception.Message)"
          }
        }
        exit 0
        """;

    private static string ChkdskScanScript(string driveLetter) => $$"""
        $drive = '{{driveLetter}}'
        Write-Output "=== chkdsk_scan (Repair-Volume -Scan) on ${drive}: ==="
        Write-Output 'Read-only scan; does not fix. For system volume may need free space / time.'
        try {
          $vol = Get-Volume -DriveLetter $drive -ErrorAction Stop
          Write-Output "Volume: $($vol.FileSystemLabel) FS=$($vol.FileSystem) Health=$($vol.HealthStatus)"
          $result = Repair-Volume -DriveLetter $drive -Scan -ErrorAction Stop
          Write-Output "Repair-Volume -Scan result: $result"
          if ($result -is [string] -or $result -is [enum] -or $null -ne $result) {
            Write-Output ($result | Format-List | Out-String -Width 200)
          }
          exit 0
        } catch {
          $msg = $_.Exception.Message
          if ($msg -match 'Access denied|access is denied|отказано в доступе|недостаточно прав|UnauthorizedAccess|0x80070005|administrator') {
            Write-Output "требуются права администратора для chkdsk_scan тома ${drive}:"
            exit 1
          }
          Write-Output "ERROR: $msg"
          exit 1
        }
        """;

    private static string ChkdskFixScript(string driveLetter) => $$"""
        $drive = '{{driveLetter}}'
        Write-Output "=== chkdsk_fix (Repair-Volume -OfflineScanAndFix) on ${drive}: ==="
        Write-Output 'WARNING: volume may be unavailable during fix; system volume may schedule fix on reboot.'
        try {
          $vol = Get-Volume -DriveLetter $drive -ErrorAction Stop
          Write-Output "Volume: $($vol.FileSystemLabel) FS=$($vol.FileSystem) Health=$($vol.HealthStatus)"
          $sys = $env:SystemDrive.TrimEnd(':').TrimEnd('\')
          if ($drive -eq $sys) {
            Write-Output 'This is the system volume - offline fix may require reboot (not performed automatically).'
          }
          $result = Repair-Volume -DriveLetter $drive -OfflineScanAndFix -ErrorAction Stop
          Write-Output "Repair-Volume -OfflineScanAndFix result: $result"
          Write-Output ($result | Format-List | Out-String -Width 200)
          exit 0
        } catch {
          $msg = $_.Exception.Message
          if ($msg -match 'Access denied|access is denied|отказано в доступе|недостаточно прав|UnauthorizedAccess|0x80070005|administrator') {
            Write-Output "требуются права администратора для chkdsk_fix тома ${drive}:"
            exit 1
          }
          Write-Output "ERROR: $msg"
          exit 1
        }
        """;

    /// <summary>
    /// Status only — never dump RecoveryPassword / KeyProtector secrets.
    /// Note: foreach is a statement — collect into $rows, then pipe (cannot pipe foreach itself).
    /// </summary>
    private static string BitLockerStatusScript(string? driveLetter)
    {
        var filter = driveLetter ?? string.Empty;
        // Escape single quotes for PowerShell string literal only.
        var safeFilter = filter.Replace("'", "''", StringComparison.Ordinal);

        return $$"""
            Write-Output '=== BitLocker status (no recovery keys) ==='
            $filter = '{{safeFilter}}'
            try {
              $vols = @(Get-BitLockerVolume -ErrorAction Stop)
              if ($filter) {
                $vols = @($vols | Where-Object { $_.MountPoint -match ('^' + [regex]::Escape($filter) + ':') })
              }
              if ($vols.Count -eq 0) {
                Write-Output 'BitLocker volumes not found (or module/admin unavailable).'
                exit 0
              }

              $rows = foreach ($v in $vols) {
                $protTypes = @()
                try {
                  foreach ($p in @($v.KeyProtector)) {
                    if ($null -ne $p -and $p.KeyProtectorType) {
                      $protTypes += [string]$p.KeyProtectorType
                    }
                  }
                } catch { }

                [PSCustomObject]@{
                  MountPoint            = $v.MountPoint
                  VolumeStatus          = $v.VolumeStatus
                  ProtectionStatus      = $v.ProtectionStatus
                  EncryptionPercentage  = $v.EncryptionPercentage
                  EncryptionMethod      = $v.EncryptionMethod
                  LockStatus            = $v.LockStatus
                  AutoUnlockEnabled     = $v.AutoUnlockEnabled
                  MetadataVersion       = $v.MetadataVersion
                  CapacityGB            = if ($v.CapacityGB) { [math]::Round([double]$v.CapacityGB, 2) } else { $null }
                  KeyProtectorTypes     = ($protTypes -join ', ')
                }
              }

              $text = $rows | Format-List | Out-String -Width 200
              Write-Output $text
              Write-Output 'Recovery keys and passwords are intentionally not shown.'
              exit 0
            } catch {
              $msg = $_.Exception.Message
              if ($msg -match 'Access denied|access is denied|отказано в доступе|недостаточно прав|UnauthorizedAccess|0x80070005|administrator') {
                # Read-only status: report need for elevation without ERROR (agent should not fall back to run_powershell).
                Write-Output 'требуются права администратора для bitlocker_status'
                Write-Output 'Запустите Amarin от имени администратора, чтобы увидеть статус томов. Ключи восстановления не выводятся.'
                exit 0
              }
              if ($msg -match 'not recognized|не распознан|Get-BitLockerVolume|CommandNotFound') {
                Write-Output 'Get-BitLockerVolume недоступен (модуль BitLocker / OS edition).'
                exit 0
              }
              Write-Output "ERROR: $msg"
              exit 1
            }
            """;
    }
}
