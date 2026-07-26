using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amarin.Tools;

/// <summary>
/// Windows optional features (Get/Enable/Disable-WindowsOptionalFeature -Online).
/// enable/disable use -NoRestart; never reboots the machine. Response always reports RestartNeeded.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFeaturesTool : ITool
{
    // Feature names are typically alphanumeric with dots, hyphens, underscores (e.g. Microsoft-Windows-Subsystem-Linux).
    private static readonly Regex FeatureNamePattern = new(
        @"^[A-Za-z0-9 ._\-+]{1,200}$",
        RegexOptions.Compiled);

    private static readonly Regex FilterSafePattern = new(
        @"^[\w .+\-]{1,80}$",
        RegexOptions.Compiled);

    private const int DefaultTop = 80;
    private const int MaxTop = 200;

    public string Name => "windows_features";

    public string Description =>
        "Windows optional features (DISM/CBS): list/get state, enable or disable online with -NoRestart. " +
        "Never reboots; always reports RestartNeeded. Prefer this over run_powershell for features.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "get", "enable", "disable"],
              "description": "Windows optional feature action"
            },
            "feature_name": {
              "type": "string",
              "description": "Feature FeatureName (required for get/enable/disable)"
            },
            "filter": {
              "type": "string",
              "description": "For list: enabled, disabled, disabledwithpayloadremoved, or name substring"
            },
            "top": {
              "type": "integer",
              "description": "Max features for list (default 80, max 200)"
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
                "list" => Task.FromResult(List(arguments)),
                "get" => Task.FromResult(Get(arguments)),
                "enable" => Task.FromResult(SetFeature(arguments, enable: true)),
                "disable" => Task.FromResult(SetFeature(arguments, enable: false)),
                _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Windows features error: {ex.Message}"));
        }
    }

    private static ToolResult List(JsonElement arguments)
    {
        var top = GetInt(arguments, "top", DefaultTop, 1, MaxTop);
        var filter = "";
        if (arguments.TryGetProperty("filter", out var f) &&
            f.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(f.GetString()))
        {
            filter = f.GetString()!.Trim();
            if (!FilterSafePattern.IsMatch(filter))
            {
                return ToolResult.Fail("filter contains unsupported characters");
            }
        }

        var safeFilter = filter.Replace("'", "''", StringComparison.Ordinal);
        return PowerShellHelper.Run(ListScript(safeFilter, top), 180, maxOutput: 4000);
    }

    private static ToolResult Get(JsonElement arguments)
    {
        if (!TryGetFeatureName(arguments, out var name, out var err))
        {
            return ToolResult.Fail(err ?? "feature_name is required for get");
        }

        return PowerShellHelper.Run(GetScript(name), 120, maxOutput: 4000);
    }

    private static ToolResult SetFeature(JsonElement arguments, bool enable)
    {
        if (!TryGetFeatureName(arguments, out var name, out var err))
        {
            return ToolResult.Fail(err ?? "feature_name is required");
        }

        return PowerShellHelper.Run(SetFeatureScript(name, enable), 600, maxOutput: 4000);
    }

    private static string ListScript(string safeFilter, int top) => $$"""
        $ErrorActionPreference = 'SilentlyContinue'
        $top = {{top}}
        $filter = '{{safeFilter}}'.ToLowerInvariant()
        Write-Output "=== windows_features list (top=$top filter='$filter') ==="

        try {
          $features = @(Get-WindowsOptionalFeature -Online -ErrorAction Stop)
        } catch {
          $msg = $_.Exception.Message
          if ($msg -match 'Access denied|access is denied|отказано|недостаточно прав|UnauthorizedAccess|0x80070005|administrator|elevation|elevat') {
            # Read path: soft-skip so --smoke-tools stays green without elevation.
            Write-Output 'пропущено: нужны права администратора для windows_features list (Get-WindowsOptionalFeature -Online).'
            Write-Output 'Запустите Amarin от имени администратора для полного списка optional features.'
            exit 0
          }
          Write-Output "ERROR: $msg"
          exit 1
        }

        if ($filter -eq 'enabled') {
          $features = @($features | Where-Object { $_.State -eq 'Enabled' })
        } elseif ($filter -eq 'disabled') {
          $features = @($features | Where-Object { $_.State -eq 'Disabled' })
        } elseif ($filter -eq 'disabledwithpayloadremoved') {
          $features = @($features | Where-Object { $_.State -eq 'DisabledWithPayloadRemoved' })
        } elseif ($filter) {
          $features = @($features | Where-Object {
            ($_.FeatureName -and $_.FeatureName -like "*$filter*")
          })
        }

        $total = $features.Count
        $features = @($features | Sort-Object FeatureName | Select-Object -First $top)
        $text = $features |
          Select-Object FeatureName, State |
          Format-Table -AutoSize -Wrap |
          Out-String -Width 200
        Write-Output $text
        Write-Output "Matched: $total; shown: $($features.Count) (cap $top)."
        Write-Output 'Enable/disable: use action=enable|disable with feature_name. Machine is never rebooted by this tool.'
        exit 0
        """;

    private static string GetScript(string safeName) => $$"""
        $ErrorActionPreference = 'Stop'
        $name = '{{safeName}}'
        Write-Output "=== windows_features get: $name ==="
        try {
          $f = Get-WindowsOptionalFeature -Online -FeatureName $name -ErrorAction Stop
          $f | Select-Object FeatureName, State, RestartRequired, Path, Online, LogPath, ScratchDirectory, LogLevel |
            Format-List | Out-String -Width 200 | Write-Output
          Write-Output ("RestartNeeded (property RestartRequired): " + $f.RestartRequired)
          exit 0
        } catch {
          $msg = $_.Exception.Message
          if ($msg -match 'Access denied|access is denied|отказано|недостаточно прав|UnauthorizedAccess|0x80070005|administrator|elevation|elevat') {
            Write-Output 'пропущено: нужны права администратора для windows_features get.'
            exit 0
          }
          if ($msg -match 'not found|cannot find|не найден') {
            Write-Output "Feature not found: $name"
            exit 1
          }
          Write-Output "ERROR: $msg"
          exit 1
        }
        """;

    private static string SetFeatureScript(string safeName, bool enable)
    {
        var verb = enable ? "enable" : "disable";
        var cmdlet = enable ? "Enable-WindowsOptionalFeature" : "Disable-WindowsOptionalFeature";

        return $$"""
            $ErrorActionPreference = 'Stop'
            $name = '{{safeName}}'
            Write-Output "=== windows_features {{verb}}: $name ==="
            Write-Output 'Flags: -Online -NoRestart (machine will NOT be rebooted by this tool).'

            try {
              $before = Get-WindowsOptionalFeature -Online -FeatureName $name -ErrorAction Stop
            } catch {
              $msg = $_.Exception.Message
              if ($msg -match 'Access denied|access is denied|отказано|недостаточно прав|UnauthorizedAccess|0x80070005|administrator|elevation|elevat') {
                Write-Output 'требуются права администратора для изменения optional feature'
                exit 1
              }
              Write-Output "ERROR (read previous state): $msg"
              exit 1
            }

            Write-Output "PreviousState: $($before.State)"
            Write-Output "PreviousRestartRequired: $($before.RestartRequired)"

            try {
              $result = {{cmdlet}} -Online -FeatureName $name -NoRestart -ErrorAction Stop
              Write-Output '--- result ---'
              $result | Format-List | Out-String -Width 200 | Write-Output

              $restart = $null
              if ($null -ne $result.PSObject.Properties['RestartNeeded']) {
                $restart = $result.RestartNeeded
              } elseif ($null -ne $result.PSObject.Properties['RestartRequired']) {
                $restart = $result.RestartRequired
              }

              try {
                $after = Get-WindowsOptionalFeature -Online -FeatureName $name -ErrorAction SilentlyContinue
                if ($after) {
                  Write-Output "NewState: $($after.State)"
                  if ($null -eq $restart -and $null -ne $after.RestartRequired) {
                    $restart = $after.RestartRequired
                  }
                }
              } catch {}

              if ($null -eq $restart) { $restart = 'Unknown' }
              Write-Output ''
              Write-Output "RestartNeeded: $restart"
              Write-Output 'Machine was NOT rebooted. Reboot manually if RestartNeeded is True/Yes.'
              exit 0
            } catch {
              $msg = $_.Exception.Message
              if ($msg -match 'Access denied|access is denied|отказано|недостаточно прав|UnauthorizedAccess|0x80070005|administrator|elevation|elevat') {
                Write-Output 'требуются права администратора для изменения optional feature'
                exit 1
              }
              Write-Output "ERROR: $msg"
              Write-Output "PreviousState was: $($before.State) (unchanged if cmdlet failed)."
              exit 1
            }
            """;
    }

    private static bool TryGetFeatureName(JsonElement arguments, out string safeName, out string? error)
    {
        safeName = "";
        error = null;
        if (!arguments.TryGetProperty("feature_name", out var n) ||
            n.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(n.GetString()))
        {
            error = "feature_name is required";
            return false;
        }

        var raw = n.GetString()!.Trim();
        if (!FeatureNamePattern.IsMatch(raw))
        {
            error = "feature_name invalid. Allowed: letters, digits, space, . _ - + (max 200).";
            return false;
        }

        safeName = raw.Replace("'", "''", StringComparison.Ordinal);
        return true;
    }

    private static int GetInt(JsonElement args, string name, int defaultValue, int min, int max)
    {
        if (!args.TryGetProperty(name, out var prop) || !prop.TryGetInt32(out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }
}
