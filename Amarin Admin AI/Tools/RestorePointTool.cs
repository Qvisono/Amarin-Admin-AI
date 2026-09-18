using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

/// <summary>
/// System Restore points: list, status, create. Rollback is manual via rstrui.exe.
/// Progress CLIXML is suppressed via PowerShellHelper.ScriptPreamble.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RestorePointTool : ITool
{
    public string Name => "restore_point";

    public string Description =>
        "Windows System Restore points: list existing points, check if restore is enabled and storage usage, " +
        "create a checkpoint (max 1 per 24h). Does NOT restore to a point - use rstrui.exe for rollback.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "create", "status"],
              "description": "Restore point operation"
            },
            "description": {
              "type": "string",
              "description": "Description for create (default: Amarin checkpoint)"
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
            return action switch
            {
                "list" => Task.FromResult(PowerShellHelper.Run(ListScript(), 90, maxOutput: 4000)),
                "status" => Task.FromResult(PowerShellHelper.Run(StatusScript(), 90, maxOutput: 4000)),
                "create" => Task.FromResult(Create(arguments)),
                _ => Task.FromResult(ToolResult.Fail(
                    $"Unknown action: {action}. Supported: list, create, status. " +
                    "Откат к точке не реализован - запустите rstrui.exe (Панель управления → Восстановление)."))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Restore point error: {ex.Message}"));
        }
    }

    private static ToolResult Create(JsonElement arguments)
    {
        var description = "Amarin checkpoint";
        if (arguments.TryGetProperty("description", out var descProp) &&
            descProp.ValueKind == JsonValueKind.String)
        {
            var raw = descProp.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                description = raw.Trim();
                if (description.Length > 200)
                {
                    description = description[..200];
                }
            }
        }

        // Only validated/escaped description enters the script — never free model command text.
        var safeDescription = description.Replace("'", "''", StringComparison.Ordinal);
        return PowerShellHelper.Run(CreateScript(safeDescription), 180, maxOutput: 4000);
    }

    private static string ListScript() => """
        $ErrorActionPreference = 'Continue'
        $Error.Clear()
        $points = @(Get-ComputerRestorePoint -ErrorAction SilentlyContinue)
        $denied = $false
        foreach ($e in $Error) {
          $m = [string]$e
          if ($m -match 'Access denied|access is denied|отказано в доступе|недостаточно прав|UnauthorizedAccess|0x80070005') {
            $denied = $true
            break
          }
        }

        if ($denied -and $points.Count -eq 0) {
          Write-Output 'требуются права администратора для list точек восстановления'
          exit 1
        }

        if ($points.Count -eq 0) {
          Write-Output 'Точек восстановления не найдено.'
          Write-Output 'Откат к точке: запустите rstrui.exe (не через этот инструмент).'
          exit 0
        }

        $points |
          Select-Object SequenceNumber,
            @{n='CreationTime';e={
              if ($_.CreationTime) {
                try { [System.Management.ManagementDateTimeConverter]::ToDateTime($_.CreationTime) }
                catch { $_.CreationTime }
              } else { $null }
            }},
            Description, RestorePointType, EventType |
          Format-Table -AutoSize -Wrap | Out-String -Width 200
        Write-Output "Всего: $($points.Count)"
        Write-Output ''
        Write-Output 'Откат к точке: запустите rstrui.exe (не через этот инструмент).'
        exit 0
        """;

    private static string StatusScript() => """
        $ErrorActionPreference = 'Continue'
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

        $disable = $null
        try {
          $disable = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' -ErrorAction SilentlyContinue).DisableSR
        } catch {}
        $enabled = ($null -eq $disable) -or ([int]$disable -eq 0)
        Write-Output "SystemRestoreEnabled: $enabled"
        if (-not $enabled) {
          Write-Output 'Примечание: System Restore отключён (DisableSR=1).'
        }
        Write-Output "IsAdministrator: $isAdmin"

        $Error.Clear()
        $points = @(Get-ComputerRestorePoint -ErrorAction SilentlyContinue)
        $pointsDenied = $false
        foreach ($e in $Error) {
          $m = [string]$e
          if ($m -match 'Access denied|access is denied|отказано в доступе|недостаточно прав|UnauthorizedAccess|0x80070005') {
            $pointsDenied = $true
            break
          }
        }
        if ($pointsDenied -and $points.Count -eq 0) {
          Write-Output 'RestorePointsCount: (недоступно без прав администратора)'
        } else {
          Write-Output "RestorePointsCount: $($points.Count)"
          if ($points.Count -gt 0) {
            $last = $points | Sort-Object SequenceNumber -Descending | Select-Object -First 1
            $created = $null
            if ($last.CreationTime) {
              try { $created = [System.Management.ManagementDateTimeConverter]::ToDateTime($last.CreationTime) }
              catch { $created = $last.CreationTime }
            }
            Write-Output "LastPoint: Seq=$($last.SequenceNumber); Time=$created; Desc=$($last.Description)"
          }
        }

        Write-Output ''
        Write-Output '=== Shadow storage (место под точки) ==='
        if (-not $isAdmin) {
          Write-Output 'Подсказка: сведения о теневом хранилище (Win32_ShadowStorage / vssadmin) требуют прав администратора - не ошибка.'
        }

        $gotStorage = $false
        try {
          $storage = @(Get-CimInstance -ClassName Win32_ShadowStorage -ErrorAction SilentlyContinue)
          if ($storage.Count -gt 0) {
            $gotStorage = $true
            $storage | ForEach-Object {
              $used = if ($_.UsedSpace) { [math]::Round($_.UsedSpace / 1GB, 2) } else { '?' }
              $alloc = if ($_.AllocatedSpace) { [math]::Round($_.AllocatedSpace / 1GB, 2) } else { '?' }
              $max = if ($_.MaxSpace) { [math]::Round($_.MaxSpace / 1GB, 2) } else { '?' }
              Write-Output "Volume=$($_.Volume); UsedGB=$used; AllocatedGB=$alloc; MaxGB=$max"
            }
          }
        } catch {
          Write-Output "Win32_ShadowStorage: $($_.Exception.Message)"
        }

        if (-not $gotStorage) {
          if ($isAdmin) {
            try {
              $vss = & vssadmin list shadowstorage 2>&1 | Out-String
              if ($vss) { Write-Output $vss.Trim() }
              else { Write-Output 'Теневое хранилище: записей нет (VSS не настроен).' }
            } catch {
              Write-Output "vssadmin: $($_.Exception.Message)"
            }
          } else {
            Write-Output 'Теневое хранилище: пропуск детального опроса без прав администратора.'
          }
        }

        Write-Output ''
        Write-Output 'Откат к точке: rstrui.exe (не реализован в restore_point).'
        exit 0
        """;

    private static string CreateScript(string safeDescription) => $$$"""
        $ErrorActionPreference = 'Stop'
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
          Write-Output 'требуются права администратора для создания точки восстановления'
          exit 1
        }

        $disable = $null
        try {
          $disable = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' -ErrorAction SilentlyContinue).DisableSR
        } catch {}
        if ($null -ne $disable -and [int]$disable -ne 0) {
          Write-Output 'System Restore отключён (DisableSR). Включите восстановление системы и повторите create.'
          exit 1
        }

        # Friendly 24h limit: Windows often allows only one automatic-style point per day.
        try {
          $recent = @(Get-ComputerRestorePoint -ErrorAction SilentlyContinue | ForEach-Object {
            $t = $null
            if ($_.CreationTime) {
              try { $t = [System.Management.ManagementDateTimeConverter]::ToDateTime($_.CreationTime) } catch {}
            }
            if ($t -and $t -gt (Get-Date).AddHours(-24)) { $_ }
          })
          if ($recent.Count -gt 0) {
            $r = $recent | Sort-Object SequenceNumber -Descending | Select-Object -First 1
            $t = $null
            try { $t = [System.Management.ManagementDateTimeConverter]::ToDateTime($r.CreationTime) } catch { $t = $r.CreationTime }
            Write-Output "За последние 24 часа уже есть точка восстановления (Seq=$($r.SequenceNumber), $t, «$($r.Description)»). Лимит Windows - не ошибка. Новую точку сейчас создавать не нужно; используйте action=list."
            exit 0
          }
        } catch {}

        try {
          Checkpoint-Computer -Description '{{{safeDescription}}}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop
          Write-Output "Точка восстановления создана: «{{{safeDescription}}}» (MODIFY_SETTINGS)."
          try {
            $last = Get-ComputerRestorePoint -ErrorAction SilentlyContinue | Sort-Object SequenceNumber -Descending | Select-Object -First 1
            if ($last) {
              Write-Output "SequenceNumber: $($last.SequenceNumber)"
            }
          } catch {}
          exit 0
        } catch {
          $msg = $_.Exception.Message
          if ($msg -match '24|already been created|уже создан|частот|frequency|0x80042316|2147754998|too frequent|слишком') {
            Write-Output "За последние 24 часа уже создана точка восстановления. Это ограничение Windows, не ошибка. Используйте action=list для просмотра."
            exit 0
          }
          if ($msg -match 'Access denied|access is denied|отказано в доступе|недостаточно прав|UnauthorizedAccess|0x80070005|administrator') {
            Write-Output 'требуются права администратора для создания точки восстановления'
            exit 1
          }
          Write-Output "ERROR: $msg"
          exit 1
        }
        """;
}
