using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Права доступа: запреты на системные места, учётные записи без языка Windows, решение по
/// записям доступа, отчёт об отказе и снимок с возвратом — на временной папке теста.
/// </summary>
public sealed class PermissionsToolTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "amarin-acl-" + Guid.NewGuid().ToString("N"));
    private readonly string _snapshot;

    public PermissionsToolTests()
    {
        Directory.CreateDirectory(Path.Combine(_temp, "data", "inner"));
        File.WriteAllText(Path.Combine(_temp, "data", "inner", "file.txt"), "x");
        _snapshot = Path.Combine(_temp, "snapshot");
        Directory.CreateDirectory(_snapshot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>
    /// Прогон от администратора с полным токеном (раннер GitHub). Инструмент включает такому
    /// токену SeBackupPrivilege и SeRestorePrivilege, и запрет на чтение прав его не держит, а
    /// новый файл получает владельцем группу администраторов, — проверки, рассчитанные на
    /// обычную учётку, там видят другую систему.
    /// </summary>
    private static bool Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [Fact]
    public void System_folders_and_roots_are_protected_but_their_subfolders_are_not()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        Assert.True(PermissionTargets.IsProtectedFolder(windows));
        Assert.True(PermissionTargets.IsProtectedFolder(Path.Combine(windows, "System32", "drivers")));
        Assert.True(PermissionTargets.IsProtectedFolder(Path.GetPathRoot(windows)!));
        Assert.True(PermissionTargets.IsProtectedFolder(programs));
        Assert.True(PermissionTargets.IsProtectedFolder(profile));
        Assert.True(PermissionTargets.IsProtectedFolder(Path.GetDirectoryName(profile)!));
        Assert.True(PermissionTargets.IsProtectedFolder(Path.Combine(Path.GetPathRoot(windows)!, "System Volume Information", "x")));

        Assert.False(PermissionTargets.IsProtectedFolder(Path.Combine(programs, "Vendor", "App")));
        Assert.False(PermissionTargets.IsProtectedFolder(Path.Combine(profile, "Documents")));
        Assert.False(PermissionTargets.IsProtectedFolder(_temp));
    }

    [Theory]
    [InlineData(@"HKLM", true)]
    [InlineData(@"HKLM\SOFTWARE", true)]
    [InlineData(@"HKCU\Software", true)]
    [InlineData(@"HKLM\SAM\SAM\Domains", true)]
    [InlineData(@"HKLM\SECURITY\Policy", true)]
    [InlineData(@"HKLM\BCD00000000\Objects", true)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services", true)]
    [InlineData(@"HKLM\SYSTEM\ControlSet001\Control", true)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\Vendor\Parameters", false)]
    [InlineData(@"HKLM\SOFTWARE\Vendor\App", false)]
    [InlineData(@"HKCU\Software\Vendor", false)]
    public void Registry_hive_roots_and_system_keys_are_protected(string key, bool protectedKey)
    {
        Assert.True(RegistryPath.TryParse(key, out var path, requireSubKey: false));
        Assert.Equal(protectedKey, PermissionTargets.IsProtectedKey(path));
    }

    [Theory]
    [InlineData("users", "S-1-5-32-545")]
    [InlineData("administrators", "S-1-5-32-544")]
    [InlineData("everyone", "S-1-1-0")]
    [InlineData("system", "S-1-5-18")]
    [InlineData("authenticated_users", "S-1-5-11")]
    [InlineData("trusted_installer", PermissionTargets.TrustedInstallerSid)]
    [InlineData("S-1-5-32-545", "S-1-5-32-545")]
    public void Principals_resolve_through_well_known_sids_whatever_the_windows_language(string principal, string sid)
    {
        Assert.True(PermissionTargets.TryResolvePrincipal(principal, out var resolved, out var error), error);
        Assert.Equal(sid, resolved.Value);
    }

    [Theory]
    [InlineData("S-1-not-a-sid")]
    [InlineData("no_such_account_9f1c2e")]
    [InlineData("bad;name")]
    public void An_unknown_principal_is_refused_with_a_reason(string principal)
    {
        Assert.False(PermissionTargets.TryResolvePrincipal(principal, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Rules_decide_in_order_and_deny_only_groups_meet_only_denials()
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        AceView[] aces =
        [
            new(user, "user", 0x0002, Allow: false, Inherited: false, InheritOnly: false),
            new(admins, "Administrators", 0x1F01FF, Allow: true, Inherited: true, InheritOnly: false),
            new(Users, "Users", 0x0001, Allow: true, Inherited: true, InheritOnly: false),
            new(Users, "Users", 0x0002, Allow: true, Inherited: true, InheritOnly: true)
        ];
        var enabled = new HashSet<SecurityIdentifier> { user, Users };
        var denyOnly = new HashSet<SecurityIdentifier> { admins };

        Assert.Equal((false, "user"), Pair(AccessExplain.Decide(aces, 0x0002, enabled, denyOnly)));
        Assert.Equal((true, "Users"), Pair(AccessExplain.Decide(aces, 0x0001, enabled, denyOnly)));

        // Администраторы «только для запрета» не дают ничего, а наследуемое-только-детям не считается.
        Assert.Null(AccessExplain.Decide(aces, 0x10000, enabled, denyOnly).Allowed);

        static (bool?, string?) Pair((bool? Allowed, AceView? By) decision) => (decision.Allowed, decision.By?.Who);
    }

    [Fact]
    public void Generic_rights_count_as_the_specific_ones()
    {
        Assert.Equal(0x1F01FF, AccessExplain.MapGeneric(0x10000000, registry: false) & 0x1F01FF);
        Assert.NotEqual(0, AccessExplain.MapGeneric(unchecked((int)0x80000000), registry: false) & 0x0001);
        Assert.Equal(0xF003F, AccessExplain.MapGeneric(0x10000000, registry: true) & 0xF003F);
    }

    [Fact]
    public async Task Show_and_explain_read_a_real_folder()
    {
        var tool = new PermissionsTool();
        var folder = Path.Combine(_temp, "data");

        var show = await tool.ExecuteAsync(Args(new { action = "show", path = folder }));
        Assert.True(show.Success, show.Output);
        Assert.Contains("Owner:", show.Output, StringComparison.Ordinal);

        var explain = await tool.ExecuteAsync(Args(new { action = "explain", path = folder }));
        Assert.True(explain.Success, explain.Output);
        Assert.Contains("read:", explain.Output, StringComparison.Ordinal);
        Assert.Contains("Likely causes", explain.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_open_without_sharing_is_explained_as_in_use_not_as_denied()
    {
        var file = Path.Combine(_temp, "data", "inner", "file.txt");
        using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var report = AccessExplain.Report(new AclTarget(file, IsRegistry: false, IsDirectory: false, default));

        Assert.Contains("IN USE", report, StringComparison.Ordinal);
        Assert.Contains("file_locks", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_grant_goes_into_the_snapshot_and_the_rollback_brings_the_permissions_back()
    {
        if (Elevated())
        {
            return;
        }

        var folder = Path.Combine(_temp, "data");
        Assert.True(PermissionTargets.TryParse(folder, out var target, out _));
        var before = AclIo.Sddl(target);

        var step = AclSnapshot.Capture(_snapshot, target, recursive: false, index: 0);
        Assert.NotNull(step);

        var granted = await new PermissionsTool().ExecuteAsync(Args(new { action = "grant", path = folder, principal = "users", rights = "modify" }));
        Assert.True(granted.Success, granted.Output);
        Assert.NotEqual(before, AclIo.Sddl(target));

        var (restored, error) = AclSnapshot.Restore(step, _snapshot);
        Assert.True(restored, error);
        Assert.Equal(before, AclIo.Sddl(target));
    }

    [Fact]
    public void A_recursive_reset_snapshots_everything_inside()
    {
        var folder = Path.Combine(_temp, "data");
        Assert.True(PermissionTargets.TryParse(folder, out var target, out _));
        var step = AclSnapshot.Capture(_snapshot, target, recursive: true, index: 0);

        Assert.NotNull(step);
        Assert.True(AclSnapshot.IsTree(step));
        Assert.Equal("3", step.Values[0]);
    }

    [Fact]
    public void A_tampered_snapshot_that_points_outside_its_root_is_not_applied()
    {
        var folder = Path.Combine(_temp, "data");
        Assert.True(PermissionTargets.TryParse(folder, out var target, out _));
        var step = AclSnapshot.Capture(_snapshot, target, recursive: false, index: 0)!;

        var file = Path.Combine(_snapshot, step.File!);
        var tampered = File.ReadAllText(file).Replace(JsonEncodedText.Encode(folder).ToString(), JsonEncodedText.Encode(_temp).ToString(), StringComparison.Ordinal);
        File.WriteAllText(file, tampered.Replace("\"Root\":\"" + JsonEncodedText.Encode(_temp), "\"Root\":\"" + JsonEncodedText.Encode(folder), StringComparison.Ordinal));

        Assert.Null(AclSnapshot.Load(step, _snapshot));
        Assert.False(AclSnapshot.Restore(step, _snapshot).Success);
    }

    [Fact]
    public async Task The_tool_refuses_system_places_even_when_called_past_the_gate()
    {
        var result = await new PermissionsTool().ExecuteAsync(Args(new
        {
            action = "grant",
            path = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            principal = "everyone",
            rights = "full"
        }));

        Assert.False(result.Success);
        Assert.Contains("ЗАПРЕЩЕНО", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_owner_of_an_object_whose_permissions_cannot_be_read_is_not_changed()
    {
        // Снимок не читает такие права, а владельца пишут и без чтения: раньше владение менялось
        // без снимка, и ответ уверял, что прежнее сохранено.
        if (Elevated())
        {
            // Право резервного копирования читает такие права, и снимок честно делается.
            return;
        }

        var file = Path.Combine(_temp, "data", "inner", "file.txt");
        var info = new FileInfo(file);
        var locked = info.GetAccessControl(AccessControlSections.Access);
        FileSystemAccessRule[] denials =
        [
            new(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadPermissions, AccessControlType.Deny),
            // Без OWNER RIGHTS владелец читал бы права неявно.
            new(new SecurityIdentifier("S-1-3-4"), FileSystemRights.ReadPermissions, AccessControlType.Deny)
        ];
        foreach (var denial in denials)
        {
            locked.AddAccessRule(denial);
        }

        info.SetAccessControl(locked);
        try
        {
            // Текущий пользователь — владелец, и запись владельца прошла бы у любого: прежний код
            // ответил бы успехом без снимка.
            var result = await new PermissionsTool().ExecuteAsync(Args(new { action = "set_owner", path = file, principal = "current_user" }));

            Assert.False(result.Success, result.Output);
            Assert.Equal(Loc.Format("S.Tool.Acl.Unreadable", file), result.Output);
        }
        finally
        {
            // Список доступа без чтения прав не переписать, а удалить файл право есть.
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Grant_and_revoke_need_a_named_principal()
    {
        var folder = Path.Combine(_temp, "data");
        var result = await new PermissionsTool().ExecuteAsync(Args(new { action = "grant", path = folder, rights = "read" }));

        Assert.False(result.Success);
        Assert.Contains("principal", result.Output, StringComparison.Ordinal);
    }
}
