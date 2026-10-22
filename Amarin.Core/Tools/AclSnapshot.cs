using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Microsoft.Win32;

namespace Amarin.Tools;

/// <summary>Чтение и запись владельца и списка доступа файла, папки или раздела реестра.</summary>
/// <remarks>
/// Владелец и список доступа пишутся порознь: вернуть владельцем TrustedInstaller можно только с
/// привилегией восстановления, и откат без неё должен вернуть хотя бы права, а не отказать во всём.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class AclIo
{
    public const AccessControlSections Sections = AccessControlSections.Owner | AccessControlSections.Access;

    public static NativeObjectSecurity Read(AclTarget target)
    {
        if (target.IsRegistry)
        {
            using var key = OpenKey(target.Registry, RegistryRights.ReadPermissions);
            return key.GetAccessControl(Sections);
        }

        return target.IsDirectory
            ? new DirectoryInfo(target.Path).GetAccessControl(Sections)
            : new FileInfo(target.Path).GetAccessControl(Sections);
    }

    /// <summary>Пишет список доступа и признак наследования; владелец не трогается.</summary>
    public static void WriteAccess(AclTarget target, NativeObjectSecurity security)
    {
        if (target.IsRegistry)
        {
            using var key = OpenKey(target.Registry, RegistryRights.ReadPermissions | RegistryRights.ChangePermissions);
            key.SetAccessControl((RegistrySecurity)security);
            return;
        }

        if (target.IsDirectory)
        {
            new DirectoryInfo(target.Path).SetAccessControl((DirectorySecurity)security);
        }
        else
        {
            new FileInfo(target.Path).SetAccessControl((FileSecurity)security);
        }
    }

    public static void WriteOwner(AclTarget target, SecurityIdentifier owner)
    {
        NativeObjectSecurity security = target.IsRegistry ? new RegistrySecurity() : target.IsDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetOwner(owner);
        if (target.IsRegistry)
        {
            using var key = OpenKey(target.Registry, RegistryRights.TakeOwnership);
            key.SetAccessControl((RegistrySecurity)security);
        }
        else if (target.IsDirectory)
        {
            new DirectoryInfo(target.Path).SetAccessControl((DirectorySecurity)security);
        }
        else
        {
            new FileInfo(target.Path).SetAccessControl((FileSecurity)security);
        }
    }

    public static string Sddl(AclTarget target) => Read(target).GetSecurityDescriptorSddlForm(Sections);

    /// <summary>Возвращает права, потом владельца. Ошибку владельца отдаёт, права к этому времени уже на месте.</summary>
    public static void Restore(AclTarget target, string sddl)
    {
        NativeObjectSecurity access = target.IsRegistry ? new RegistrySecurity() : target.IsDirectory ? new DirectorySecurity() : new FileSecurity();
        access.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
        WriteAccess(target, access);

        var owner = new RawSecurityDescriptor(sddl).Owner;
        if (owner is not null && !owner.Equals(Read(target).GetOwner(typeof(SecurityIdentifier))))
        {
            WriteOwner(target, owner);
        }
    }

    /// <summary>
    /// Объект и всё внутри папки, без перехода по ссылкам, — не больше <paramref name="limit"/>.
    /// null — внутри больше, чем можно.
    /// </summary>
    public static List<AclTarget>? Tree(AclTarget root, int limit)
    {
        var items = new List<AclTarget> { root };
        if (root.IsRegistry || !root.IsDirectory)
        {
            return items;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };
        foreach (var entry in new DirectoryInfo(root.Path).EnumerateFileSystemInfos("*", options))
        {
            if (items.Count >= limit)
            {
                return null;
            }

            items.Add(new AclTarget(entry.FullName, IsRegistry: false, entry is DirectoryInfo, default));
        }

        return items;
    }

    private static RegistryKey OpenKey(RegistryPath path, RegistryRights rights)
    {
        using var root = RegistryStateIo.OpenRoot(path.Root) ?? throw new IOException($"Unknown registry root: {path.Root}");
        try
        {
            return (path.SubKey.Length == 0 ? null : root.OpenSubKey(path.SubKey, RegistryKeyPermissionCheck.ReadWriteSubTree, rights))
                   ?? throw new IOException($"{path.Canonical} does not exist.");
        }
        catch (System.Security.SecurityException ex)
        {
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
    }
}

/// <summary>
/// Привилегии владения и восстановления на время смены прав: у повышенного процесса они есть,
/// но выключены, и без них администратор не забрал бы владение у TrustedInstaller.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TokenPrivileges : IDisposable
{
    private static readonly string[] OwnershipPrivileges = ["SeTakeOwnershipPrivilege", "SeRestorePrivilege", "SeBackupPrivilege"];

    private readonly List<Luid> _enabled = [];

    private TokenPrivileges()
    {
    }

    /// <summary>Включает те из привилегий, что есть у процесса; остальные молча пропускает.</summary>
    public static TokenPrivileges ForOwnership()
    {
        var result = new TokenPrivileges();
        foreach (var name in OwnershipPrivileges)
        {
            if (LookupPrivilegeValue(null, name, out var luid) && Adjust(luid, enable: true, out var wasEnabled) && !wasEnabled)
            {
                result._enabled.Add(luid);
            }
        }

        return result;
    }

    public void Dispose()
    {
        foreach (var luid in _enabled)
        {
            _ = Adjust(luid, enable: false, out _);
        }

        _enabled.Clear();
    }

    private static bool Adjust(Luid luid, bool enable, out bool wasEnabled)
    {
        wasEnabled = false;
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return false;
        }

        try
        {
            var state = new TokenPrivilege { Count = 1, Luid = luid, Attributes = enable ? SePrivilegeEnabled : 0 };
            var previous = new TokenPrivilege();
            var size = (uint)Marshal.SizeOf<TokenPrivilege>();
            if (!AdjustTokenPrivileges(token, false, ref state, size, ref previous, out _) ||
                Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
            {
                return false;
            }

            // Прежнее состояние приходит только у привилегии, которую вызов поменял: пустое — она
            // уже была включена, и выключать её после работы нельзя.
            wasEnabled = previous.Count == 0 || (previous.Attributes & SePrivilegeEnabled) != 0;
            return true;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivilege
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivilege newState, uint length,
        ref TokenPrivilege previousState, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Права объекта до изменения — файлом в папке снимка, по SDDL на объект.</summary>
internal sealed class AclSnapshotFile
{
    public string Root { get; set; } = "";

    public bool Registry { get; set; }

    public List<AclSnapshotItem> Items { get; set; } = [];
}

internal sealed class AclSnapshotItem
{
    public string Path { get; set; } = "";

    public bool Directory { get; set; }

    public string Sddl { get; set; } = "";
}

/// <summary>Снимок прав перед изменением и их возврат при откате.</summary>
/// <remarks>
/// Файл снимка лежит в папке, куда пишет пользователь, и откат может идти от администратора.
/// Поэтому при возврате каждый путь обязан лежать внутри корня снимка, корень — проходить те же
/// запреты, что и правка (<see cref="PermissionTargets.TryRefuse"/>), а SDDL — разбираться:
/// подправленный файл не выдаст права на чужое место.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class AclSnapshot
{
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = AppJson.MaxDepth };

    /// <summary>Метка шага, снявшего объект вместе со всем вложенным.</summary>
    private const string TreeMark = "tree";

    /// <summary>
    /// Трогает ли вызов и вложенное. Права, выданные папке, вложенное наследует само, и откат
    /// корня возвращает их и там; владелец и сброс наследования ложатся на каждый объект отдельно.
    /// </summary>
    public static bool IsRecursive(JsonElement arguments, AclTarget target) =>
        target.IsDirectory && FileToolPaths.Flag(arguments, "recursive") &&
        DangerousActionGuard.ActionOf(arguments) is "set_owner" or "reset";

    public static bool IsTree(UndoStep step) => step.Values.Contains(TreeMark);

    /// <summary>Снимает права того, что изменит вызов; null — снимать нечего или не вышло.</summary>
    /// <param name="index">Который это снимок объекта за запрос: у каждого свой файл.</param>
    public static UndoStep? Capture(string snapshotDir, AclTarget target, bool recursive, int index)
    {
        using var privileges = TokenPrivileges.ForOwnership();
        var items = recursive ? AclIo.Tree(target, PermissionTargets.MaxRecursiveItems) : new List<AclTarget> { target };
        if (items is null)
        {
            // Больше предела — правка сама откажет; снимать нечего.
            return null;
        }

        var file = new AclSnapshotFile { Root = target.Path, Registry = target.IsRegistry };
        foreach (var item in items)
        {
            try
            {
                file.Items.Add(new AclSnapshotItem { Path = item.Path, Directory = item.IsDirectory, Sddl = AclIo.Sddl(item) });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
            {
                // Объект с нечитаемыми правами правка не тронет: PermissionsTool сперва читает
                // их тем же AclIo.Sddl и без него не меняет ничего.
            }
        }

        if (file.Items.Count == 0)
        {
            return null;
        }

        var name = "acl-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.Path.ToUpperInvariant())))[..12] +
                   "-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json";
        AppDataFile.WriteAtomic(Path.Combine(snapshotDir, name), JsonSerializer.Serialize(file, Json));
        return new UndoStep
        {
            Kind = UndoKind.Acl,
            Target = target.Path,
            File = name,
            Values = [file.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), recursive ? TreeMark : "object"]
        };
    }

    /// <summary>Возвращает права из файла снимка. Шаг, не прошедший проверку, не применяется вовсе.</summary>
    public static (bool Success, string? Error) Restore(UndoStep step, string snapshotDir)
    {
        if (Load(step, snapshotDir) is not { } file)
        {
            return (false, Loc.Get("S.Rollback.Undo.Invalid"));
        }

        var restored = 0;
        string? firstError = null;
        using var privileges = TokenPrivileges.ForOwnership();
        foreach (var item in file.Items)
        {
            var target = file.Registry
                ? (RegistryPath.TryParse(item.Path, out var key, requireSubKey: false) ? new AclTarget(key.Canonical, true, false, key) : null)
                : new AclTarget(item.Path, false, item.Directory, default);
            try
            {
                if (target is null)
                {
                    throw new IOException(item.Path);
                }

                AclIo.Restore(target, item.Sddl);
                restored++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException)
            {
                firstError ??= ex is UnauthorizedAccessException ? Loc.Get("S.Tool.NeedsAdmin") : ex.Message;
            }
        }

        return restored == file.Items.Count
            ? (true, null)
            : (false, Loc.Format("S.Rollback.Undo.AclPartial", restored, file.Items.Count, firstError ?? ""));
    }

    /// <summary>Файл шага, прошедший проверку: корень тот же, запреты пройдены, пути внутри корня, SDDL разбирается.</summary>
    internal static AclSnapshotFile? Load(UndoStep step, string snapshotDir)
    {
        if (step.Kind != UndoKind.Acl || UndoCommands.SnapshotFile(step, snapshotDir) is not { } path)
        {
            return null;
        }

        AclSnapshotFile? file;
        try
        {
            file = JsonSerializer.Deserialize<AclSnapshotFile>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }

        // У раздела реестра рекурсии нет: снимок с несколькими ключами подправлен руками.
        if (file is null || file.Items.Count == 0 || file.Items.Count > PermissionTargets.MaxRecursiveItems ||
            (file.Registry && file.Items.Count != 1) ||
            !string.Equals(file.Root, step.Target, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        AclTarget? root = null;
        if (file.Registry)
        {
            if (RegistryPath.TryParse(file.Root, out var key, requireSubKey: false))
            {
                root = new AclTarget(key.Canonical, true, false, key);
            }
        }
        else if (Path.IsPathFullyQualified(file.Root))
        {
            root = new AclTarget(Path.GetFullPath(file.Root), false, true, default);
        }

        if (root is null || PermissionTargets.TryRefuse(root, out _))
        {
            return null;
        }

        var rootKey = PermissionTargets.Canonical(root.Path);
        try
        {
            foreach (var item in file.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Path) || string.IsNullOrWhiteSpace(item.Sddl) ||
                    !PermissionTargets.Under(PermissionTargets.Canonical(file.Registry ? item.Path : Path.GetFullPath(item.Path)), rootKey))
                {
                    return null;
                }

                _ = new RawSecurityDescriptor(item.Sddl);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return file;
    }
}
