using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Владелец и права файла, папки или раздела реестра: показать, объяснить отказ доступа и
/// поменять со снимком для отката.
/// </summary>
/// <remarks>
/// Правка идёт API Windows, а не takeown и icacls: у тех свой текст на языке системы и своя
/// рекурсия без предела, а снимок для отката (<see cref="AclSnapshot"/>) должен знать ровно те
/// объекты, которые правка тронула.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PermissionsTool : ITool
{
    public const string ToolName = "permissions";

    public string Name => ToolName;

    public string Description =>
        "Owner and permissions of a file, folder or registry key. show lists them. explain tells why access is denied " +
        "to this program: the owner, the entries that apply to this account and its groups (UAC included), a test " +
        "open of the object that changes nothing, and the likely causes. set_owner, grant, revoke and reset change " +
        "them; they need user confirmation, and the previous owner and permissions go to the rollback snapshot. " +
        "The Windows folder, drive roots, the roots of program and profile folders and registry hive roots are refused.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["show", "explain", "set_owner", "grant", "revoke", "reset"],
              "description": "Permissions operation"
            },
            "path": {
              "type": "string",
              "description": "Full path of a file or folder, or a registry key written as HKLM\\..., HKCU\\..."
            },
            "principal": {
              "type": "string",
              "description": "Account for set_owner, grant and revoke: current_user, administrators, users, authenticated_users, everyone, system, trusted_installer, an account or group name, or a SID. set_owner defaults to current_user"
            },
            "rights": {
              "type": "string",
              "enum": ["read", "read_execute", "write", "modify", "full"],
              "description": "grant: read; read_execute (read and run); write (write without read); modify (read, write and delete); full (also permissions and owner). On a folder the right is inherited by everything inside"
            },
            "recursive": {
              "type": "boolean",
              "description": "set_owner and reset on a folder: apply to everything inside as well (links are not followed)"
            }
          },
          "required": ["action", "path"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        Task.Run(() => Execute(arguments), cancellationToken);

    private static ToolResult Execute(JsonElement arguments)
    {
        var action = DangerousActionGuard.ActionOf(arguments);
        if (!PermissionTargets.TryParse(FileToolPaths.String(arguments, "path"), out var target, out var error))
        {
            return ToolResult.Fail(error);
        }

        try
        {
            if (!PermissionTargets.IsWrite(action))
            {
                return action switch
                {
                    "show" => Show(target),
                    "explain" => ToolResult.Ok(AccessExplain.Report(target)),
                    _ => ToolResult.Fail($"Unknown action: {action}")
                };
            }

            // Второй рубеж после шлюза.
            return PermissionTargets.TryRefuse(target, out var refusal)
                ? ToolResult.Fail(refusal)
                : Change(action, target, arguments);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException)
        {
            return ToolResult.Fail(Loc.Get("S.Tool.NeedsAdmin"));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            return ToolResult.Fail($"Permissions error: {ex.Message}");
        }
    }

    private static ToolResult Show(AclTarget target)
    {
        var security = AclIo.Read(target);
        var sb = new StringBuilder();
        sb.AppendLine($"Object: {target.Path} ({(target.IsRegistry ? "registry key" : target.IsDirectory ? "folder" : "file")})");
        sb.AppendLine($"Owner: {Who(security.GetOwner(typeof(SecurityIdentifier)))}");
        sb.AppendLine($"Inheritance from parent: {(security.AreAccessRulesProtected ? "off" : "on")}");

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<AuthorizationRule>().ToList();
        sb.AppendLine($"Entries: {rules.Count}");
        foreach (var rule in rules)
        {
            // Общие биты (GENERIC_ALL и подобные) у наследуемых записей перечисление не называет —
            // без перевода в конкретные права модель видела бы «268435456».
            var (rights, allow) = rule switch
            {
                FileSystemAccessRule file => (((FileSystemRights)AccessExplain.MapGeneric((int)file.FileSystemRights, registry: false)).ToString(),
                    file.AccessControlType == AccessControlType.Allow),
                RegistryAccessRule key => (((RegistryRights)AccessExplain.MapGeneric((int)key.RegistryRights, registry: true)).ToString(),
                    key.AccessControlType == AccessControlType.Allow),
                _ => ("", false)
            };
            sb.AppendLine($"  {(allow ? "Allow" : "Deny ")}  {Who(rule.IdentityReference)}  {rights}  " +
                          $"[{(rule.IsInherited ? "inherited" : "explicit")}; {AppliesTo(rule, target)}]");
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult Change(string action, AclTarget target, JsonElement arguments)
    {
        var recursive = FileToolPaths.Flag(arguments, "recursive") && target.IsDirectory && action is "set_owner" or "reset";
        var items = recursive ? AclIo.Tree(target, PermissionTargets.MaxRecursiveItems) : new List<AclTarget> { target };
        if (items is null)
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Acl.TooMany", PermissionTargets.MaxRecursiveItems));
        }

        // Владельцем по умолчанию становится текущий пользователь; кому дать или у кого забрать
        // права, называет только модель — «текущий» по умолчанию тут был бы догадкой.
        var named = FileToolPaths.String(arguments, "principal");
        if (action is "grant" or "revoke" && string.IsNullOrWhiteSpace(named))
        {
            return ToolResult.Fail("principal is required for grant and revoke.");
        }

        SecurityIdentifier? principal = null;
        if (action != "reset" && !PermissionTargets.TryResolvePrincipal(named, out principal, out var principalError))
        {
            return ToolResult.Fail(principalError);
        }

        using var privileges = TokenPrivileges.ForOwnership();

        // Меняется только то, что снимок для отката смог прочитать (AclSnapshot.Capture читает так
        // же). Владельца пишут и без чтения, и раньше владение забиралось у объекта с нечитаемыми
        // правами без снимка, а ответ уверял, что прежнее сохранено.
        if (!Readable(target))
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Acl.Unreadable", target.Path));
        }

        string done;
        switch (action)
        {
            case "set_owner":
                done = ForEach(items, item =>
                {
                    _ = AclIo.Sddl(item);
                    AclIo.WriteOwner(item, principal!);
                }, out var ownerFailed);
                return Result($"Owner set to {Who(principal!)} on {done}.", ownerFailed);

            case "grant":
                var granted = Grant(target, principal!, FileToolPaths.String(arguments, "rights"));
                return granted ?? Result($"{Who(principal!)} granted {FileToolPaths.String(arguments, "rights")?.Trim()} on {target.Path}" +
                                         (target.IsDirectory || target.IsRegistry ? " and what inherits from it." : "."), null);

            case "revoke":
                var security = AclIo.Read(target);
                var inherited = security.GetAccessRules(false, true, typeof(SecurityIdentifier))
                    .Cast<AuthorizationRule>().Any(rule => principal!.Equals(rule.IdentityReference));
                security.PurgeAccessRules(principal!);
                AclIo.WriteAccess(target, security);
                return Result($"Explicit entries of {Who(principal!)} removed from {target.Path}." +
                              (inherited ? " Inherited entries remain: they come from the parent and change there." : ""), null);

            default:
                done = ForEach(items, Reset, out var resetFailed);
                return Result($"Inheritance turned on and explicit entries removed on {done}.", resetFailed);
        }
    }

    /// <returns>null — получилось; иначе отказ.</returns>
    private static ToolResult? Grant(AclTarget target, SecurityIdentifier principal, string? rights)
    {
        var security = AclIo.Read(target);
        if (target.IsRegistry)
        {
            if (!PermissionTargets.TryRegistryRights(rights, out var keyRights))
            {
                return ToolResult.Fail("rights must be read, read_execute, write, modify or full. Do not retry with the same value.");
            }

            ((RegistrySecurity)security).AddAccessRule(new RegistryAccessRule(
                principal, keyRights, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        else
        {
            if (!PermissionTargets.TryFileRights(rights, out var fileRights))
            {
                return ToolResult.Fail("rights must be read, read_execute, write, modify or full. Do not retry with the same value.");
            }

            var inheritance = target.IsDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
            ((FileSystemSecurity)security).AddAccessRule(new FileSystemAccessRule(
                principal, fileRights, inheritance, PropagationFlags.None, AccessControlType.Allow));
        }

        AclIo.WriteAccess(target, security);
        return null;
    }

    private static void Reset(AclTarget item)
    {
        var security = AclIo.Read(item);
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        foreach (var identity in security.GetAccessRules(true, false, typeof(SecurityIdentifier))
                     .Cast<AuthorizationRule>().Select(rule => rule.IdentityReference).Distinct().ToList())
        {
            security.PurgeAccessRules(identity);
        }

        AclIo.WriteAccess(item, security);
    }

    /// <summary>Применяет ко всем; сбой одного не останавливает остальные. Итог — «N из M» или путь.</summary>
    private static string ForEach(IReadOnlyList<AclTarget> items, Action<AclTarget> change, out string? failure)
    {
        failure = null;
        var done = 0;
        foreach (var item in items)
        {
            try
            {
                change(item);
                done++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException or IOException or InvalidOperationException)
            {
                // Корень обязан получиться: без него остальное смысла не имеет.
                if (ReferenceEquals(item, items[0]))
                {
                    throw;
                }

                failure ??= $"{item.Path}: {(ex is UnauthorizedAccessException or PrivilegeNotHeldException ? "access denied" : ex.Message)}";
            }
        }

        return items.Count == 1 ? items[0].Path : $"{done} of {items.Count} objects under {items[0].Path}";
    }

    private static bool Readable(AclTarget target)
    {
        try
        {
            _ = AclIo.Sddl(target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException)
        {
            return false;
        }
    }

    private static ToolResult Result(string line, string? failure) =>
        ToolResult.Ok(line + (failure is null ? "" : $"\nNot changed, first one: {failure}") + "\n" + Loc.Get("S.Tool.Acl.Changed"));

    private static string Who(IdentityReference? identity) => identity is null ? "unknown" : PermissionTargets.NameOf(identity);

    private static string AppliesTo(AuthorizationRule rule, AclTarget target)
    {
        if (!target.IsDirectory && !target.IsRegistry)
        {
            return "this file";
        }

        var children = (rule.InheritanceFlags & InheritanceFlags.ContainerInherit) != 0
            ? (rule.InheritanceFlags & InheritanceFlags.ObjectInherit) != 0 ? "subfolders and files" : "subfolders"
            : (rule.InheritanceFlags & InheritanceFlags.ObjectInherit) != 0 ? "files" : "";
        if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
        {
            return children.Length == 0 ? "nothing" : "only " + children;
        }

        return children.Length == 0 ? "this object only" : "this object, " + children;
    }
}
