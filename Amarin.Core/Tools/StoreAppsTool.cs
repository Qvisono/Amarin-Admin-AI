using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Приложения Microsoft Store: список с отметками «предустановлено» и «неудаляемое», удаление
/// ненужного и сброс приложения.
/// </summary>
/// <remarks>
/// Какие приложения лишние, инструмент не решает: списка «мусора» у него нет. Он показывает, что
/// пришло с Windows и что удалить нельзя, а решают модель и человек.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StoreAppsTool : ITool
{
    public const string ToolName = "store_apps";

    public string Name => ToolName;

    public string Description =>
        "Microsoft Store (AppX/MSIX) apps of the current user. list shows name, version, signature kind and flags: " +
        "framework, non-removable, preinstalled (comes with Windows for every new account). remove uninstalls an app " +
        "for the current user; for_new_users also removes its preinstalled copy (administrator rights). reset clears " +
        "the app's data and settings, as Settings > Apps > Reset does. Changes need user confirmation; frameworks, " +
        "system packages and the Store itself are never removed.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "remove", "reset"],
              "description": "Store apps operation"
            },
            "name": {
              "type": "string",
              "description": "remove and reset: the package Name from list (a PackageFullName works too)"
            },
            "filter": {
              "type": "string",
              "description": "list: part of the package name"
            },
            "include_frameworks": {
              "type": "boolean",
              "description": "list: also show framework packages (hidden by default)"
            },
            "for_new_users": {
              "type": "boolean",
              "description": "remove: also remove the preinstalled copy so it is not installed for new accounts"
            },
            "max_items": {
              "type": "integer",
              "description": "list: rows to show, 10-300 (default 80)"
            }
          },
          "required": ["action"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var action = DangerousActionGuard.ActionOf(arguments);
        if (action == "list")
        {
            var filter = FileToolPaths.String(arguments, "filter")?.Trim() ?? "";
            if (filter.Length > 0 && !StoreAppsCommands.IsPackageName(filter))
            {
                return ToolResult.Fail("filter must be part of a package name: letters, digits, dots, dashes, underscores.");
            }

            var max = Math.Clamp(FileToolPaths.Int(arguments, "max_items") ?? 80, 10, 300);
            var listed = await PowerShellHelper.RunAsync(
                StoreAppsCommands.ListScript(filter, FileToolPaths.Flag(arguments, "include_frameworks"), max), 120, cancellationToken)
                .ConfigureAwait(false);

            // Таблица PowerShell добивает строки пробелами до ширины: модели они ни к чему, а предел
            // ответа съели бы на трети списка.
            return listed.Success
                ? ToolResult.Ok(string.Join('\n', PowerShellHelper.ExtractStdout(listed.Output)
                    .ReplaceLineEndings("\n").Split('\n').Select(line => line.TrimEnd()).Where((line, index) => index == 0 || line.Length > 0)))
                : listed;
        }

        if (action is not ("remove" or "reset"))
        {
            return ToolResult.Fail($"Unknown action: {action}");
        }

        var name = FileToolPaths.String(arguments, "name")?.Trim();
        if (!StoreAppsCommands.IsPackageName(name))
        {
            return ToolResult.Fail("name is required: the package Name or PackageFullName from list.");
        }

        if (TryGetHardBlockReason(arguments, out var refusal))
        {
            return ToolResult.Fail(refusal);
        }

        var forNewUsers = action == "remove" && FileToolPaths.Flag(arguments, "for_new_users");
        if (forNewUsers && !IsElevated())
        {
            return ToolResult.Fail(Loc.Get("S.Tool.NeedsAdmin"));
        }

        var script = action == "remove" ? StoreAppsCommands.RemoveScript(name, forNewUsers) : StoreAppsCommands.ResetScript(name);
        var result = await PowerShellHelper.RunAsync(script, 300, cancellationToken).ConfigureAwait(false);
        var text = PowerShellHelper.ExtractStdout(result.Output);
        if (text.Contains("NOT_FOUND", StringComparison.Ordinal))
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Store.NotFound", name));
        }

        if (text.Contains("PROTECTED", StringComparison.Ordinal))
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Store.Protected", name));
        }

        if (text.Contains("NO_RESET", StringComparison.Ordinal))
        {
            return ToolResult.Fail(Loc.Get("S.Tool.Store.NoReset"));
        }

        if (!result.Success)
        {
            return PowerShellHelper.IsAccessDenied(result.Output) ? ToolResult.Fail(Loc.Get("S.Tool.NeedsAdmin")) : result;
        }

        return ToolResult.Ok(action == "remove"
            ? text.Replace("REMOVED", "Removed:", StringComparison.Ordinal).Replace("DEPROVISIONED", "Preinstalled copies removed:", StringComparison.Ordinal) +
              "\n" + Loc.Get("S.Tool.Store.Removed")
            : text.Replace("RESET", "Reset:", StringComparison.Ordinal));
    }

    /// <summary>Запрет без вопроса: Store, «Установщик приложений» и их соседи по списку. Его зовёт шлюз.</summary>
    internal static bool TryGetHardBlockReason(JsonElement arguments, out string reason)
    {
        reason = "";
        if (DangerousActionGuard.ActionOf(arguments) != "remove" ||
            FileToolPaths.String(arguments, "name")?.Trim() is not { Length: > 0 } name ||
            !StoreAppsCommands.IsProtected(name))
        {
            return false;
        }

        reason = Loc.Format("S.Tool.Store.Protected", name);
        return true;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
