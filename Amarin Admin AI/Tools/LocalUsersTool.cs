using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Amarin.Tools;

/// <summary>
/// Local users and groups: list/details (read) and enable/disable/membership (write + confirm + undo snapshot).
/// Hard safety blocks (current user SID, last Administrators member via S-1-5-32-544) run before confirm.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LocalUsersTool : ITool
{
    public string Name => "local_users";

    public string Description =>
        "Local users and groups: list users/groups, members, user details; enable/disable user; add/remove group membership. " +
        "Never creates users or sets passwords. Highlights Administrators (SID S-1-5-32-544) and enabled built-in accounts.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": [
                "list_users", "list_groups", "group_members", "user_details",
                "enable_user", "disable_user", "add_to_group", "remove_from_group"
              ],
              "description": "Local users/groups action"
            },
            "user": {
              "type": "string",
              "description": "User name (Unicode OK: Гость, etc.)"
            },
            "group": {
              "type": "string",
              "description": "Group name (Unicode OK: Администраторы, Пользователи)"
            }
          },
          "required": ["action"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            // Hard blocks again at execute (belt + suspenders if confirm path skipped).
            if (LocalUsersSafety.TryGetHardBlockReason(arguments, out var blockReason))
            {
                return Task.FromResult(ToolResult.Fail(blockReason));
            }

            if (!arguments.TryGetProperty("action", out var actionProp))
            {
                return Task.FromResult(ToolResult.Fail("Missing required parameter: action"));
            }

            var action = actionProp.GetString()?.ToLowerInvariant();
            return action switch
            {
                "list_users" => Task.FromResult(PowerShellHelper.Run(ListUsersScript(), 90, maxOutput: 4000)),
                "list_groups" => Task.FromResult(PowerShellHelper.Run(ListGroupsScript(), 90, maxOutput: 4000)),
                "group_members" => Task.FromResult(GroupMembers(arguments)),
                "user_details" => Task.FromResult(UserDetails(arguments)),
                "enable_user" => Task.FromResult(SetUserEnabled(arguments, enabled: true)),
                "disable_user" => Task.FromResult(SetUserEnabled(arguments, enabled: false)),
                "add_to_group" => Task.FromResult(ChangeGroupMembership(arguments, add: true)),
                "remove_from_group" => Task.FromResult(ChangeGroupMembership(arguments, add: false)),
                _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Local users error: {ex.Message}"));
        }
    }

    private static ToolResult GroupMembers(JsonElement arguments)
    {
        if (!TryGetName(arguments, "group", out var group, out var err))
        {
            return ToolResult.Fail(err ?? "group is required for group_members");
        }

        // Prefer net localgroup — typically works without elevation (avoids agent falling back to run_powershell).
        if (LocalUsersSafety.TryListGroupMembers(group, out var members, out var netError))
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== local_users group_members: {group} ===");
            if (LocalUsersSafety.IsBuiltinAdministratorsGroup(group))
            {
                sb.AppendLine($"Resolved as BUILTIN\\Administrators (S-1-5-32-544 → «{LocalUsersSafety.GetBuiltinAdministratorsSamName()}»)");
            }

            sb.AppendLine("Source: WMI/net localgroup (read without elevation when possible)");
            if (members.Count == 0)
            {
                sb.AppendLine("Членов нет.");
            }
            else
            {
                foreach (var m in members)
                {
                    sb.AppendLine(m);
                }

                sb.AppendLine($"Всего: {members.Count}");
            }

            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        // Fallback PowerShell (may need rights on some systems)
        var safe = LocalUsersSafety.EscapeForPowerShell(group);
        var ps = PowerShellHelper.Run(GroupMembersScript(safe), 90, maxOutput: 4000);
        if (ps.Success)
        {
            return ps;
        }

        return ToolResult.Fail(
            $"group_members failed. net: {netError ?? "—"}; powershell: {Truncate(ps.Output, 400)}");
    }

    private static ToolResult UserDetails(JsonElement arguments)
    {
        if (!TryGetName(arguments, "user", out var user, out var err))
        {
            return ToolResult.Fail(err ?? "user is required for user_details");
        }

        return PowerShellHelper.Run(
            UserDetailsScript(LocalUsersSafety.EscapeForPowerShell(user)),
            90,
            maxOutput: 4000);
    }

    private static ToolResult SetUserEnabled(JsonElement arguments, bool enabled)
    {
        if (!TryGetName(arguments, "user", out var user, out var err))
        {
            return ToolResult.Fail(err ?? "user is required");
        }

        // Hard block already handled above; keep SID check in script as last resort.
        return PowerShellHelper.Run(
            SetUserEnabledScript(LocalUsersSafety.EscapeForPowerShell(user), enabled),
            90,
            maxOutput: 4000);
    }

    private static ToolResult ChangeGroupMembership(JsonElement arguments, bool add)
    {
        if (!TryGetName(arguments, "user", out var user, out var errUser))
        {
            return ToolResult.Fail(errUser ?? "user is required");
        }

        if (!TryGetName(arguments, "group", out var group, out var errGroup))
        {
            return ToolResult.Fail(errGroup ?? "group is required");
        }

        return PowerShellHelper.Run(
            ChangeGroupScript(
                LocalUsersSafety.EscapeForPowerShell(user),
                LocalUsersSafety.EscapeForPowerShell(group),
                add),
            90,
            maxOutput: 4000);
    }

    private static bool TryGetName(JsonElement arguments, string field, out string name, out string? error)
    {
        name = string.Empty;
        error = null;
        if (!arguments.TryGetProperty(field, out var p) ||
            p.ValueKind != JsonValueKind.String)
        {
            error = $"{field} is required";
            return false;
        }

        if (!LocalUsersSafety.TryValidateAccountName(p.GetString(), out name, out error))
        {
            return false;
        }

        return true;
    }

    private static string ListUsersScript() => """
        $ErrorActionPreference = 'SilentlyContinue'
        Write-Output '=== local_users list_users ==='
        $current = $env:USERNAME
        Write-Output "CurrentSessionUser: $current"

        try {
          $users = @(Get-LocalUser -ErrorAction Stop)
        } catch {
          $msg = $_.Exception.Message
          if ($msg -match 'Access denied|elevation|administrator|0x80070005|недостаточно|отказано') {
            Write-Output 'пропущено: нужны права администратора для Get-LocalUser (или модуль Microsoft.PowerShell.LocalAccounts).'
            exit 0
          }
          Write-Output "ERROR: $msg"
          exit 1
        }

        $rows = foreach ($u in ($users | Sort-Object Name)) {
          $pwdExpires = if ($u.PasswordExpires) { $u.PasswordExpires.ToString('yyyy-MM-dd') } else { 'Never/NotSet' }
          $last = if ($u.LastLogon) { $u.LastLogon.ToString('yyyy-MM-dd HH:mm') } else { '-' }
          $flags = @()
          if ($u.Enabled) { $flags += 'Enabled' } else { $flags += 'Disabled' }
          if ($u.Name -eq $current) { $flags += 'CURRENT_SESSION' }
          if ($u.SID -and ($u.SID.Value -match '-500$' -or $u.SID.Value -match '-501$')) {
            $flags += 'BUILT_IN'
          }
          [PSCustomObject]@{
            Name = $u.Name
            Enabled = $u.Enabled
            LastLogon = $last
            PasswordExpires = $pwdExpires
            Description = $u.Description
            Flags = ($flags -join ',')
          }
        }
        $text = $rows | Format-Table -AutoSize -Wrap | Out-String -Width 200
        Write-Output $text

        Write-Output '=== Administrators (S-1-5-32-544) members ==='
        try {
          $adminSid = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
          $adminName = $adminSid.Translate([System.Security.Principal.NTAccount]).Value
          if ($adminName -match '\\') { $adminName = $adminName.Split('\')[-1] }
          Write-Output "Localized group name: $adminName"
          $admins = @(Get-LocalGroupMember -Group $adminName -ErrorAction SilentlyContinue)
          if ($admins.Count -eq 0) {
            # net fallback often works without elevation
            $net = & net.exe localgroup $adminName 2>&1 | Out-String
            Write-Output $net
          } else {
            foreach ($m in $admins) {
              Write-Output ("  {0} | {1} | {2}" -f $m.Name, $m.ObjectClass, $m.PrincipalSource)
            }
          }
        } catch {
          Write-Output ("  (не удалось: {0})" -f $_.Exception.Message)
        }

        Write-Output ''
        Write-Output 'Enabled built-in accounts:'
        $rows | Where-Object { $_.Flags -match 'BUILT_IN' -and $_.Enabled -eq $true } | ForEach-Object {
          Write-Output ("  {0}" -f $_.Name)
        }
        exit 0
        """;

    private static string ListGroupsScript() => """
        $ErrorActionPreference = 'SilentlyContinue'
        Write-Output '=== local_users list_groups ==='
        try {
          $groups = @(Get-LocalGroup -ErrorAction Stop | Sort-Object Name)
        } catch {
          $msg = $_.Exception.Message
          if ($msg -match 'Access denied|elevation|administrator|0x80070005|недостаточно|отказано') {
            Write-Output 'пропущено: нужны права администратора для Get-LocalGroup.'
            exit 0
          }
          Write-Output "ERROR: $msg"
          exit 1
        }
        $groups | Select-Object Name, Description, SID |
          Format-Table -AutoSize -Wrap | Out-String -Width 200 | Write-Output
        Write-Output ("Всего: {0}" -f $groups.Count)
        exit 0
        """;

    private static string GroupMembersScript(string safeGroup) => $$"""
        $ErrorActionPreference = 'SilentlyContinue'
        $group = '{{safeGroup}}'
        Write-Output "=== local_users group_members (PS fallback): $group ==="
        try {
          $members = @(Get-LocalGroupMember -Group $group -ErrorAction Stop)
          foreach ($m in $members) {
            Write-Output ("{0} | {1} | {2}" -f $m.Name, $m.ObjectClass, $m.PrincipalSource)
          }
          Write-Output ("Всего: {0}" -f $members.Count)
          exit 0
        } catch {
          Write-Output "ERROR: $($_.Exception.Message)"
          exit 1
        }
        """;

    private static string UserDetailsScript(string safeUser) => $$"""
        $ErrorActionPreference = 'SilentlyContinue'
        $name = '{{safeUser}}'
        Write-Output "=== local_users user_details: $name ==="
        try {
          $u = Get-LocalUser -Name $name -ErrorAction Stop
        } catch {
          Write-Output "User not found or inaccessible: $name"
          Write-Output $_.Exception.Message
          exit 1
        }
        $u | Select-Object Name, FullName, Description, Enabled, LastLogon, PasswordRequired, PasswordExpires,
          UserMayChangePassword, PasswordLastSet, AccountExpires, SID |
          Format-List | Out-String -Width 200 | Write-Output
        exit 0
        """;

    private static string SetUserEnabledScript(string safeUser, bool enabled)
    {
        var verb = enabled ? "enable_user" : "disable_user";
        var want = enabled ? "$true" : "$false";
        return $$"""
            $ErrorActionPreference = 'Stop'
            $name = '{{safeUser}}'
            $wantEnabled = {{want}}
            Write-Output "=== local_users {{verb}}: $name ==="

            try {
              $u = Get-LocalUser -Name $name -ErrorAction Stop
            } catch {
              Write-Output "User not found: $name"
              exit 1
            }

            Write-Output "PreviousEnabled: $($u.Enabled)"
            if ($u.Enabled -eq $wantEnabled) {
              Write-Output "Already in desired state (Enabled=$wantEnabled)."
              exit 0
            }

            try {
              if ($wantEnabled) { Enable-LocalUser -Name $name -ErrorAction Stop }
              else { Disable-LocalUser -Name $name -ErrorAction Stop }
              $after = Get-LocalUser -Name $name
              Write-Output "NewEnabled: $($after.Enabled)"
              Write-Output 'Done. The session snapshot does not re-apply user Enabled.'
              exit 0
            } catch {
              $msg = $_.Exception.Message
              if ($msg -match 'Access denied|elevation|administrator|0x80070005|недостаточно|отказано') {
                Write-Output 'требуются права администратора для изменения учётки'
                exit 1
              }
              Write-Output "ERROR: $msg"
              exit 1
            }
            """;
    }

    private static string ChangeGroupScript(string safeUser, string safeGroup, bool add)
    {
        var verb = add ? "add_to_group" : "remove_from_group";
        var addLiteral = add ? "$true" : "$false";
        return $$"""
            $ErrorActionPreference = 'Stop'
            $user = '{{safeUser}}'
            $group = '{{safeGroup}}'
            Write-Output "=== local_users {{verb}}: user=$user group=$group ==="

            try {
              $membersBefore = @(Get-LocalGroupMember -Group $group -ErrorAction Stop)
            } catch {
              Write-Output "Group not found or inaccessible: $group"
              Write-Output $_.Exception.Message
              exit 1
            }

            Write-Output 'Previous members:'
            foreach ($m in $membersBefore) { Write-Output ("  {0}" -f $m.Name) }

            $already = $false
            foreach ($m in $membersBefore) {
              if ($m.Name -eq $user -or $m.Name -like "*\$user" -or $m.Name -eq ("$env:COMPUTERNAME\$user")) {
                $already = $true
              }
            }

            if ({{addLiteral}}) {
              if ($already) {
                Write-Output "User already in group."
                exit 0
              }
              try {
                Add-LocalGroupMember -Group $group -Member $user -ErrorAction Stop
                Write-Output "Added $user to $group."
              } catch {
                $msg = $_.Exception.Message
                if ($msg -match 'Access denied|elevation|administrator|0x80070005') {
                  Write-Output 'требуются права администратора для изменения членства'
                  exit 1
                }
                Write-Output "ERROR: $msg"
                exit 1
              }
            } else {
              if (-not $already) {
                Write-Output "User is not a member of the group."
                exit 0
              }
              try {
                Remove-LocalGroupMember -Group $group -Member $user -ErrorAction Stop
                Write-Output "Removed $user from $group."
              } catch {
                $msg = $_.Exception.Message
                if ($msg -match 'Access denied|elevation|administrator|0x80070005') {
                  Write-Output 'требуются права администратора для изменения членства'
                  exit 1
                }
                Write-Output "ERROR: $msg"
                exit 1
              }
            }

            Write-Output 'New members:'
            try {
              Get-LocalGroupMember -Group $group -ErrorAction SilentlyContinue | ForEach-Object {
                Write-Output ("  {0}" -f $_.Name)
              }
            } catch {}
            Write-Output 'Done. The session snapshot does not restore group membership.'
            exit 0
            """;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
