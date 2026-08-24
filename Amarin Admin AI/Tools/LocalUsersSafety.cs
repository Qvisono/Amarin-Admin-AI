using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amarin.Tools;

/// <summary>
/// Validation and hard safety blocks for local_users (SID-based, not English name strings).
/// Hard blocks are evaluated before confirm and before mutation.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class LocalUsersSafety
{
    // Unicode letters + digits + common SAM punctuation; no shell metacharacters.
    [GeneratedRegex(@"^[\p{L}\p{N} ._\-$]{1,64}$")]
    private static partial Regex AccountNamePattern();

    [GeneratedRegex(@"Name\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex WmiNamePattern();

    [GeneratedRegex(@"Domain\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex WmiDomainPattern();

    [GeneratedRegex(@"[;|&`$()""'\r\n<>]")]
    private static partial Regex DangerousChars();

    /// <summary>Well-known BUILTIN\Administrators — S-1-5-32-544.</summary>
    public static SecurityIdentifier BuiltinAdministratorsSid { get; } =
        new(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null);

    /// <summary>
    /// If the action is hard-blocked, returns true and a Fail message (no confirm should be shown).
    /// </summary>
    public static bool TryGetHardBlockReason(JsonElement arguments, out string reason)
    {
        reason = string.Empty;
        if (!arguments.TryGetProperty("action", out var actionProp) ||
            actionProp.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var action = actionProp.GetString()?.ToLowerInvariant();
        return action switch
        {
            "disable_user" => IsDisableCurrentUserBlocked(arguments, out reason),
            "remove_from_group" => IsRemoveLastAdminBlocked(arguments, out reason),
            _ => false
        };
    }

    public static bool TryValidateAccountName(string? raw, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "name is required";
            return false;
        }

        var name = raw.Trim();
        if (DangerousChars().IsMatch(name))
        {
            error = "name contains forbidden characters (; | & ` $ ( ) quotes, newlines)";
            return false;
        }

        // DOMAIN\User → validate each segment that is present
        var local = name.Contains('\\', StringComparison.Ordinal)
            ? name[(name.LastIndexOf('\\') + 1)..]
            : name;

        if (local.Length is 0 or > 64 || !AccountNamePattern().IsMatch(local))
        {
            error =
                "name invalid. Allowed: Unicode letters/digits, space, . _ - $ (max 64). " +
                "Examples: Администраторы, Пользователи, Гость, Users.";
            return false;
        }

        // Full string may be DOMAIN\user — allow letters in domain part too
        if (name.Contains('\\', StringComparison.Ordinal))
        {
            var domain = name[..name.LastIndexOf('\\')];
            if (domain.Length > 0 && (DangerousChars().IsMatch(domain) || domain.Length > 64))
            {
                error = "domain part of name is invalid";
                return false;
            }
        }

        normalized = name;
        return true;
    }

    public static string EscapeForPowerShell(string name) =>
        name.Replace("'", "''", StringComparison.Ordinal);

    public static bool TryGetCurrentUserSid(out SecurityIdentifier sid)
    {
        sid = WindowsIdentity.GetCurrent().User!;
        return sid is not null;
    }

    public static bool TryResolveAccountSid(string accountName, out SecurityIdentifier sid, out string? error)
    {
        sid = null!;
        error = null;
        var sam = accountName.Contains('\\', StringComparison.Ordinal)
            ? accountName[(accountName.LastIndexOf('\\') + 1)..]
            : accountName;

        // Prefer machine-local translation for local SAM names.
        foreach (var candidate in new[]
                 {
                     new NTAccount(Environment.MachineName, sam),
                     new NTAccount(sam),
                     new NTAccount(accountName)
                 })
        {
            try
            {
                sid = (SecurityIdentifier)candidate.Translate(typeof(SecurityIdentifier));
                return true;
            }
            catch
            {
                // try next
            }
        }

        error = $"Cannot resolve SID for account «{accountName}»";
        return false;
    }

    public static bool TryResolveGroupSid(string groupName, out SecurityIdentifier sid, out string? error)
    {
        sid = null!;
        error = null;
        var sam = groupName.Contains('\\', StringComparison.Ordinal)
            ? groupName[(groupName.LastIndexOf('\\') + 1)..]
            : groupName;

        foreach (var candidate in new[]
                 {
                     new NTAccount("BUILTIN", sam),
                     new NTAccount(Environment.MachineName, sam),
                     new NTAccount(sam),
                     new NTAccount(groupName)
                 })
        {
            try
            {
                sid = (SecurityIdentifier)candidate.Translate(typeof(SecurityIdentifier));
                return true;
            }
            catch
            {
                // try next
            }
        }

        error = $"Cannot resolve SID for group «{groupName}»";
        return false;
    }

    public static bool IsBuiltinAdministratorsGroup(string groupName)
    {
        if (TryResolveGroupSid(groupName, out var sid, out _))
        {
            return sid.Equals(BuiltinAdministratorsSid);
        }

        // Fallback string hints (should rarely be needed if SID resolve works)
        return groupName.Equals("Administrators", StringComparison.OrdinalIgnoreCase) ||
               groupName.Equals("Администраторы", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Localized SAM name for BUILTIN\Administrators (e.g. Administrators / Администраторы).</summary>
    public static string GetBuiltinAdministratorsSamName()
    {
        try
        {
            var nt = (NTAccount)BuiltinAdministratorsSid.Translate(typeof(NTAccount));
            var value = nt.Value; // BUILTIN\Administrators or BUILTIN\Администраторы
            var idx = value.LastIndexOf('\\');
            return idx >= 0 ? value[(idx + 1)..] : value;
        }
        catch
        {
            return "Administrators";
        }
    }

    /// <summary>
    /// Read group members without requiring elevation when possible (WMI, then net localgroup).
    /// </summary>
    public static bool TryListGroupMembers(string groupName, out IReadOnlyList<string> members, out string? error)
    {
        members = Array.Empty<string>();
        error = null;

        var resolvedName = groupName;
        if (IsBuiltinAdministratorsGroup(groupName))
        {
            resolvedName = GetBuiltinAdministratorsSamName();
        }

        // 1) WMI Win32_GroupUser — Unicode-safe, often no elevation for read.
        if (TryListGroupMembersWmi(resolvedName, out var wmiMembers, out _))
        {
            members = wmiMembers;
            return true;
        }

        // 2) net localgroup
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "net.exe",
                ArgumentList = { "localgroup", resolvedName },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Default,
                StandardErrorEncoding = Encoding.Default
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                error = "Failed to start net.exe";
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                error = "net localgroup timed out";
                return false;
            }

            if (process.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = $"net localgroup failed (exit {process.ExitCode})";
                }

                return false;
            }

            members = ParseNetLocalGroupMembers(stdout);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryListGroupMembersWmi(
        string groupSamName,
        out IReadOnlyList<string> members,
        out string? error)
    {
        members = Array.Empty<string>();
        error = null;
        try
        {
            var machine = Environment.MachineName;
            // PartComponent = Win32_Group.Domain="MACHINE",Name="Administrators"
            var filter =
                $"GroupComponent=\"Win32_Group.Domain=\\\"{EscapeWmi(machine)}\\\",Name=\\\"{EscapeWmi(groupSamName)}\\\"\"";
            using var searcher = new ManagementObjectSearcher(
                "SELECT PartComponent FROM Win32_GroupUser WHERE " + filter);
            var list = new List<string>();
            foreach (ManagementObject row in searcher.Get())
            {
                var part = row["PartComponent"]?.ToString() ?? "";
                // ...Win32_UserAccount.Domain="X",Name="Y" or Win32_Group...
                var name = ExtractWmiName(part);
                var domain = ExtractWmiDomain(part);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                list.Add(string.IsNullOrEmpty(domain) || domain.Equals(machine, StringComparison.OrdinalIgnoreCase)
                    ? name
                    : domain + "\\" + name);
            }

            if (list.Count == 0)
            {
                error = "WMI returned no members";
                return false;
            }

            members = list;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string EscapeWmi(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string ExtractWmiName(string partComponent)
    {
        var m = WmiNamePattern().Match(partComponent);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string ExtractWmiDomain(string partComponent)
    {
        var m = WmiDomainPattern().Match(partComponent);
        return m.Success ? m.Groups[1].Value : "";
    }

    public static bool IsLocalUserEnabled(string accountName)
    {
        var sam = accountName.Contains('\\', StringComparison.Ordinal)
            ? accountName[(accountName.LastIndexOf('\\') + 1)..]
            : accountName;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Disabled FROM Win32_UserAccount WHERE LocalAccount=True");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "";
                if (!name.Equals(sam, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var disabled = obj["Disabled"];
                if (disabled is bool b)
                {
                    return !b;
                }

                if (disabled is not null && bool.TryParse(disabled.ToString(), out var d))
                {
                    return !d;
                }

                return true;
            }
        }
        catch
        {
            // fall through — assume enabled if unknown (safer for last-admin check: over-count)
        }

        // Unknown account type (domain) — treat as "active" so we don't block on domain-only admins incorrectly
        // when other enabled local admins exist; if only domain members remain, still protect if zero local enabled.
        return true;
    }

    private static bool IsDisableCurrentUserBlocked(JsonElement arguments, out string reason)
    {
        reason = string.Empty;
        if (!arguments.TryGetProperty("user", out var userProp) ||
            userProp.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(userProp.GetString()))
        {
            return false; // missing param → normal Fail later, not hard-block
        }

        if (!TryValidateAccountName(userProp.GetString(), out var user, out _))
        {
            return false;
        }

        if (!TryGetCurrentUserSid(out var currentSid))
        {
            return false;
        }

        if (!TryResolveAccountSid(user, out var targetSid, out _))
        {
            // Cannot resolve — fall back to string compare on SAM
            var currentSam = Environment.UserName;
            var targetSam = user.Contains('\\') ? user[(user.LastIndexOf('\\') + 1)..] : user;
            if (currentSam.Equals(targetSam, StringComparison.OrdinalIgnoreCase))
            {
                reason =
                    $"ЗАПРЕЩЕНО: нельзя отключить текущего пользователя сессии ({currentSam}). " +
                    "Действие отклонено до подтверждения.";
                return true;
            }

            return false;
        }

        if (currentSid.Equals(targetSid))
        {
            reason =
                $"ЗАПРЕЩЕНО: нельзя отключить текущего пользователя сессии " +
                $"(SID {currentSid.Value}, {Environment.UserName}). Действие отклонено до подтверждения.";
            return true;
        }

        return false;
    }

    private static bool IsRemoveLastAdminBlocked(JsonElement arguments, out string reason)
    {
        reason = string.Empty;
        if (!arguments.TryGetProperty("user", out var userProp) ||
            userProp.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(userProp.GetString()))
        {
            return false;
        }

        if (!arguments.TryGetProperty("group", out var groupProp) ||
            groupProp.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(groupProp.GetString()))
        {
            return false;
        }

        if (!TryValidateAccountName(userProp.GetString(), out var user, out _))
        {
            return false;
        }

        if (!TryValidateAccountName(groupProp.GetString(), out var group, out _))
        {
            return false;
        }

        if (!IsBuiltinAdministratorsGroup(group))
        {
            return false; // only protect Administrators (S-1-5-32-544)
        }

        if (!TryListGroupMembers(group, out var members, out var listError))
        {
            // Cannot verify — block conservatively when targeting admin group removal
            reason =
                "ЗАПРЕЩЕНО: не удалось проверить состав группы Администраторы (S-1-5-32-544) " +
                $"перед remove_from_group ({listError}). Действие отклонено до подтверждения.";
            return true;
        }

        var targetSam = user.Contains('\\') ? user[(user.LastIndexOf('\\') + 1)..] : user;
        var isMember = members.Any(m =>
        {
            var sam = m.Contains('\\') ? m[(m.LastIndexOf('\\') + 1)..] : m;
            return sam.Equals(targetSam, StringComparison.OrdinalIgnoreCase) ||
                   m.Equals(user, StringComparison.OrdinalIgnoreCase);
        });

        if (!isMember)
        {
            return false; // not in group — tool will report "not a member"
        }

        // Enabled members excluding the user being removed
        var otherEnabled = new List<string>();
        foreach (var m in members)
        {
            var sam = m.Contains('\\') ? m[(m.LastIndexOf('\\') + 1)..] : m;
            if (sam.Equals(targetSam, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsLocalUserEnabled(sam) || IsLikelyDomainPrincipal(m))
            {
                // Domain/group principals count as remaining admin path
                otherEnabled.Add(m);
            }
        }

        // Count only local enabled users among remaining; domain groups still leave admin capability.
        // Hard-block only when NO remaining member is enabled local user AND no domain/group principal remains.
        var remainingEnabledLocal = new List<string>();
        var remainingOther = new List<string>();
        foreach (var m in members)
        {
            var sam = m.Contains('\\') ? m[(m.LastIndexOf('\\') + 1)..] : m;
            if (sam.Equals(targetSam, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsLikelyDomainPrincipal(m))
            {
                remainingOther.Add(m);
                continue;
            }

            if (IsLocalUserEnabled(sam))
            {
                remainingEnabledLocal.Add(m);
            }
        }

        if (remainingEnabledLocal.Count == 0 && remainingOther.Count == 0)
        {
            reason =
                "ЗАПРЕЩЕНО: нельзя удалить последнего активного члена группы Администраторы " +
                $"(S-1-5-32-544 / {GetBuiltinAdministratorsSamName()}). " +
                $"Удаление «{user}» оставило бы группу без Enabled-участников. " +
                "Действие отклонено до подтверждения.";
            return true;
        }

        return false;
    }

    private static bool IsLikelyDomainPrincipal(string member)
    {
        // DOMAIN\name where DOMAIN is not local machine / BUILTIN
        if (!member.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        var domain = member[..member.IndexOf('\\')];
        if (domain.Equals("BUILTIN", StringComparison.OrdinalIgnoreCase) ||
            domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
            domain.Equals(".", StringComparison.OrdinalIgnoreCase) ||
            domain.Equals("NT AUTHORITY", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static IReadOnlyList<string> ParseNetLocalGroupMembers(string stdout)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return list;
        }

        var lines = stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var pastHeader = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // net localgroup prints a line of dashes before members
            if (line.StartsWith("---", StringComparison.Ordinal) ||
                line.All(c => c is '-' or '—' or '='))
            {
                pastHeader = true;
                continue;
            }

            if (!pastHeader)
            {
                continue;
            }

            // Footer: "The command completed successfully" / "Команда выполнена успешно" / mojibake
            if (IsNetLocalGroupFooter(line))
            {
                break;
            }

            list.Add(line);
        }

        // Drop trailing sentence-like line if footer heuristic missed (OEM mojibake).
        while (list.Count > 0 && LooksLikeNetFooter(list[^1]))
        {
            list.RemoveAt(list.Count - 1);
        }

        return list;
    }

    private static bool IsNetLocalGroupFooter(string line) =>
        line.Contains("completed successfully", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("successfully", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("выполнена успешно", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("выполнена", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("успешно", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("The command", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Команда", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeNetFooter(string line)
    {
        if (IsNetLocalGroupFooter(line))
        {
            return true;
        }

        // Sentence with several words and no DOMAIN\user — not a member line.
        if (line.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 3;
    }
}
