using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amarin.Tools;

public static class ReadOnlyGuard
{
    private static readonly string[] PowerShellMutationPatterns =
    [
        @"\bSet-",
        @"\bNew-",
        @"\bRemove-",
        @"\bAdd-",
        @"\bClear-",
        @"\bStart-Service\b",
        @"\bStop-Service\b",
        @"\bRestart-Service\b",
        @"\bSet-Service\b",
        @"\bStop-Process\b",
        @"\bRestart-Computer\b",
        @"\bStop-Computer\b",
        @"\bInvoke-WebRequest\b.*-OutFile",
        @"\bInvoke-RestMethod\b.*-OutFile",
        @"\breg\s+add\b",
        @"\breg\s+delete\b",
        @"\bnetsh\s+advfirewall\b",
        @"\bbcdedit\b",
        @"\bFormat-",
        @"\bDisable-",
        @"\bEnable-",
        @"\bInstall-",
        @"\bUninstall-",
        @"\bUpdate-",
        @"\bRename-",
        @"\bMove-Item\b",
        @"\bCopy-Item\b.*-Force",
        @"\bOut-File\b",
        @"\bSet-Content\b",
        @"\bAdd-Content\b"
    ];

    public static bool IsToolAllowed(string toolName, JsonElement arguments)
    {
        if (toolName.Equals(AskUserTool.ToolName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return toolName.ToLowerInvariant() switch
        {
            "download_file" => false,
            "registry" => IsRegistryRead(arguments),
            "filesystem" => IsFilesystemRead(arguments),
            "windows_service" => IsServiceRead(arguments),
            "run_powershell" => IsPowerShellReadOnly(arguments),
            "windows_process" => IsProcessRead(arguments),
            "scheduled_task" => IsTaskRead(arguments),
            "network" => IsNetworkRead(arguments),
            "virtualization" => IsVirtualizationRead(arguments),
            "change_rollback" => IsRollbackAllowed(arguments),
            "system_repair" => IsSystemRepairRead(arguments),
            _ => true
        };
    }

    public static string BlockedMessage(string toolName) =>
        $"Режим /readonly: инструмент «{toolName}» недоступен для записи или опасных операций.";

    private static bool IsRegistryRead(JsonElement arguments) =>
        !arguments.TryGetProperty("action", out var action) ||
        action.GetString() is "read" or "list_subkeys";

    private static bool IsFilesystemRead(JsonElement arguments) =>
        !arguments.TryGetProperty("action", out var action) ||
        action.GetString() is "read" or "list" or "exists";

    private static bool IsServiceRead(JsonElement arguments) =>
        !arguments.TryGetProperty("action", out var action) ||
        action.GetString() is "list" or "status" or "dependencies";

    private static bool IsProcessRead(JsonElement arguments) =>
        !arguments.TryGetProperty("action", out var action) ||
        action.GetString() is "list" or "top";

    private static bool IsTaskRead(JsonElement arguments) =>
        !arguments.TryGetProperty("action", out var action) ||
        action.GetString() is "list" or "info";

    private static bool IsNetworkRead(JsonElement arguments) =>
        arguments.TryGetProperty("action", out var action) &&
        action.GetString() is "adapters" or "dns" or "ping" or "connections" or "firewall_rules";

    private static bool IsVirtualizationRead(JsonElement arguments) =>
        !arguments.TryGetProperty("action", out var action) ||
        action.GetString() is
            "list_hyperv_vms" or "hyperv_vm_status" or "list_docker_containers" or "docker_status";

    private static bool IsRollbackAllowed(JsonElement arguments) =>
        !arguments.TryGetProperty("action", out var action) ||
        action.GetString() is "list_snapshots" or "compare" or "snapshot_info";

    private static bool IsSystemRepairRead(JsonElement arguments) =>
        !arguments.TryGetProperty("action", out var action) ||
        action.GetString() is "status_sfc" or "status_dism";

    private static bool IsPowerShellReadOnly(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("command", out var commandProp))
        {
            return false;
        }

        var command = commandProp.GetString() ?? string.Empty;

        if (DeletionGuard.PowerShellAttemptsDeletion(command))
        {
            return false;
        }

        if (DangerousActionGuard.RequiresConfirmation("run_powershell", arguments))
        {
            return false;
        }

        return !PowerShellMutationPatterns.Any(p =>
            Regex.IsMatch(command, p, RegexOptions.IgnoreCase));
    }
}