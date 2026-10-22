using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>Объект, у которого смотрят или меняют права: файл, папка или раздел реестра.</summary>
/// <param name="Path">Полный путь файла или папки, или раздел реестра в виде <c>HKLM\…</c>.</param>
internal sealed record AclTarget(string Path, bool IsRegistry, bool IsDirectory, RegistryPath Registry);

/// <summary>
/// Что можно передать инструменту <c>permissions</c>: разбор цели, учётной записи и прав и
/// жёсткие запреты на изменение.
/// </summary>
/// <remarks>
/// <para>
/// Запреты — для мест, где смена владельца или прав ломает саму Windows: её папка, корни дисков,
/// корни папок программ и профилей, кусты реестра и их первый уровень, SAM, SECURITY и BCD. Это
/// классический способ «починить доступ» так, что система больше не загрузится или не обновится,
/// и человек, нажавший «Да» на вопрос, вряд ли это имел в виду. Подпапки программ и профиля
/// разрешены: права там обычно и чинят.
/// </para>
/// <para>
/// Учётная запись принимается псевдонимом встроенной группы, именем или SID. Псевдонимы идут
/// через well-known SID: на русской Windows группа «Users» называется «Пользователи», и имя
/// из модели не нашлось бы.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class PermissionTargets
{
    /// <summary>Больше объектов рекурсивная смена не трогает: снимок и откат должны оставаться обозримыми.</summary>
    public const int MaxRecursiveItems = 10_000;

    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private static readonly string[] RootServiceFolders =
        ["System Volume Information", "$Recycle.Bin", "Recovery", "Boot", "EFI", "$WinREAgent", "Config.Msi", "Documents and Settings"];

    public static bool IsWrite(string action) => action is "set_owner" or "grant" or "revoke" or "reset";

    /// <summary>Разбирает путь: раздел реестра или существующий файл либо папка с полным путём.</summary>
    public static bool TryParse(string? raw, [NotNullWhen(true)] out AclTarget? target, out string error)
    {
        target = null;
        error = "";
        var value = (raw ?? "").Trim().Trim('"');
        if (value.Length == 0)
        {
            error = "path is required: a full file or folder path, or a registry key such as HKLM\\…";
            return false;
        }

        if (RegistryPath.TryParse(value, out var key, requireSubKey: false))
        {
            target = new AclTarget(key.Canonical, IsRegistry: true, IsDirectory: false, key);
            return true;
        }

        if (!Path.IsPathFullyQualified(value))
        {
            error = "path must be a full path that starts with a drive letter or \\\\server. Do not retry with a relative path.";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"path is not a valid path: {ex.Message}";
            return false;
        }

        var isDirectory = Directory.Exists(full);
        if (!isDirectory && !File.Exists(full))
        {
            error = $"{full} does not exist.";
            return false;
        }

        target = new AclTarget(full, IsRegistry: false, isDirectory, default);
        return true;
    }

    /// <summary>
    /// Запрет на изменение без вопроса. Зовут шлюз (до вопроса) и сам инструмент; отказ — текст
    /// для модели.
    /// </summary>
    public static bool TryGetHardBlockReason(JsonElement arguments, out string reason)
    {
        reason = "";
        if (!IsWrite(DangerousActionGuard.ActionOf(arguments)) ||
            !TryParse(FileToolPaths.String(arguments, "path"), out var target, out _))
        {
            return false;
        }

        return TryRefuse(target, out reason);
    }

    public static bool TryRefuse(AclTarget target, out string reason)
    {
        reason = "";
        if (target.IsRegistry)
        {
            if (IsProtectedKey(target.Registry))
            {
                reason = Loc.Format("S.Tool.Acl.ProtectedKey", target.Path);
                return true;
            }

            return false;
        }

        if (SensitivePaths.IsProgramData(target.Path))
        {
            reason = SensitivePaths.ProgramDataRefusal(target.Path);
            return true;
        }

        if (SensitivePaths.IsSensitive(target.Path, out var secret))
        {
            reason = secret;
            return true;
        }

        if (target.Path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            reason = Loc.Format("S.Tool.Acl.Network", target.Path);
            return true;
        }

        if (IsProtectedFolder(target.Path))
        {
            reason = Loc.Format("S.Tool.Acl.ProtectedFolder", target.Path);
            return true;
        }

        return false;
    }

    /// <summary>Системное место файловой системы, где права не меняются ни с каким вопросом.</summary>
    internal static bool IsProtectedFolder(string path)
    {
        var canonical = Canonical(path);
        var root = Canonical(Path.GetPathRoot(path) ?? "");
        if (canonical.Length == 0 || canonical.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var windows = Canonical(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (Under(canonical, windows))
        {
            return true;
        }

        foreach (var folder in RootServiceFolders)
        {
            if (Under(canonical, root + "\\" + folder))
            {
                return true;
            }
        }

        var profile = Canonical(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var users = Path.GetDirectoryName(profile) ?? "";
        string[] roots =
        [
            Canonical(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            Canonical(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            Canonical(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            Canonical(users)
        ];
        if (roots.Any(item => item.Length > 0 && canonical.Equals(item, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Корень любого профиля: под ним живут реестр пользователя и его AppData.
        return users.Length > 0 &&
               Path.GetDirectoryName(canonical) is { } parent &&
               parent.Equals(Canonical(users), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Корни кустов, их первый уровень, SAM, SECURITY, BCD и управляющие наборы SYSTEM.</summary>
    internal static bool IsProtectedKey(RegistryPath key)
    {
        var parts = key.SubKey.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
        {
            return true;
        }

        if (key.Root == "HKLM" &&
            (SensitivePaths.IsSecretHive(key.SubKey) || parts[0].Equals("BCD00000000", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // SYSTEM\CurrentControlSet и ControlSet001… с их прямыми детьми (Control, Services, Enum):
        // глубже лежат ключи отдельных служб и устройств, права которых чинят по делу.
        return key.Root == "HKLM" && parts[0].Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) &&
               ControlSet().IsMatch(parts[1]) && parts.Length <= 3;
    }

    /// <summary>Учётная запись по псевдониму, SID или имени.</summary>
    public static bool TryResolvePrincipal(string? raw, [NotNullWhen(true)] out SecurityIdentifier? sid, out string error)
    {
        sid = null;
        error = "";
        var value = (raw ?? "").Trim();
        sid = value.ToLowerInvariant() switch
        {
            "" or "current_user" => WindowsIdentity.GetCurrent().User,
            "administrators" => new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            "users" => new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            "authenticated_users" => new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            "everyone" => new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            "system" => new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            "trusted_installer" => new SecurityIdentifier(TrustedInstallerSid),
            _ => null
        };
        if (sid is not null)
        {
            return true;
        }

        if (value.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                sid = new SecurityIdentifier(value);
                return true;
            }
            catch (ArgumentException)
            {
                error = $"{value} is not a valid SID.";
                return false;
            }
        }

        if (!LocalUsersSafety.TryValidateAccountName(value, out var name, out var invalid))
        {
            error = invalid ?? "principal is invalid.";
            return false;
        }

        if (LocalUsersSafety.TryResolveAccountSid(name, out var account, out _) ||
            LocalUsersSafety.TryResolveGroupSid(name, out account, out _))
        {
            sid = account;
            return true;
        }

        error = Loc.Format("S.Tool.Acl.UnknownPrincipal", value);
        return false;
    }

    /// <summary>Имя учётной записи для ответа; не переводится — SID.</summary>
    public static string NameOf(IdentityReference identity)
    {
        try
        {
            return identity is NTAccount ? identity.Value : identity.Translate(typeof(NTAccount)).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return identity.Value;
        }
    }

    public static bool TryFileRights(string? raw, out FileSystemRights rights)
    {
        rights = (raw ?? "").Trim().ToLowerInvariant() switch
        {
            "read" => FileSystemRights.Read | FileSystemRights.Synchronize,
            "read_execute" => FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            "write" => FileSystemRights.Write | FileSystemRights.Synchronize,
            "modify" => FileSystemRights.Modify | FileSystemRights.Synchronize,
            "full" => FileSystemRights.FullControl,
            _ => 0
        };
        return rights != 0;
    }

    public static bool TryRegistryRights(string? raw, out RegistryRights rights)
    {
        rights = (raw ?? "").Trim().ToLowerInvariant() switch
        {
            "read" or "read_execute" => RegistryRights.ReadKey,
            "write" => RegistryRights.WriteKey,
            "modify" => RegistryRights.ReadKey | RegistryRights.WriteKey | RegistryRights.Delete,
            "full" => RegistryRights.FullControl,
            _ => 0
        };
        return rights != 0;
    }

    internal static string Canonical(string path) => SensitivePaths.Canonical(path).TrimEnd('\\');

    internal static bool Under(string path, string root) =>
        root.Length > 0 &&
        (path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^(CurrentControlSet|ControlSet\d{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex ControlSet();
}
