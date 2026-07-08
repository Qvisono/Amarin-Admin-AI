using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amarin.Tools;

public enum DangerousRiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

public sealed record DangerousActionInfo(
    string ToolName,
    string ChangeSummary,
    string Details,
    DangerousRiskLevel RiskLevel);

internal static class DangerousActionGuard
{
    private static readonly string[] PowerShellDangerousPatterns =
    [
        @"\bSet-ItemProperty\b",
        @"\bNew-ItemProperty\b",
        @"\bRemove-ItemProperty\b",
        @"\bStart-Service\b",
        @"\bStop-Service\b",
        @"\bRestart-Service\b",
        @"\bSet-Service\b",
        @"\bbcdedit\b",
        @"\breagentc\b",
        @"\bnetsh\s+advfirewall\b",
        @"\breg\s+add\b",
        @"\breg\s+delete\b",
        @"\bDisable-WindowsOptionalFeature\b",
        @"\bEnable-WindowsOptionalFeature\b",
        @"\bRestart-Computer\b",
        @"\bStop-Computer\b",
        @"\bFormat-Volume\b",
        @"\bClear-Disk\b",
        @"\bStop-Process\b",
        @"\bdocker\s+(stop|kill|rm|remove)\b",
        @"\bStop-VM\b",
        @"\bSuspend-VM\b",
        @"\bCheckpoint-VM\b"
    ];

    public static bool RequiresConfirmation(string toolName, JsonElement arguments)
    {
        if (toolName.Equals(AskUserTool.ToolName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return toolName.ToLowerInvariant() switch
        {
            "registry" => IsRegistryWrite(arguments),
            "windows_service" => IsServiceControl(arguments),
            "filesystem" => IsFileWrite(arguments),
            "run_powershell" => PowerShellAttemptsDanger(arguments),
            "windows_process" => IsProcessStop(arguments),
            "scheduled_task" => IsTaskMutation(arguments),
            "network" => IsNetworkMutation(arguments),
            "virtualization" => IsVirtualizationMutation(arguments),
            "download_file" => true,
            "change_rollback" => IsRollbackRestore(arguments),
            "system_repair" => IsSystemRepairRun(arguments),
            _ => false
        };
    }

    /// <summary>
    /// System snapshot for /undo — only when rollback can restore meaningful state.
    /// Downloads, file writes, and process stops are confirmed but not snapshotted.
    /// </summary>
    public static bool RequiresUndoSnapshot(string toolName, JsonElement arguments) =>
        toolName.ToLowerInvariant() switch
        {
            "registry" => IsRegistryWrite(arguments),
            "windows_service" => IsServiceControl(arguments),
            "scheduled_task" => IsTaskMutation(arguments),
            "network" => IsNetworkMutation(arguments),
            "change_rollback" => IsRollbackRestore(arguments),
            "system_repair" => IsSystemRepairRun(arguments),
            "run_powershell" => PowerShellAttemptsDanger(arguments),
            _ => false
        };

    public static string Describe(string toolName, JsonElement arguments) =>
        DescribeDetailed(toolName, arguments).Details;

    public static DangerousActionInfo DescribeDetailed(string toolName, JsonElement arguments)
    {
        var action = arguments.TryGetProperty("action", out var actionProp) &&
                     actionProp.ValueKind == JsonValueKind.String
            ? actionProp.GetString() ?? ""
            : "";

        var changeSummary = BuildChangeSummary(toolName, action, arguments);
        var risk = GetRiskLevel(toolName, action, arguments);

        var sb = new StringBuilder();
        sb.AppendLine($"Инструмент: {toolName}");

        if (!string.IsNullOrWhiteSpace(action))
        {
            sb.AppendLine($"Действие: {action}");
        }

        foreach (var field in new[] { "path", "service_name", "command", "process_name", "pid", "task_name", "url", "destination", "query" })
        {
            if (arguments.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
            {
                sb.AppendLine($"{field}: {Truncate(value.GetString() ?? "", 200)}");
            }
        }

        return new DangerousActionInfo(toolName, changeSummary, sb.ToString().TrimEnd(), risk);
    }

    public static DangerousActionInfo DescribeUndo(string description) =>
        new(
            "change_rollback",
            "Восстановление служб, задач планировщика и реестра из снимка",
            description,
            DangerousRiskLevel.High);

    private static string BuildChangeSummary(string toolName, string action, JsonElement arguments)
    {
        return toolName.ToLowerInvariant() switch
        {
            "registry" => $"Изменение реестра ({action})" +
                          FormatField(arguments, "path", prefix: " по пути "),
            "windows_service" => $"Управление службой{FormatField(arguments, "service_name", prefix: ": ")}" +
                                 (string.IsNullOrWhiteSpace(action) ? "" : $" → {action}"),
            "filesystem" => $"Запись в файл{FormatField(arguments, "path", prefix: ": ")}",
            "run_powershell" => $"Выполнение PowerShell{FormatField(arguments, "command", prefix: ": ", max: 120)}",
            "windows_process" => $"Завершение процесса{FormatField(arguments, "process_name", prefix: ": ")}" +
                                 FormatField(arguments, "pid", prefix: " PID "),
            "scheduled_task" => $"Изменение задачи{FormatField(arguments, "task_name", prefix: ": ")}" +
                                (string.IsNullOrWhiteSpace(action) ? "" : $" → {action}"),
            "network" => action switch
            {
                "flush_dns" => "Сброс кэша DNS",
                "firewall_enable" => "Включение брандмауэра",
                "firewall_disable" => "Отключение брандмауэра",
                _ => $"Изменение сети ({action})"
            },
            "virtualization" => $"Виртуализация → {action}",
            "download_file" => $"Загрузка файла{FormatField(arguments, "url", prefix: " с ")}",
            "change_rollback" => "Восстановление системы из снимка",
            "system_repair" => action switch
            {
                "run_sfc" => "Запуск проверки системных файлов (SFC)",
                "run_dism" => "Восстановление образа Windows (DISM)",
                _ => $"Системный ремонт ({action})"
            },
            _ => $"Операция через {toolName}"
        };
    }

    private static DangerousRiskLevel GetRiskLevel(string toolName, string action, JsonElement arguments)
    {
        return toolName.ToLowerInvariant() switch
        {
            "registry" when action is "delete_key" or "delete_value" => DangerousRiskLevel.High,
            "registry" => DangerousRiskLevel.Medium,
            "windows_service" => DangerousRiskLevel.Medium,
            "filesystem" => DangerousRiskLevel.Medium,
            "windows_process" => DangerousRiskLevel.Medium,
            "scheduled_task" when action is "delete" => DangerousRiskLevel.High,
            "scheduled_task" => DangerousRiskLevel.Medium,
            "network" when action is "firewall_disable" => DangerousRiskLevel.High,
            "network" => DangerousRiskLevel.Medium,
            "virtualization" => DangerousRiskLevel.Medium,
            "download_file" => DangerousRiskLevel.Medium,
            "change_rollback" => DangerousRiskLevel.High,
            "system_repair" => DangerousRiskLevel.High,
            "run_powershell" => ClassifyPowerShellRisk(arguments),
            _ => DangerousRiskLevel.Low
        };
    }

    private static DangerousRiskLevel ClassifyPowerShellRisk(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("command", out var commandProp))
        {
            return DangerousRiskLevel.Medium;
        }

        var command = commandProp.GetString() ?? string.Empty;

        if (Regex.IsMatch(command, @"\b(Format-Volume|Clear-Disk|Remove-Item\b.*-Recurse|Restart-Computer|Stop-Computer)\b",
                RegexOptions.IgnoreCase))
        {
            return DangerousRiskLevel.Critical;
        }

        if (Regex.IsMatch(command, @"\b(bcdedit|reg\s+delete|netsh\s+advfirewall.*disable)\b", RegexOptions.IgnoreCase))
        {
            return DangerousRiskLevel.High;
        }

        return DangerousRiskLevel.Medium;
    }

    private static string FormatField(JsonElement arguments, string field, string prefix = "", int max = 80)
    {
        if (!arguments.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var text = value.GetString() ?? "";
        return string.IsNullOrWhiteSpace(text) ? "" : prefix + Truncate(text, max);
    }

    private static bool IsRegistryWrite(JsonElement arguments) =>
        arguments.TryGetProperty("action", out var action) &&
        action.GetString() is "write" or "delete_value" or "delete_key";

    private static bool IsServiceControl(JsonElement arguments) =>
        arguments.TryGetProperty("action", out var action) &&
        action.GetString() is "start" or "stop" or "restart";

    private static bool IsFileWrite(JsonElement arguments) =>
        arguments.TryGetProperty("action", out var action) &&
        action.GetString() == "write";

    private static bool IsProcessStop(JsonElement arguments) =>
        arguments.TryGetProperty("action", out var action) &&
        action.GetString() is "stop" or "kill";

    private static bool IsTaskMutation(JsonElement arguments) =>
        arguments.TryGetProperty("action", out var action) &&
        action.GetString() is "create" or "delete" or "enable" or "disable" or "run";

    private static bool IsNetworkMutation(JsonElement arguments) =>
        arguments.TryGetProperty("action", out var action) &&
        action.GetString() is "flush_dns" or "firewall_enable" or "firewall_disable";

    private static bool IsVirtualizationMutation(JsonElement arguments) =>
        arguments.TryGetProperty("action", out var action) &&
        action.GetString() is "start_vm" or "stop_vm" or "docker_start" or "docker_stop";

    private static bool IsRollbackRestore(JsonElement arguments) =>
        arguments.TryGetProperty("action", out var action) &&
        action.GetString() == "restore";

    private static bool IsSystemRepairRun(JsonElement arguments) =>
        arguments.TryGetProperty("action", out var action) &&
        action.GetString() is "run_sfc" or "run_dism";

    private static bool PowerShellAttemptsDanger(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("command", out var commandProp))
        {
            return false;
        }

        var command = commandProp.GetString() ?? string.Empty;
        return PowerShellDangerousPatterns.Any(p => Regex.IsMatch(command, p, RegexOptions.IgnoreCase));
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}