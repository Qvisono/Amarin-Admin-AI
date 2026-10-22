using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Amarin.Tools;

/// <summary>Запись списка доступа в виде, удобном для разбора: чья, какие права, разрешение или запрет.</summary>
/// <param name="InheritOnly">Только для наследников: к самому объекту не относится.</param>
internal sealed record AceView(SecurityIdentifier Sid, string Who, int Rights, bool Allow, bool Inherited, bool InheritOnly);

/// <summary>Чем кончилось пробное открытие объекта.</summary>
internal enum ProbeResult
{
    Ok,
    Denied,

    /// <summary>Объект открыт другой программой без общего доступа.</summary>
    InUse,
    Failed
}

/// <summary>
/// Разбор «Отказано в доступе»: что говорят записи доступа о правах этого процесса и что
/// получается на деле, когда объект открывают.
/// </summary>
/// <remarks>
/// <para>
/// Группы берутся из маркера процесса, включая «только для запрета»: так видно UAC. Пользователь
/// входит в «Администраторы», но процесс не повышен — права этой группы к нему не применяются,
/// а её запреты применяются. Разбор по одному списку доступа это не показал бы.
/// </para>
/// <para>
/// Пробное открытие ничего не меняет: объект открывается с нужным доступом и сразу
/// закрывается. Оно отличает отказ доступа от «занят другой программой» — а это две разные
/// поломки с разными лекарствами.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class AccessExplain
{
    /// <summary>Проверяемые права — названия для ответа и биты маски доступа.</summary>
    internal static readonly (string Name, int Bit)[] Checks =
    [
        ("read", 0x0001),
        ("write", 0x0002),
        ("delete", 0x10000),
        ("change permissions", 0x40000),
        ("take ownership", 0x80000)
    ];

    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");
    private const int ReadControl = 0x20000;
    private const int WriteDac = 0x40000;

    /// <summary>Решение по одному праву: первое подходящее правило в порядке списка.</summary>
    /// <returns>true — разрешено, false — запрещено, null — никто не дал; и правило, которое решило.</returns>
    internal static (bool? Allowed, AceView? By) Decide(
        IEnumerable<AceView> aces,
        int bit,
        IReadOnlySet<SecurityIdentifier> enabled,
        IReadOnlySet<SecurityIdentifier> denyOnly)
    {
        foreach (var ace in aces)
        {
            if (ace.InheritOnly || (ace.Rights & bit) == 0)
            {
                continue;
            }

            // Группа «только для запрета» попадает под запреты, но не под разрешения.
            var applies = enabled.Contains(ace.Sid) || (!ace.Allow && denyOnly.Contains(ace.Sid));
            if (applies)
            {
                return (ace.Allow, ace);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Права из общих битов в конкретные: <c>GENERIC_ALL</c> у файла — полный доступ. Без этого
    /// запись «полный доступ», заданная общим битом, выглядела бы как отсутствие прав.
    /// </summary>
    internal static int MapGeneric(int rights, bool registry)
    {
        const int genericAll = 0x10000000, genericExecute = 0x20000000, genericWrite = 0x40000000;
        const int genericRead = unchecked((int)0x80000000);
        var mapped = rights & 0x0FFFFFFF;
        if ((rights & genericAll) != 0)
        {
            mapped |= registry ? 0xF003F : 0x1F01FF;
        }

        if ((rights & genericRead) != 0 || (rights & genericExecute) != 0)
        {
            mapped |= registry ? 0x20019 : 0x1200A9;
        }

        if ((rights & genericWrite) != 0)
        {
            mapped |= registry ? 0x20006 : 0x120116;
        }

        return mapped;
    }

    public static List<AceView> Aces(NativeObjectSecurity security, bool registry)
    {
        var result = new List<AceView>();
        foreach (AuthorizationRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference is not SecurityIdentifier sid)
            {
                continue;
            }

            var (rights, allow) = rule switch
            {
                FileSystemAccessRule file => ((int)file.FileSystemRights, file.AccessControlType == AccessControlType.Allow),
                RegistryAccessRule key => ((int)key.RegistryRights, key.AccessControlType == AccessControlType.Allow),
                _ => (0, false)
            };
            result.Add(new AceView(sid, PermissionTargets.NameOf(sid), MapGeneric(rights, registry), allow, rule.IsInherited,
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0));
        }

        return result;
    }

    /// <summary>Отчёт для модели: владелец, маркер процесса, решение списка, пробы и вероятные причины.</summary>
    public static string Report(AclTarget target)
    {
        // Права могут быть закрыты и для чтения — тогда разбор опирается только на пробы.
        NativeObjectSecurity? security = null;
        try
        {
            security = AclIo.Read(target);
        }
        catch (UnauthorizedAccessException)
        {
        }

        var aces = security is null ? new List<AceView>() : Aces(security, target.IsRegistry);
        var owner = security?.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

        using var identity = WindowsIdentity.GetCurrent();
        var enabled = new HashSet<SecurityIdentifier>();
        if (identity.User is { } user)
        {
            enabled.Add(user);
        }

        if (identity.Groups is { } groups)
        {
            // Groups отдаёт только включённые группы; «только для запрета» — ниже, из утверждений.
            foreach (var group in groups)
            {
                if (group is SecurityIdentifier sid)
                {
                    enabled.Add(sid);
                }
            }
        }

        var denyOnly = identity.Claims
            .Where(claim => claim.Type == ClaimTypes.DenyOnlySid)
            .Select(claim => TrySid(claim.Value))
            .OfType<SecurityIdentifier>()
            .ToHashSet();
        var filteredAdmin = denyOnly.Contains(Administrators);
        var owns = owner is not null && enabled.Contains(owner);

        var sb = new StringBuilder();
        sb.AppendLine($"Object: {target.Path} ({(target.IsRegistry ? "registry key" : target.IsDirectory ? "folder" : "file")})");
        if (security is null)
        {
            sb.AppendLine("Permissions: this account may not even read them (no read permissions right).");
        }
        else
        {
            sb.AppendLine($"Owner: {(owner is null ? "unknown" : PermissionTargets.NameOf(owner))}{(owns ? " (this account or its group)" : "")}");
            sb.AppendLine($"Inheritance from parent: {(security.AreAccessRulesProtected ? "off" : "on")}");
        }

        sb.AppendLine($"This program runs as: {identity.Name}, elevated: {(filteredAdmin ? "no (Administrators is deny-only: UAC)" : new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ? "yes" : "no")}");

        FileAttributes? attributes = null;
        if (!target.IsRegistry)
        {
            attributes = File.GetAttributes(target.Path);
            sb.AppendLine($"Attributes: {attributes}");
        }

        sb.AppendLine("Check (by the permissions | by opening the object, nothing is changed):");
        var causes = new List<string>();
        foreach (var (name, bit) in Checks)
        {
            var (allowed, by) = Decide(aces, bit, enabled, denyOnly);

            // Владелец всегда может читать и менять права, если нет записи OWNER RIGHTS.
            var implicitOwner = owns && (bit & (ReadControl | WriteDac)) != 0 && !aces.Any(ace => ace.Sid.Equals(OwnerRights));
            var verdict = allowed switch
            {
                true => $"allowed by {by!.Who}",
                false => $"denied by {by!.Who}",
                null when implicitOwner => "allowed to the owner",
                null when security is null => "unknown",
                _ => "not granted"
            };
            var probe = target.IsRegistry ? ProbeKey(target.Registry, bit) : ProbeFile(target.Path, target.IsDirectory, bit);
            sb.AppendLine($"  {name}: {verdict} | {Text(probe)}");

            if (probe is ProbeResult.Ok)
            {
                continue;
            }

            if (probe == ProbeResult.InUse)
            {
                causes.Add($"{name}: the object is open in another program without sharing. file_locks find shows which one.");
            }
            else if (allowed == false)
            {
                causes.Add($"{name}: a deny entry for {by!.Who} blocks it{(by.Inherited ? " (inherited from a parent)" : "")}.");
            }
            else if (allowed is null && security is null)
            {
                causes.Add($"{name}: refused; the permissions cannot be read by this account, so the reason is not visible without administrator rights.");
            }
            else if (allowed is null && !implicitOwner)
            {
                var admin = aces.FirstOrDefault(ace => ace.Allow && !ace.InheritOnly && (ace.Rights & bit) != 0 && denyOnly.Contains(ace.Sid));
                causes.Add(admin is not null
                    ? $"{name}: granted to {admin.Who} only, and this process is not elevated (UAC): it works when the program runs as administrator."
                    : $"{name}: no entry grants it to this account or its groups.");
            }
            else if (bit is 0x0002 or 0x10000 && attributes?.HasFlag(FileAttributes.ReadOnly) == true && !target.IsDirectory)
            {
                causes.Add($"{name}: the read-only attribute blocks it. It is an attribute, not a permission.");
            }
            else if (attributes?.HasFlag(FileAttributes.Encrypted) == true)
            {
                causes.Add($"{name}: the file is encrypted (EFS), and only the account that encrypted it can open it.");
            }
            else
            {
                causes.Add($"{name}: the permissions allow it, but Windows refuses: an integrity level, a parent folder or a policy decides.");
            }
        }

        if (owner?.Value == PermissionTargets.TrustedInstallerSid)
        {
            causes.Add("Owned by TrustedInstaller: Windows servicing keeps it, and changing it can break updates.");
        }

        sb.AppendLine(causes.Count == 0 ? "Likely causes: none, access works." : "Likely causes:");
        foreach (var cause in causes)
        {
            sb.AppendLine("  - " + cause);
        }

        return sb.ToString().TrimEnd();
    }

    private static string Text(ProbeResult probe) => probe switch
    {
        ProbeResult.Ok => "opens",
        ProbeResult.Denied => "ACCESS DENIED",
        ProbeResult.InUse => "IN USE by another program",
        _ => "error"
    };

    private static ProbeResult ProbeFile(string path, bool directory, int access)
    {
        const uint shareAll = 0x7, openExisting = 3, backupSemantics = 0x02000000;
        using var handle = CreateFileW(path, (uint)access, shareAll, IntPtr.Zero, openExisting, directory ? backupSemantics : 0, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return ProbeResult.Ok;
        }

        return Marshal.GetLastWin32Error() switch
        {
            5 => ProbeResult.Denied,
            32 or 33 => ProbeResult.InUse,
            _ => ProbeResult.Failed
        };
    }

    private static ProbeResult ProbeKey(RegistryPath path, int access)
    {
        try
        {
            using var root = RegistryStateIo.OpenRoot(path.Root);
            if (root is null)
            {
                return ProbeResult.Failed;
            }

            using var key = path.SubKey.Length == 0
                ? null
                : root.OpenSubKey(path.SubKey, RegistryKeyPermissionCheck.ReadWriteSubTree, (RegistryRights)access);
            return key is null && path.SubKey.Length > 0 ? ProbeResult.Failed : ProbeResult.Ok;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return ProbeResult.Denied;
        }
        catch (IOException)
        {
            return ProbeResult.Failed;
        }
    }

    private static SecurityIdentifier? TrySid(string value)
    {
        try
        {
            return new SecurityIdentifier(value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
