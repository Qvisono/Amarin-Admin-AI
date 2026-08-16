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
    DangerousRiskLevel RiskLevel,
    /// <summary>Plain-language explanation from the model (shown as «Объяснение»).</summary>
    string Explanation = "");

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
            "disk_management" => IsDiskManagementWrite(arguments),
            "disk_space" => IsDiskSpaceWrite(arguments),
            "software_inventory" => IsSoftwareInventoryWrite(arguments),
            "firewall_rules" => IsFirewallRulesWrite(arguments),
            "windows_features" => IsWindowsFeaturesWrite(arguments),
            // Hard-blocked local_users ops (current user / last admin) must NOT prompt confirm.
            "local_users" => IsLocalUsersWrite(arguments) && !LocalUsersSafety.TryGetHardBlockReason(arguments, out _),
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
            "firewall_rules" => IsFirewallRulesWrite(arguments),
            "windows_features" => IsWindowsFeaturesWrite(arguments),
            "local_users" => IsLocalUsersWrite(arguments) && !LocalUsersSafety.TryGetHardBlockReason(arguments, out _),
            // chkdsk_fix / software_inventory / disk_space cleanup: confirm only
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
        var explanation = ExtractExplanation(arguments, changeSummary);
        var unlistedDownloadHost = TryGetUnlistedDownloadHost(toolName, arguments);

        if (unlistedDownloadHost is not null)
        {
            changeSummary =
                $"Загрузка с домена вне AllowedDomains: {unlistedDownloadHost}";
            if (risk < DangerousRiskLevel.High)
            {
                risk = DangerousRiskLevel.High;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Инструмент: {toolName}");

        if (!string.IsNullOrWhiteSpace(action))
        {
            sb.AppendLine($"Действие: {action}");
        }

        foreach (var field in new[]
                 {
                     "path", "service_name", "command", "process_name", "pid", "task_name",
                     "url", "destination", "query", "drive_letter", "package_id", "name",
                     "feature_name", "user", "group"
                 })
        {
            if (arguments.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
            {
                sb.AppendLine($"{field}: {Truncate(value.GetString() ?? "", 200)}");
            }
        }

        if (unlistedDownloadHost is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"⚠ Домен вне списка AllowedDomains: {unlistedDownloadHost}");
            sb.AppendLine("Этот сайт не в доверенном списке (Microsoft, GitHub, Discord и т.д.).");
            sb.AppendLine("Загрузка возможна только если вы явно подтвердите (1 / да).");
            sb.AppendLine("При отказе (2 / нет) файл скачан не будет.");
        }

        if (toolName.Equals("disk_management", StringComparison.OrdinalIgnoreCase) &&
            action.Equals("chkdsk_fix", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("Внимание: том может стать недоступен; для системного тома может потребоваться перезагрузка.");
            sb.AppendLine("Откат (/undo) не применим.");
        }

        if (toolName.Equals("disk_space", StringComparison.OrdinalIgnoreCase) &&
            action.Equals("cleanup", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("Категории очистки (только фиксированные пути):");
            if (arguments.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cats.EnumerateArray())
                {
                    if (c.ValueKind == JsonValueKind.String)
                    {
                        sb.AppendLine("  - " + (c.GetString() ?? ""));
                    }
                }
            }
            else
            {
                sb.AppendLine("  (categories не указаны)");
            }

            sb.AppendLine("Параметр path на cleanup НЕ влияет — произвольные пути не удаляются.");
            sb.AppendLine("Откат (/undo) не применим.");
        }

        if (toolName.Equals("software_inventory", StringComparison.OrdinalIgnoreCase) &&
            action is "install" or "upgrade" or "upgrade_all" or "uninstall")
        {
            sb.AppendLine("Источник: winget (App Installer). Флаги: --silent --accept-*-agreements --disable-interactivity.");
            sb.AppendLine("Откат (/undo) не применим — winget install/uninstall не откатывается снимком сессии.");
        }

        if (toolName.Equals("firewall_rules", StringComparison.OrdinalIgnoreCase) &&
            action is "enable" or "disable" or "create" or "delete")
        {
            sb.AppendLine("Изменение правил Windows Firewall.");
            if (arguments.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            {
                sb.AppendLine($"name: {Truncate(n.GetString() ?? "", 120)}");
            }

            sb.AppendLine("Перед изменением создаётся снимок сессии (/undo: службы/задачи/реестр; правило лучше откатить вручную enable/disable/delete).");
        }

        if (toolName.Equals("windows_features", StringComparison.OrdinalIgnoreCase) &&
            action is "enable" or "disable")
        {
            sb.AppendLine("Изменение Windows Optional Feature (-Online -NoRestart).");
            if (arguments.TryGetProperty("feature_name", out var fn) && fn.ValueKind == JsonValueKind.String)
            {
                sb.AppendLine($"feature_name: {Truncate(fn.GetString() ?? "", 120)}");
            }

            sb.AppendLine("PreviousState будет в ответе тулы — для ручного отката (enable/disable обратно).");
            sb.AppendLine("Снимок сессии (/undo) восстанавливает службы/задачи/реестр, но НЕ откатывает состояние optional feature.");
            sb.AppendLine("Машина НЕ перезагружается автоматически; при RestartNeeded=True — reboot вручную.");
        }

        if (toolName.Equals("local_users", StringComparison.OrdinalIgnoreCase) &&
            action is "enable_user" or "disable_user" or "add_to_group" or "remove_from_group")
        {
            sb.AppendLine("Изменение локальных учёток/членства в группах.");
            sb.AppendLine("PreviousEnabled / previous members будут в ответе тулы — для ручного отката.");
            sb.AppendLine("Снимок сессии (/undo) — службы/задачи/реестр; состояние Enabled и членство групп НЕ восстанавливает.");
            sb.AppendLine("Защита: нельзя отключить текущего пользователя сессии; нельзя убрать последнего Enabled из Администраторы.");
        }

        return new DangerousActionInfo(
            toolName,
            changeSummary,
            sb.ToString().TrimEnd(),
            risk,
            explanation);
    }

    public static DangerousActionInfo DescribeUndo(string description) =>
        new(
            "change_rollback",
            "Восстановление служб, задач планировщика и реестра из снимка",
            description,
            DangerousRiskLevel.High,
            "Вернёт ранее сохранённое состояние служб, задач планировщика и выбранных ключей реестра. " +
            "Файлы, установленные программы и произвольные изменения PowerShell этим снимком не откатываются.");

    /// <summary>
    /// Prefer model-supplied <c>explanation</c>; fall back to the structured change summary.
    /// </summary>
    private static string ExtractExplanation(JsonElement arguments, string changeSummary)
    {
        foreach (var key in new[] { "explanation", "reason", "purpose" })
        {
            if (arguments.TryGetProperty(key, out var prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                var text = (prop.GetString() ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return Truncate(text, 400);
                }
            }
        }

        return changeSummary;
    }

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
            "disk_management" => action switch
            {
                "chkdsk_fix" => "chkdsk / исправление тома" +
                                  FormatField(arguments, "drive_letter", prefix: " ", suffix: ":"),
                _ => $"Диски ({action})"
            },
            "disk_space" => action switch
            {
                "cleanup" => "Очистка диска (фиксированные категории)",
                _ => $"Место на диске ({action})"
            },
            "software_inventory" => action switch
            {
                "install" => "Установка пакета" + FormatField(arguments, "package_id", prefix: ": "),
                "upgrade" => "Обновление пакета" + FormatField(arguments, "package_id", prefix: ": "),
                "upgrade_all" => "Обновление всех пакетов (winget upgrade --all)",
                "uninstall" => "Удаление пакета" + FormatField(arguments, "package_id", prefix: ": "),
                _ => $"ПО ({action})"
            },
            "firewall_rules" => action switch
            {
                "enable" => "Включение правила брандмауэра" + FormatField(arguments, "name", prefix: ": "),
                "disable" => "Отключение правила брандмауэра" + FormatField(arguments, "name", prefix: ": "),
                "create" => "Создание правила брандмауэра" + FormatField(arguments, "name", prefix: ": "),
                "delete" => "Удаление правила брандмауэра" + FormatField(arguments, "name", prefix: ": "),
                _ => $"Брандмауэр ({action})"
            },
            "windows_features" => action switch
            {
                "enable" => "Включение optional feature" + FormatField(arguments, "feature_name", prefix: ": "),
                "disable" => "Отключение optional feature" + FormatField(arguments, "feature_name", prefix: ": "),
                _ => $"Windows features ({action})"
            },
            "local_users" => action switch
            {
                "enable_user" => "Включение учётки" + FormatField(arguments, "user", prefix: ": "),
                "disable_user" => "Отключение учётки" + FormatField(arguments, "user", prefix: ": "),
                "add_to_group" => "Добавление в группу" + FormatField(arguments, "user", prefix: " ") +
                                  FormatField(arguments, "group", prefix: " → "),
                "remove_from_group" => "Удаление из группы" + FormatField(arguments, "user", prefix: " ") +
                                       FormatField(arguments, "group", prefix: " ← "),
                _ => $"Локальные пользователи ({action})"
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
            "disk_management" when action is "chkdsk_fix" => DangerousRiskLevel.High,
            "disk_space" when action is "cleanup" => DangerousRiskLevel.Medium,
            "software_inventory" when action is "uninstall" => DangerousRiskLevel.High,
            "software_inventory" when action is "install" or "upgrade" or "upgrade_all"
                => DangerousRiskLevel.Medium,
            "firewall_rules" when action is "delete" => DangerousRiskLevel.High,
            "firewall_rules" when action is "enable" or "disable" or "create" => DangerousRiskLevel.Medium,
            "windows_features" when action is "enable" or "disable" => DangerousRiskLevel.High,
            "local_users" when action is "disable_user" or "remove_from_group" => DangerousRiskLevel.High,
            "local_users" when action is "enable_user" or "add_to_group" => DangerousRiskLevel.Medium,
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

    private static string FormatField(
        JsonElement arguments,
        string field,
        string prefix = "",
        string suffix = "",
        int max = 80)
    {
        if (!arguments.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var text = value.GetString() ?? "";
        return string.IsNullOrWhiteSpace(text) ? "" : prefix + Truncate(text, max) + suffix;
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

    private static bool IsDiskManagementWrite(JsonElement arguments) =>
        arguments.TryGetProperty("action", out var action) &&
        action.GetString() is "chkdsk_fix";

    private static bool IsDiskSpaceWrite(JsonElement arguments) =>
        arguments.TryGetProperty("action", out var action) &&
        action.GetString() is "cleanup";

    private static bool IsSoftwareInventoryWrite(JsonElement arguments) =>
        arguments.TryGetProperty("action", out var action) &&
        action.GetString() is "install" or "upgrade" or "upgrade_all" or "uninstall";

    private static bool IsFirewallRulesWrite(JsonElement arguments) =>
        arguments.TryGetProperty("action", out var action) &&
        action.GetString() is "enable" or "disable" or "create" or "delete";

    private static bool IsWindowsFeaturesWrite(JsonElement arguments) =>
        arguments.TryGetProperty("action", out var action) &&
        action.GetString() is "enable" or "disable";

    private static bool IsLocalUsersWrite(JsonElement arguments) =>
        arguments.TryGetProperty("action", out var action) &&
        action.GetString() is "enable_user" or "disable_user" or "add_to_group" or "remove_from_group";

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

    /// <summary>
    /// Returns the host when download_file targets a domain outside AllowedDomains; otherwise null.
    /// </summary>
    private static string? TryGetUnlistedDownloadHost(string toolName, JsonElement arguments)
    {
        if (!toolName.Equals("download_file", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!arguments.TryGetProperty("url", out var urlProp) ||
            urlProp.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var urlText = urlProp.GetString();
        if (string.IsNullOrWhiteSpace(urlText) ||
            !Uri.TryCreate(urlText, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return DownloadValidator.IsDomainAllowed(uri) ? null : uri.Host;
    }
}