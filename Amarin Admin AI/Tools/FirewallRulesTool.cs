using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amarin.Tools;

/// <summary>
/// Firewall rules: list/get (read) and enable/disable/create/delete (write + confirm + undo snapshot).
/// Rules created by this tool get Description "Created by Amarin Admin AI".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FirewallRulesTool : ITool
{
    private static readonly Regex LocalPortPattern = new(@"^[0-9,\-]+$", RegexOptions.Compiled);
    private static readonly Regex NameSafePattern = new(
        @"^[\w .+\-@#()\[\]/\\,:]{1,128}$",
        RegexOptions.Compiled);
    private static readonly Regex FilterSafePattern = new(
        @"^[\w .+\-@#()\[\]/\\,:]{1,80}$",
        RegexOptions.Compiled);
    private static readonly Regex ProgramPathPattern = new(
        @"^[A-Za-z]:\\(?:[^<>:""|?*\x00-\x1F]+\\)*[^<>:""|?*\x00-\x1F]*$",
        RegexOptions.Compiled);

    private const string AmarinDescription = "Created by Amarin Admin AI";
    private const int DefaultTop = 40;
    private const int MaxTop = 100;

    public string Name => "firewall_rules";

    public string Description =>
        "Windows Firewall rules: list/get with filter or top-N, enable/disable/create/delete. " +
        "Prefer this for rule management; use network for overall adapter/DNS picture.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "get", "enable", "disable", "create", "delete"],
              "description": "Firewall rule action"
            },
            "name": {
              "type": "string",
              "description": "Rule DisplayName or Name (required for get/enable/disable/delete/create)"
            },
            "direction": {
              "type": "string",
              "enum": ["inbound", "outbound"],
              "description": "Direction for create"
            },
            "action_type": {
              "type": "string",
              "enum": ["allow", "block"],
              "description": "Allow or block for create"
            },
            "protocol": {
              "type": "string",
              "enum": ["tcp", "udp", "any"],
              "description": "Protocol for create (default any)"
            },
            "local_port": {
              "type": "string",
              "description": "Local port(s) for create, e.g. 80 or 80,443 or 1000-2000"
            },
            "program": {
              "type": "string",
              "description": "Optional program path for create"
            },
            "filter": {
              "type": "string",
              "description": "Filter for list: enabled, disabled, inbound, outbound, allow, block, or name substring"
            },
            "top": {
              "type": "integer",
              "description": "Max rules for list (default 40, max 100). Required implicitly when filter empty."
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
            if (action == "list")
            {
                var listResult = List(arguments);
                return Task.FromResult(listResult);
            }

            return action switch
            {
                "get" => Task.FromResult(Get(arguments)),
                "enable" => Task.FromResult(SetEnabled(arguments, enabled: true)),
                "disable" => Task.FromResult(SetEnabled(arguments, enabled: false)),
                "create" => Task.FromResult(Create(arguments)),
                "delete" => Task.FromResult(Delete(arguments)),
                _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Firewall rules error: {ex.Message}"));
        }
    }

    private static ToolResult Get(JsonElement arguments)
    {
        if (!TryGetSafeName(arguments, out var name, out var err))
        {
            return ToolResult.Fail(err ?? "name is required for get");
        }

        return PowerShellHelper.Run(GetScript(name), 90, maxOutput: 4000);
    }

    private static ToolResult SetEnabled(JsonElement arguments, bool enabled)
    {
        if (!TryGetSafeName(arguments, out var name, out var err))
        {
            return ToolResult.Fail(err ?? "name is required");
        }

        return PowerShellHelper.Run(SetEnabledScript(name, enabled), 90, maxOutput: 4000);
    }

    private static ToolResult Delete(JsonElement arguments)
    {
        if (!TryGetSafeName(arguments, out var name, out var err))
        {
            return ToolResult.Fail(err ?? "name is required for delete");
        }

        return PowerShellHelper.Run(DeleteScript(name), 90, maxOutput: 4000);
    }

    private static ToolResult Create(JsonElement arguments)
    {
        if (!TryGetSafeName(arguments, out var name, out var err))
        {
            return ToolResult.Fail(err ?? "name is required for create");
        }

        var direction = GetEnum(arguments, "direction", ["inbound", "outbound"], required: true, out var dirErr);
        if (dirErr is not null)
        {
            return ToolResult.Fail(dirErr);
        }

        var actionType = GetEnum(arguments, "action_type", ["allow", "block"], required: true, out var actErr);
        if (actErr is not null)
        {
            return ToolResult.Fail(actErr);
        }

        var protocol = GetEnum(arguments, "protocol", ["tcp", "udp", "any"], required: false, out _) ?? "any";

        string? localPort = null;
        if (arguments.TryGetProperty("local_port", out var portProp) &&
            portProp.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(portProp.GetString()))
        {
            localPort = portProp.GetString()!.Trim();
            if (!LocalPortPattern.IsMatch(localPort))
            {
                return ToolResult.Fail("local_port invalid. Allowed: digits, comma, hyphen (e.g. 80 or 80,443 or 1000-2000).");
            }
        }

        string? program = null;
        if (arguments.TryGetProperty("program", out var progProp) &&
            progProp.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(progProp.GetString()))
        {
            program = progProp.GetString()!.Trim();
            try
            {
                program = Path.GetFullPath(program);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Invalid program path: {ex.Message}");
            }

            if (!ProgramPathPattern.IsMatch(program) ||
                program.Contains('\'', StringComparison.Ordinal) ||
                program.Contains('`', StringComparison.Ordinal))
            {
                return ToolResult.Fail("program path contains unsupported characters");
            }
        }

        return PowerShellHelper.Run(
            CreateScript(name, direction!, actionType!, protocol, localPort, program),
            90,
            maxOutput: 4000);
    }

    private static ToolResult List(JsonElement arguments)
    {
        var top = GetInt(arguments, "top", DefaultTop, 1, MaxTop);
        string filter = "";
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

        // Always cap with top (default 40) — rules can number in the thousands.
        return PowerShellHelper.Run(ListScript(filter, top), 120, maxOutput: 4000);
    }

    private static string ListScript(string filter, int top)
    {
        var safeFilter = filter.Replace("'", "''", StringComparison.Ordinal);

        return $$"""
            $top = {{top}}
            $filter = '{{safeFilter}}'.ToLowerInvariant()
            Write-Output "=== firewall_rules list (top=$top filter='$filter') ==="

            $rules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue)
            if ($rules.Count -eq 0) {
              Write-Output 'No firewall rules returned (admin/module may be required).'
              exit 0
            }

            if ($filter -eq 'enabled') {
              $rules = @($rules | Where-Object { $_.Enabled -eq 'True' -or $_.Enabled -eq $true })
            } elseif ($filter -eq 'disabled') {
              $rules = @($rules | Where-Object { $_.Enabled -eq 'False' -or $_.Enabled -eq $false })
            } elseif ($filter -eq 'inbound') {
              $rules = @($rules | Where-Object { $_.Direction -eq 'Inbound' })
            } elseif ($filter -eq 'outbound') {
              $rules = @($rules | Where-Object { $_.Direction -eq 'Outbound' })
            } elseif ($filter -eq 'allow') {
              $rules = @($rules | Where-Object { $_.Action -eq 'Allow' })
            } elseif ($filter -eq 'block') {
              $rules = @($rules | Where-Object { $_.Action -eq 'Block' })
            } elseif ($filter) {
              $rules = @($rules | Where-Object {
                ($_.DisplayName -and $_.DisplayName -like "*$filter*") -or
                ($_.Name -and $_.Name -like "*$filter*") -or
                ($_.Description -and $_.Description -like "*$filter*")
              })
            }

            $total = $rules.Count
            $rules = @($rules | Select-Object -First $top)
            $rows = foreach ($r in $rules) {
              $port = $null
              $prog = $null
              try {
                $pf = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $r -ErrorAction SilentlyContinue
                if ($pf) {
                  $port = [string]$pf.LocalPort
                  if ($pf.Protocol) { $port = "$($pf.Protocol):$port" }
                }
              } catch {}
              try {
                $af = Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $r -ErrorAction SilentlyContinue
                if ($af -and $af.Program -and $af.Program -ne 'Any') { $prog = [string]$af.Program }
              } catch {}
              [PSCustomObject]@{
                Enabled     = $r.Enabled
                Direction   = $r.Direction
                Action      = $r.Action
                Profile     = $r.Profile
                DisplayName = $r.DisplayName
                Name        = $r.Name
                Port        = $port
                Program     = $prog
              }
            }

            $text = $rows | Format-Table -AutoSize -Wrap | Out-String -Width 200
            Write-Output $text
            Write-Output "Matched: $total; shown: $($rows.Count) (cap $top). Use filter or raise top (max {{MaxTop}})."
            exit 0
            """;
    }

    private static string GetScript(string safeName) => $$"""
        $q = '{{safeName}}'
        Write-Output "=== firewall_rules get: $q ==="
        $rules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object {
          $_.DisplayName -eq $q -or $_.Name -eq $q
        })
        if ($rules.Count -eq 0) {
          # partial match as fallback
          $rules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object {
            ($_.DisplayName -and $_.DisplayName -like "*$q*") -or ($_.Name -and $_.Name -like "*$q*")
          } | Select-Object -First 10)
        }
        if ($rules.Count -eq 0) {
          Write-Output "Rule not found: $q"
          exit 1
        }
        foreach ($r in $rules) {
          Write-Output "--- $($r.DisplayName) [$($r.Name)] ---"
          $r | Select-Object Name, DisplayName, Description, Enabled, Direction, Action, Profile, PolicyStoreSourceType |
            Format-List | Out-String -Width 200 | Write-Output
          try {
            Get-NetFirewallPortFilter -AssociatedNetFirewallRule $r -ErrorAction SilentlyContinue |
              Select-Object Protocol, LocalPort, RemotePort, IcmpType |
              Format-List | Out-String -Width 200 | Write-Output
          } catch {}
          try {
            Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $r -ErrorAction SilentlyContinue |
              Select-Object Program, Package |
              Format-List | Out-String -Width 200 | Write-Output
          } catch {}
        }
        exit 0
        """;

    private static string SetEnabledScript(string safeName, bool enabled)
    {
        var verb = enabled ? "enable" : "disable";
        var wantLiteral = enabled ? "$true" : "$false";
        return $$"""
            $q = '{{safeName}}'
            $want = {{wantLiteral}}
            Write-Output "=== firewall_rules {{verb}}: $q ==="
            $rules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object {
              $_.DisplayName -eq $q -or $_.Name -eq $q
            })
            if ($rules.Count -eq 0) {
              Write-Output "Rule not found: $q"
              exit 1
            }
            foreach ($r in $rules) {
              $prev = $r.Enabled
              Write-Output "Rule: $($r.DisplayName) [$($r.Name)] previous Enabled=$prev"
              try {
                if ($want) {
                  Enable-NetFirewallRule -Name $r.Name -ErrorAction Stop
                } else {
                  Disable-NetFirewallRule -Name $r.Name -ErrorAction Stop
                }
                $now = (Get-NetFirewallRule -Name $r.Name -ErrorAction SilentlyContinue).Enabled
                Write-Output "  new Enabled=$now"
              } catch {
                $msg = $_.Exception.Message
                if ($msg -match 'Access denied|access is denied|отказано|недостаточно прав|UnauthorizedAccess|0x80070005|administrator') {
                  Write-Output 'требуются права администратора для изменения правила брандмауэра'
                  exit 1
                }
                Write-Output "ERROR: $msg"
                exit 1
              }
            }
            exit 0
            """;
    }

    private static string DeleteScript(string safeName) => $$"""
        $q = '{{safeName}}'
        Write-Output "=== firewall_rules delete: $q ==="
        $rules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object {
          $_.DisplayName -eq $q -or $_.Name -eq $q
        })
        if ($rules.Count -eq 0) {
          Write-Output "Rule not found: $q"
          exit 1
        }
        foreach ($r in $rules) {
          # Save definition for logs / human undo
          $port = $null; $prog = $null; $proto = $null
          try {
            $pf = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $r -ErrorAction SilentlyContinue
            if ($pf) { $port = [string]$pf.LocalPort; $proto = [string]$pf.Protocol }
          } catch {}
          try {
            $af = Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $r -ErrorAction SilentlyContinue
            if ($af) { $prog = [string]$af.Program }
          } catch {}
          Write-Output "Deleting: DisplayName=$($r.DisplayName); Name=$($r.Name); Enabled=$($r.Enabled); Direction=$($r.Direction); Action=$($r.Action); Protocol=$proto; LocalPort=$port; Program=$prog; Description=$($r.Description)"
          try {
            Remove-NetFirewallRule -Name $r.Name -ErrorAction Stop
            Write-Output '  removed.'
          } catch {
            $msg = $_.Exception.Message
            if ($msg -match 'Access denied|access is denied|отказано|недостаточно прав|UnauthorizedAccess|0x80070005|administrator') {
              Write-Output 'требуются права администратора для удаления правила брандмауэра'
              exit 1
            }
            Write-Output "ERROR: $msg"
            exit 1
          }
        }
        exit 0
        """;

    private static string CreateScript(
        string safeName,
        string direction,
        string actionType,
        string protocol,
        string? localPort,
        string? program)
    {
        // All values validated against enums / patterns in C# before interpolation.
        var dirPs = direction.Equals("inbound", StringComparison.OrdinalIgnoreCase) ? "Inbound" : "Outbound";
        var actPs = actionType.Equals("block", StringComparison.OrdinalIgnoreCase) ? "Block" : "Allow";
        var protoPs = protocol.ToLowerInvariant() switch
        {
            "tcp" => "TCP",
            "udp" => "UDP",
            _ => "Any"
        };
        var safePort = localPort?.Replace("'", "''", StringComparison.Ordinal) ?? "";
        var safeProg = program?.Replace("'", "''", StringComparison.Ordinal) ?? "";
        var safeDesc = AmarinDescription.Replace("'", "''", StringComparison.Ordinal);

        return $$"""
            $displayName = '{{safeName}}'
            Write-Output "=== firewall_rules create: $displayName ==="
            try {
              $existing = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq $displayName })
              if ($existing.Count -gt 0) {
                Write-Output "Rule with this DisplayName already exists: $displayName"
                exit 1
              }

              $params = @{
                DisplayName = $displayName
                Name        = ('Amarin_' + [guid]::NewGuid().ToString('N').Substring(0, 12))
                Direction   = '{{dirPs}}'
                Action      = '{{actPs}}'
                Protocol    = '{{protoPs}}'
                Description = '{{safeDesc}}'
                Enabled     = 'True'
                ErrorAction = 'Stop'
              }
              $port = '{{safePort}}'
              if ($port) { $params['LocalPort'] = $port }
              $prog = '{{safeProg}}'
              if ($prog) { $params['Program'] = $prog }

              $rule = New-NetFirewallRule @params
              Write-Output "Created: DisplayName=$($rule.DisplayName); Name=$($rule.Name); Direction=$($rule.Direction); Action=$($rule.Action); Protocol={{protoPs}}; LocalPort=$port; Program=$prog"
              Write-Output "Description=$($rule.Description)"
              Write-Output 'Undo: delete this rule by Name/DisplayName (the session snapshot may not restore firewall rules).'
              exit 0
            } catch {
              $msg = $_.Exception.Message
              if ($msg -match 'Access denied|access is denied|отказано|недостаточно прав|UnauthorizedAccess|0x80070005|administrator') {
                Write-Output 'требуются права администратора для создания правила брандмауэра'
                exit 1
              }
              Write-Output "ERROR: $msg"
              exit 1
            }
            """;
    }

    private static bool TryGetSafeName(JsonElement arguments, out string safeName, out string? error)
    {
        safeName = "";
        error = null;
        if (!arguments.TryGetProperty("name", out var n) ||
            n.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(n.GetString()))
        {
            error = "name is required";
            return false;
        }

        var raw = n.GetString()!.Trim();
        if (!NameSafePattern.IsMatch(raw))
        {
            error = "name contains unsupported characters (max 128; letters/digits and ._+-@#()[]/\\,: )";
            return false;
        }

        safeName = raw.Replace("'", "''", StringComparison.Ordinal);
        return true;
    }

    private static string? GetEnum(
        JsonElement arguments,
        string field,
        string[] allowed,
        bool required,
        out string? error)
    {
        error = null;
        if (!arguments.TryGetProperty(field, out var p) || p.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(p.GetString()))
        {
            if (required)
            {
                error = $"{field} is required ({string.Join('|', allowed)})";
            }

            return null;
        }

        var v = p.GetString()!.Trim().ToLowerInvariant();
        if (!allowed.Contains(v))
        {
            error = $"{field} must be one of: {string.Join(", ", allowed)}";
            return null;
        }

        return v;
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
