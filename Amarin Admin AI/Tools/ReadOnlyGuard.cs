using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amarin.Tools;

public static partial class ReadOnlyGuard
{
    [GeneratedRegex(
        @"\b(?:Set-|New-|Remove-|Add-|Clear-|Start-Service\b|Stop-Service\b|Restart-Service\b|Set-Service\b|Stop-Process\b|Restart-Computer\b|Stop-Computer\b|Invoke-WebRequest\b.*-OutFile|Invoke-RestMethod\b.*-OutFile|reg\s+add\b|reg\s+delete\b|netsh\s+advfirewall\b|bcdedit\b|Format-|Disable-|Enable-|Install-|Uninstall-|Update-|Rename-|Move-Item\b|Copy-Item\b.*-Force|Out-File\b|Set-Content\b|Add-Content\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellMutationPattern();

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
            "restore_point" => IsRestorePointRead(arguments),
            "disk_management" => IsDiskManagementRead(arguments),
            "disk_space" => IsDiskSpaceRead(arguments),
            "software_inventory" => IsSoftwareInventoryRead(arguments),
            "firewall_rules" => IsFirewallRulesRead(arguments),
            "windows_features" => IsWindowsFeaturesRead(arguments),
            "local_users" => IsLocalUsersRead(arguments),
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

    private static bool IsRestorePointRead(JsonElement arguments) =>
        !arguments.TryGetProperty("action", out var action) ||
        action.GetString() is "list" or "status";

    private static bool IsDiskManagementRead(JsonElement arguments) =>
        !arguments.TryGetProperty("action", out var action) ||
        action.GetString() is "list_disks" or "list_volumes" or "smart_status"
            or "chkdsk_scan" or "bitlocker_status";

    private static bool IsDiskSpaceRead(JsonElement arguments) =>
        !arguments.TryGetProperty("action", out var action) ||
        action.GetString() is "analyze" or "largest_items";

    private static bool IsSoftwareInventoryRead(JsonElement arguments) =>
        !arguments.TryGetProperty("action", out var action) ||
        action.GetString() is "list_installed" or "search" or "list_upgrades";

    private static bool IsFirewallRulesRead(JsonElement arguments) =>
        !arguments.TryGetProperty("action", out var action) ||
        action.GetString() is "list" or "get";

    private static bool IsWindowsFeaturesRead(JsonElement arguments) =>
        !arguments.TryGetProperty("action", out var action) ||
        action.GetString() is "list" or "get";

    private static bool IsLocalUsersRead(JsonElement arguments) =>
        !arguments.TryGetProperty("action", out var action) ||
        action.GetString() is "list_users" or "list_groups" or "group_members" or "user_details";

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

        return !PowerShellMutationPattern().IsMatch(command);
    }
}