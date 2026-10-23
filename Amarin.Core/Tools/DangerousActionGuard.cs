using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amarin.Core;

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
    string Explanation = "",
    /// <summary>
    /// Everything that will actually run or be written: the script, the file's contents, the
    /// registry data. Never truncated — the summary above is the short form, and a person
    /// approving a change is entitled to see the whole of what they are approving.
    /// </summary>
    string CodeText = "",
    /// <summary>Highlighting hint for <c>CodeHighlighter</c>; empty renders as plain monospace.</summary>
    string CodeLanguage = "",
    /// <summary>
    /// Ask even in «подтверждать всё автоматически» mode. Set only for the question SynGuard
    /// raises about a call it read as an attack: that question is the last thing standing
    /// between the machine and an attack, so a convenience switch must not answer it — the same
    /// reasoning that keeps the download allowlist asking.
    /// </summary>
    bool AlwaysAsk = false,
    /// <summary>Аргументы вызова — для пробного прогона (<see cref="WhatIfProbe"/>).</summary>
    JsonElement? Arguments = null,
    /// <summary>
    /// Удалённая машина, на которой исполнится вызов; null — этот ПК. Окно подтверждения
    /// называет её, а пробный прогон не запускается: он прошёл бы здесь, а не там.
    /// </summary>
    string? Target = null)
{
    /// <summary>
    /// Согласие на будущее, а не на сейчас: отложенная команда выполнится без человека, поэтому
    /// ни режим «подтверждать всё автоматически», ни «разрешить на ход/чат» за него не отвечают —
    /// одно «да» иначе одобрило бы и все следующие скрипты, которые модель поставит на потом.
    /// </summary>
    public bool Standing => ToolName.Equals(DeferredTaskTool.ToolName, StringComparison.OrdinalIgnoreCase);
}

internal static partial class DangerousActionGuard
{
    [GeneratedRegex(
        @"\b(?:Set-ItemProperty|New-ItemProperty|Remove-ItemProperty|Start-Service|Stop-Service|Restart-Service|Set-Service|bcdedit|reagentc|netsh\s+advfirewall|reg\s+add|reg\s+delete|Disable-WindowsOptionalFeature|Enable-WindowsOptionalFeature|Restart-Computer|Stop-Computer|Format-Volume|Clear-Disk|Stop-Process|docker\s+(?:stop|kill|rm|remove)|Stop-VM|Suspend-VM|Checkpoint-VM)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellDangerousPattern();

    [GeneratedRegex(
        @"\b(?:Format-Volume|Clear-Disk|Remove-Item\b.*-Recurse|Restart-Computer|Stop-Computer)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellCriticalPattern();

    [GeneratedRegex(
        @"\b(?:bcdedit|reg\s+delete|netsh\s+advfirewall.*disable)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellHighRiskPattern();

    public static bool RequiresConfirmation(string toolName, JsonElement arguments)
    {
        return toolName.ToLowerInvariant() switch
        {
            "registry" => IsRegistryWrite(arguments),
            "windows_service" => IsServiceControl(arguments),
            "filesystem" => IsFileWrite(arguments),
            // write_file wraps its arguments into a filesystem/write call and executes it
            // directly, so checking only "filesystem" let every file it wrote through unasked.
            "write_file" => true,
            // Файлы и документы чата (1.33.0): спрашиваются так же, как write_file, — без вопроса
            // шлюз пропускает только новый файл в «Загрузках» и на «Рабочем столе».
            "edit_file" or "create_folder" or "create_document" or "edit_document" or "save_image" => true,
            // Отложенная команда и возврат программ выполнятся без человека — согласие берётся сейчас.
            "deferred_task" => ActionOf(arguments) is "run_command" or "restore_programs",
            // Разбор по дереву: спрашивается всё, кроме чистого чтения. Прежде — только то, что
            // нашлось регуляркой в списке опасного, и Set-Content или winget install шли молча.
            "run_powershell" => PowerShellAnalysis.Analyze(arguments).IsWrite,
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
            "startup_programs" => IsStartupToggle(arguments),
            "windows_update" => IsUpdateMutation(arguments),
            "dns_config" => IsDnsMutation(arguments),
            "devices" => IsDeviceMutation(arguments),
            "security_status" => IsDefenderAction(arguments),
            "remote_access" => IsRdpToggle(arguments),
            // Буфер читает любое приложение на машине: что туда кладётся, решает человек.
            "write_clipboard" => true,
            // Нажатие в чужом окне — запись, и параллельной пачкой его не пускают. Вопрос в обычном
            // режиме шлюз про мышь не задаёт (ToolGate.RequiresConfirmation): согласие — просьба
            // человека, а держат «небезопасно» SynGuard и ComputerSafety.
            "computer" => !ComputerTool.ReadActions.Contains(ActionOf(arguments)),
            // Браузер агента (1.37.0) — как мышь: нажатие и ввод на сайте — запись, а вопрос в
            // обычном режиме шлюз не задаёт (ToolGate.RequiresConfirmation).
            "browser" => !BrowserTool.ReadActions.Contains(ActionOf(arguments)),
            // Инструменты 1.37.0: спрашивается всё, что не чтение по таблице ToolEffects, — и новое
            // действие, про которое там забыли, тоже.
            "audio" or "power" or "file_locks" or "permissions" or "environment" or "store_apps" or "print_queue"
                or "shares" or "bluetooth" => ToolEffects.Classify(toolName, arguments) == ToolEffect.Write,
            _ => false
        };
    }

    /// <summary>
    /// System snapshot before the change — only when rollback can restore meaningful state.
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
            "startup_programs" => IsStartupToggle(arguments),
            "windows_update" => ActionOf(arguments) is "pause" or "resume",
            "dns_config" => IsDnsMutation(arguments),
            "devices" => ActionOf(arguments) is "enable" or "disable",
            "remote_access" => IsRdpToggle(arguments),
            // Права и переменные среды возвращает откат: SDDL объекта и раздел окружения в снимке.
            "permissions" => PermissionTargets.IsWrite(ActionOf(arguments)),
            "environment" => IsEnvironmentWrite(arguments),
            // chkdsk_fix / software_inventory / disk_space cleanup / установка обновлений / откат
            // драйвера / проверка Защитника / буфер обмена: только вопрос — вернуть снимком нечего.
            _ => false
        };

    public static string Describe(string toolName, JsonElement arguments) =>
        DescribeDetailed(toolName, arguments).Details;

    public static DangerousActionInfo DescribeDetailed(string toolName, JsonElement arguments)
    {
        var action = ActionOf(arguments);

        var changeSummary = BuildChangeSummary(toolName, action, arguments);
        var risk = GetRiskLevel(toolName, action, arguments);
        var explanation = ExtractExplanation(arguments, changeSummary);
        var (codeText, codeLanguage) = ExtractCode(toolName, arguments);
        var unlistedDownloadHost = TryGetUnlistedDownloadHost(toolName, arguments);

        if (unlistedDownloadHost is not null)
        {
            changeSummary = L("S.Guard.DownloadRefused", unlistedDownloadHost);
            if (risk < DangerousRiskLevel.High)
            {
                risk = DangerousRiskLevel.High;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine(L("S.Guard.Tool", toolName));

        if (!string.IsNullOrWhiteSpace(action))
        {
            sb.AppendLine(L("S.Guard.Action", action));
        }

        foreach (var field in new[]
                 {
                     "path", "service_name", "command", "process_name", "pid", "task_name",
                     "url", "destination", "query", "drive_letter", "package_id", "name",
                     "feature_name", "user", "group", "adapter", "wifi_profile", "instance_id",
                     "hostname", "address", "start_type", "location", "window", "element", "keys", "text",
                     "device", "app", "scheme", "principal", "rights", "scope", "printer", "letter", "remote_path"
                 })
        {
            if (arguments.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
            {
                sb.AppendLine($"{field}: {Truncate(value.GetString() ?? "", 200)}");
            }
        }

        // Перезапись — отдельной строкой и в начале: из пути её не видно, а человек решает
        // именно об этом.
        if (OverwrittenPath(toolName, action, arguments) is { } overwritten)
        {
            changeSummary = Amarin.Core.Loc.Format("S.Confirm.Overwrite", overwritten) + " " + changeSummary;
            if (risk < DangerousRiskLevel.Medium)
            {
                risk = DangerousRiskLevel.Medium;
            }
        }

        // Что именно в скрипте сочтено записью: человек решает про эти команды, а не про весь
        // текст, в котором их ещё надо найти.
        if (toolName.Equals("run_powershell", StringComparison.OrdinalIgnoreCase) &&
            PowerShellAnalysis.Analyze(arguments) is { IsWrite: true, Reasons.Count: > 0 } verdict)
        {
            var changes = Amarin.Core.Loc.Format("S.Confirm.ScriptChanges", string.Join(", ", verdict.Reasons));
            changeSummary += "\n" + changes;
            sb.AppendLine(changes);
        }

        if (unlistedDownloadHost is not null)
        {
            sb.AppendLine();
            sb.AppendLine(L("S.Guard.DomainNotListed", unlistedDownloadHost));
            sb.AppendLine(L("S.Guard.RefusedAnyway"));
            sb.AppendLine(L("S.Guard.AddDomainHint"));
        }

        if (toolName.Equals("disk_management", StringComparison.OrdinalIgnoreCase) &&
            action.Equals("chkdsk_fix", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine(L("S.Guard.ChkdskWarn"));
            sb.AppendLine(L("S.Guard.NoSnapshotUndo"));
        }

        if (toolName.Equals("disk_space", StringComparison.OrdinalIgnoreCase) &&
            action.Equals("cleanup", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine(L("S.Guard.CleanupCategories"));
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
                sb.AppendLine("  " + L("S.Guard.NoCategories"));
            }

            sb.AppendLine(L("S.Guard.CleanupPathIgnored"));
            sb.AppendLine(L("S.Guard.NoSnapshotUndo"));
        }

        if (toolName.Equals("software_inventory", StringComparison.OrdinalIgnoreCase) &&
            action is "install" or "upgrade" or "upgrade_all" or "uninstall")
        {
            sb.AppendLine(L("S.Guard.WingetSource"));
            sb.AppendLine(L("S.Guard.WingetNoUndo"));
        }

        if (toolName.Equals("firewall_rules", StringComparison.OrdinalIgnoreCase) &&
            action is "enable" or "disable" or "create" or "delete")
        {
            sb.AppendLine(L("S.Guard.FirewallChange"));
            if (arguments.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            {
                sb.AppendLine($"name: {Truncate(n.GetString() ?? "", 120)}");
            }

            sb.AppendLine(L("S.Guard.FirewallSnapshot"));
        }

        if (toolName.Equals("windows_features", StringComparison.OrdinalIgnoreCase) &&
            action is "enable" or "disable")
        {
            sb.AppendLine(L("S.Guard.FeatureChange"));
            if (arguments.TryGetProperty("feature_name", out var fn) && fn.ValueKind == JsonValueKind.String)
            {
                sb.AppendLine($"feature_name: {Truncate(fn.GetString() ?? "", 120)}");
            }

            sb.AppendLine(L("S.Guard.FeaturePrevious"));
            sb.AppendLine(L("S.Guard.FeatureSnapshot"));
            sb.AppendLine(L("S.Guard.FeatureNoReboot"));
        }

        if (toolName.Equals("local_users", StringComparison.OrdinalIgnoreCase) &&
            action is "enable_user" or "disable_user" or "add_to_group" or "remove_from_group")
        {
            sb.AppendLine(L("S.Guard.UsersChange"));
            sb.AppendLine(L("S.Guard.UsersPrevious"));
            sb.AppendLine(L("S.Guard.UsersSnapshot"));
            sb.AppendLine(L("S.Guard.UsersProtection"));
        }

        foreach (var note in ActionNotes(toolName, action, arguments))
        {
            sb.AppendLine(note);
        }

        return new DangerousActionInfo(
            toolName,
            changeSummary,
            sb.ToString().TrimEnd(),
            risk,
            explanation,
            codeText,
            codeLanguage,
            Arguments: arguments.ValueKind == JsonValueKind.Undefined ? null : arguments.Clone());
    }

    /// <summary>Что человеку важно знать про новые действия, кроме сводки: перезагрузка, отката нет.</summary>
    private static IEnumerable<string> ActionNotes(string toolName, string action, JsonElement arguments)
    {
        switch (toolName.ToLowerInvariant(), action)
        {
            case ("permissions", _) when PermissionTargets.IsWrite(action):
                yield return L("S.Guard.Note.AclSnapshot");
                break;
            case ("environment", _) when IsEnvironmentWrite(arguments):
                yield return L("S.Guard.Note.EnvSnapshot");
                break;
            case ("file_locks", "close"):
                yield return L(Flag(arguments, "force") ? "S.Guard.Note.LockForce" : "S.Guard.Note.LockClose");
                yield return L("S.Guard.NoSnapshotUndo");
                break;
            case ("audio", "restart_services"):
                yield return L("S.Guard.Note.AudioRestart");
                break;
            case ("store_apps", "remove"):
                yield return L("S.Guard.Note.StoreRemove");
                break;
            case ("store_apps", "reset"):
                yield return L("S.Guard.Note.StoreReset");
                yield return L("S.Guard.NoSnapshotUndo");
                break;
            case ("print_queue", "clear_queue"):
                yield return L("S.Guard.Note.PrintClear");
                break;
            case ("print_queue", "restart_spooler"):
                yield return L("S.Guard.Note.PrintRestart");
                if (Flag(arguments, "purge"))
                {
                    yield return L("S.Guard.Note.PrintClear");
                }

                break;
            case ("shares", "enable_file_sharing"):
                yield return L("S.Guard.Note.SharesFiles");
                yield return L("S.Guard.NoSnapshotUndo");
                break;
            case ("bluetooth", "radio_off"):
                yield return L("S.Guard.Note.BluetoothOff");
                break;
            case ("bluetooth", "forget"):
                yield return L("S.Guard.Note.BluetoothForget");
                break;
            case ("audio" or "power" or "shares" or "print_queue", _) when ToolEffects.Classify(toolName, arguments) == ToolEffect.Write:
                yield return L("S.Guard.NoSnapshotUndo");
                break;
            case ("network", "reset_winsock" or "reset_ip"):
                yield return L("S.Guard.Note.RebootNeeded");
                yield return L("S.Guard.NoSnapshotUndo");
                break;
            case ("network", "adapter_disable"):
                yield return L("S.Guard.Note.AdapterOff");
                break;
            case ("windows_update", "install"):
                yield return L("S.Guard.Note.UpdateNoReboot");
                yield return L("S.Guard.NoSnapshotUndo");
                break;
            case ("devices", "rollback_driver"):
                yield return L("S.Guard.Note.DriverRollback");
                break;
            case ("devices", "disable"):
                yield return L("S.Guard.Note.DeviceProtected");
                break;
            case ("remote_access", "rdp_enable"):
                yield return L("S.Guard.Note.RdpOpen");
                break;
        }
    }

    /// <summary>Файл, который вызов перезапишет, если он уже есть. Иначе null.</summary>
    /// <remarks>
    /// Прежде окно подтверждения о перезаписи молчало: <c>download_file</c> и копирование
    /// заменяли файл с тем же именем, а вопрос выглядел как про новый.
    /// </remarks>
    internal static string? OverwrittenPath(string toolName, string action, JsonElement arguments)
    {
        string? target = toolName.ToLowerInvariant() switch
        {
            "write_file" or "create_document" or "save_image" => Field(arguments, "path"),
            // Правка на месте — правка, а не перезапись; перезапись — только save_as поверх чужого файла.
            "edit_document" => Field(arguments, "save_as"),
            "filesystem" when action == "write" => Field(arguments, "path"),
            "filesystem" when action is "copy" or "move" => Field(arguments, "destination"),
            "download_file" => DownloadTarget(arguments),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        try
        {
            return File.Exists(target.Trim().Trim('"')) ? target.Trim().Trim('"') : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        static string? Field(JsonElement args, string name) =>
            args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        static string? DownloadTarget(JsonElement args)
        {
            if (Field(args, "url") is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return null;
            }

            return DownloadPaths.TryResolveDestination(Field(args, "destination"), Field(args, "folder"), uri,
                out var path, out _)
                ? path
                : null;
        }
    }

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

    /// <summary>
    /// Pulls out the body the tool is about to run or write.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="BuildChangeSummary"/> and the details block, both of
    /// which truncate: a summary is meant to be short, but the thing being approved is not. Before
    /// this existed a file write showed only its path, so a .bat could be approved without a single
    /// line of it ever reaching the screen.
    /// </remarks>
    private static (string Text, string Language) ExtractCode(string toolName, JsonElement arguments)
    {
        // Правка — тем, что меняется: «было» и «стало» строками с минусом и плюсом, а не одним путём.
        if (toolName.Equals("edit_file", StringComparison.OrdinalIgnoreCase))
        {
            return (EditDiff(arguments), "");
        }

        var (field, language) = toolName.ToLowerInvariant() switch
        {
            "run_powershell" => ("command", "powershell"),
            "filesystem" or "write_file" => ("content", LanguageFromPath(arguments)),
            "create_document" => ("content", "markdown"),
            "registry" => ("value_data", "ini"),
            "scheduled_task" => ("command", "powershell"),
            "deferred_task" => ("command", "powershell"),
            _ => ("", "")
        };

        if (toolName.Equals("edit_document", StringComparison.OrdinalIgnoreCase) &&
            arguments.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array)
        {
            return (operations.ToString(), "json");
        }

        if (field.Length == 0 ||
            !arguments.TryGetProperty(field, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return ("", "");
        }

        var text = value.GetString() ?? "";
        return string.IsNullOrWhiteSpace(text) ? ("", "") : (text, language);
    }

    /// <summary>Правка <c>edit_file</c> строками «- было» и «+ стало» — так её и читают глазами.</summary>
    /// <summary>
    /// Что и когда выполнится без человека: «Отложенная команда: Очистка TEMP · каждый день в 03:00».
    /// Срок в вопросе — главное: человек одобряет не только скрипт, но и то, как часто он пойдёт.
    /// </summary>
    private static string DeferredSummary(JsonElement arguments)
    {
        var restore = ActionOf(arguments) == "restore_programs";
        var head = L(restore ? "S.Guard.Sum.DeferredRestore" : "S.Guard.Sum.DeferredCommand") + FormatField(arguments, "title", prefix: ": ");
        try
        {
            var facts = DeferredClock.Now();
            var kind = restore ? DeferredKind.RestorePrograms : DeferredKind.Command;
            var schedule = DeferredTimeParser.Resolve(arguments, kind, facts);
            var task = new DeferredTask { Kind = kind, Trigger = schedule.Trigger, Repeat = schedule.Repeat, NextDueUtc = schedule.FirstDueUtc };
            return head + " · " + DeferredText.When(task, facts);
        }
        catch (DeferredInputException)
        {
            // Срок не разобрался — инструмент откажет сам; вопрос всё равно честно называет команду.
            return head;
        }
    }

    private static string EditDiff(JsonElement arguments)
    {
        static IEnumerable<string> Lines(JsonElement args, string name, string sign) =>
            (args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => sign + line);

        return string.Join("\n", Lines(arguments, "old_string", "- ").Concat(Lines(arguments, "new_string", "+ ")));
    }

    /// <summary>
    /// Highlighting hint from the file's extension. Unknown extensions return empty on purpose:
    /// <c>CodeHighlighter</c> renders those as plain monospace, which is better than colouring a
    /// .bat as if it were C#.
    /// </summary>
    private static string LanguageFromPath(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("path", out var pathProp) ||
            pathProp.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        var path = pathProp.GetString() ?? "";
        var dot = path.LastIndexOf('.');
        if (dot < 0 || dot == path.Length - 1)
        {
            return "";
        }

        return path[(dot + 1)..].ToLowerInvariant() switch
        {
            "ps1" or "psm1" => "powershell",
            "bat" or "cmd" => "bat",
            "py" => "python",
            "json" => "json",
            "xml" or "csproj" => "xml",
            "reg" or "ini" => "ini",
            "cs" => "csharp",
            "js" => "javascript",
            "ts" => "typescript",
            "sql" => "sql",
            "html" or "htm" => "html",
            "css" => "css",
            _ => ""
        };
    }

    /// <remarks>
    /// Слова — из словаря, а приставки полей (двоеточие, «по пути», стрелки) — в коде: пробелы
    /// по краям значения словарь не хранит (XAML их срезает).
    /// </remarks>
    private static string BuildChangeSummary(string toolName, string action, JsonElement arguments)
    {
        return toolName.ToLowerInvariant() switch
        {
            "registry" => L("S.Guard.Sum.Registry", action) +
                          FormatField(arguments, "path", prefix: " " + L("S.Guard.Sum.AtPath") + " "),
            "windows_service" => L("S.Guard.Sum.Service") + FormatField(arguments, "service_name", prefix: ": ") +
                                 (string.IsNullOrWhiteSpace(action) ? "" : $" → {action}"),
            "filesystem" or "write_file" => L("S.Guard.Sum.WriteFile") + FormatField(arguments, "path", prefix: ": "),
            "edit_file" => L("S.Guard.Sum.EditFile") + FormatField(arguments, "path", prefix: ": "),
            "create_folder" => L("S.Guard.Sum.CreateFolder") + FormatField(arguments, "path", prefix: ": "),
            "create_document" => L("S.Guard.Sum.CreateDocument") + FormatField(arguments, "path", prefix: ": "),
            "edit_document" => L("S.Guard.Sum.EditDocument") + FormatField(arguments, "path", prefix: ": ") +
                               FormatField(arguments, "save_as", prefix: " → "),
            "save_image" => L("S.Guard.Sum.SaveImage") + FormatField(arguments, "path", prefix: ": "),
            "deferred_task" => DeferredSummary(arguments),
            "run_powershell" => L("S.Guard.Sum.PowerShell") + FormatField(arguments, "command", prefix: ": ", max: 120),
            "computer" => L("S.Guard.Sum.Computer", action) + FormatField(arguments, "keys", prefix: ": ") +
                          FormatField(arguments, "text", prefix: ": ", max: 120),
            "browser" => L("S.Guard.Sum.Browser", action) + FormatField(arguments, "url", prefix: ": ") +
                         FormatField(arguments, "element", prefix: " ") + FormatField(arguments, "key", prefix: ": ") +
                         FormatField(arguments, "text", prefix: ": ", max: 120),
            "windows_process" => L("S.Guard.Sum.KillProcess") + FormatField(arguments, "process_name", prefix: ": ") +
                                 FormatField(arguments, "pid", prefix: " PID "),
            "scheduled_task" => L("S.Guard.Sum.Task") + FormatField(arguments, "task_name", prefix: ": ") +
                                (string.IsNullOrWhiteSpace(action) ? "" : $" → {action}"),
            "network" => action switch
            {
                "flush_dns" => L("S.Guard.Sum.FlushDns"),
                "firewall_enable" => L("S.Guard.Sum.FirewallOn"),
                "firewall_disable" => L("S.Guard.Sum.FirewallOff"),
                "adapter_enable" => L("S.Guard.Sum.AdapterOn") + FormatField(arguments, "adapter", prefix: ": "),
                "adapter_disable" => L("S.Guard.Sum.AdapterOff") + FormatField(arguments, "adapter", prefix: ": "),
                "wifi_forget" => L("S.Guard.Sum.WifiForget") + FormatField(arguments, "wifi_profile", prefix: ": "),
                "reset_winsock" => L("S.Guard.Sum.ResetWinsock"),
                "reset_ip" => L("S.Guard.Sum.ResetIp"),
                _ => L("S.Guard.Sum.Network", action)
            },
            "dns_config" => action switch
            {
                "set_dns" => L("S.Guard.Sum.SetDns") + FormatField(arguments, "adapter", prefix: ": ") +
                             FormatList(arguments, "servers", prefix: " → "),
                "reset_dns" => L("S.Guard.Sum.ResetDns") + FormatField(arguments, "adapter", prefix: ": "),
                "hosts_add" => L("S.Guard.Sum.HostsAdd") + FormatField(arguments, "address", prefix: ": ") +
                               FormatField(arguments, "hostname", prefix: " "),
                "hosts_remove" => L("S.Guard.Sum.HostsRemove") + FormatField(arguments, "hostname", prefix: ": "),
                _ => L("S.Guard.Sum.Other", toolName)
            },
            "startup_programs" => (action == "enable" ? L("S.Guard.Sum.StartupOn") : L("S.Guard.Sum.StartupOff")) +
                                  FormatField(arguments, "name", prefix: ": "),
            "windows_update" => action switch
            {
                "install" => L("S.Guard.Sum.UpdateInstall") + FormatList(arguments, "kb", prefix: ": ", empty: L("S.Guard.Sum.AllPending")),
                "hide" => L("S.Guard.Sum.UpdateHide") + FormatList(arguments, "kb", prefix: ": "),
                "unhide" => L("S.Guard.Sum.UpdateUnhide") + FormatList(arguments, "kb", prefix: ": "),
                "pause" => L("S.Guard.Sum.UpdatePause", PauseDays(arguments)),
                "resume" => L("S.Guard.Sum.UpdateResume"),
                _ => L("S.Guard.Sum.Other", toolName)
            },
            "devices" => action switch
            {
                "enable" => L("S.Guard.Sum.DeviceOn") + FormatField(arguments, "instance_id", prefix: ": "),
                "disable" => L("S.Guard.Sum.DeviceOff") + FormatField(arguments, "instance_id", prefix: ": "),
                "rollback_driver" => L("S.Guard.Sum.DriverRollback") + FormatField(arguments, "instance_id", prefix: ": "),
                _ => L("S.Guard.Sum.Other", toolName)
            },
            "security_status" => action == "quick_scan" ? L("S.Guard.Sum.QuickScan") : L("S.Guard.Sum.Signatures"),
            "remote_access" => action == "rdp_enable" ? L("S.Guard.Sum.RdpOn") : L("S.Guard.Sum.RdpOff"),
            "write_clipboard" => L("S.Guard.Sum.Clipboard") + FormatField(arguments, "text", prefix: ": ", max: 60),
            "virtualization" => L("S.Guard.Sum.Virtualization") + $" → {action}",
            "download_file" => L("S.Guard.Sum.Download") + FormatField(arguments, "url", prefix: ": "),
            "change_rollback" => L("S.Guard.Sum.Rollback"),
            "system_repair" => action switch
            {
                "run_sfc" => L("S.Guard.Sum.Sfc"),
                "run_dism" => L("S.Guard.Sum.Dism"),
                _ => L("S.Guard.Sum.Repair", action)
            },
            "disk_management" => action switch
            {
                "chkdsk_fix" => L("S.Guard.Sum.Chkdsk") +
                                FormatField(arguments, "drive_letter", prefix: " ", suffix: ":"),
                _ => L("S.Guard.Sum.Disks", action)
            },
            "disk_space" => action switch
            {
                "cleanup" => L("S.Guard.Sum.Cleanup"),
                _ => L("S.Guard.Sum.DiskSpace", action)
            },
            "software_inventory" => action switch
            {
                "install" => L("S.Guard.Sum.Install") + FormatField(arguments, "package_id", prefix: ": "),
                "upgrade" => L("S.Guard.Sum.Upgrade") + FormatField(arguments, "package_id", prefix: ": "),
                "upgrade_all" => L("S.Guard.Sum.UpgradeAll"),
                "uninstall" => L("S.Guard.Sum.Uninstall") + FormatField(arguments, "package_id", prefix: ": "),
                _ => L("S.Guard.Sum.Software", action)
            },
            "firewall_rules" => action switch
            {
                "enable" => L("S.Guard.Sum.RuleOn") + FormatField(arguments, "name", prefix: ": "),
                "disable" => L("S.Guard.Sum.RuleOff") + FormatField(arguments, "name", prefix: ": "),
                "create" => L("S.Guard.Sum.RuleCreate") + FormatField(arguments, "name", prefix: ": "),
                "delete" => L("S.Guard.Sum.RuleDelete") + FormatField(arguments, "name", prefix: ": "),
                _ => L("S.Guard.Sum.Firewall", action)
            },
            "windows_features" => action switch
            {
                "enable" => L("S.Guard.Sum.FeatureOn") + FormatField(arguments, "feature_name", prefix: ": "),
                "disable" => L("S.Guard.Sum.FeatureOff") + FormatField(arguments, "feature_name", prefix: ": "),
                _ => $"Windows features ({action})"
            },
            "audio" => action switch
            {
                "set_default" => L("S.Guard.Sum.AudioDefault") + FormatField(arguments, "device", prefix: ": "),
                "set_volume" => L("S.Guard.Sum.AudioVolume", FileToolPaths.Int(arguments, "volume")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?") +
                                AudioTarget(arguments),
                "set_mute" => L(Flag(arguments, "mute") ? "S.Guard.Sum.AudioMute" : "S.Guard.Sum.AudioUnmute") + AudioTarget(arguments),
                "restart_services" => L("S.Guard.Sum.AudioRestart"),
                _ => L("S.Guard.Sum.Other", toolName)
            },
            "power" => L("S.Guard.Sum.PowerScheme") + FormatField(arguments, "scheme", prefix: ": "),
            "file_locks" => L(Flag(arguments, "force") ? "S.Guard.Sum.LockForce" : "S.Guard.Sum.LockClose") +
                            (FileToolPaths.Int(arguments, "pid") is { } pid ? $" PID {pid}" : "") +
                            FormatField(arguments, "path", prefix: ": "),
            "permissions" => action switch
            {
                "set_owner" => L("S.Guard.Sum.AclOwner", Principal(arguments, "current_user")),
                "grant" => L("S.Guard.Sum.AclGrant", Principal(arguments, "?"), FileToolPaths.String(arguments, "rights")?.Trim() ?? "?"),
                "revoke" => L("S.Guard.Sum.AclRevoke", Principal(arguments, "?")),
                "reset" => L("S.Guard.Sum.AclReset"),
                _ => L("S.Guard.Sum.Other", toolName)
            } + FormatField(arguments, "path", prefix: ": ") + (Flag(arguments, "recursive") ? " " + L("S.Guard.Sum.Recursive") : ""),
            "environment" => action switch
            {
                "set" => L("S.Guard.Sum.EnvSet") + FormatField(arguments, "name", prefix: ": "),
                "delete" => L("S.Guard.Sum.EnvDelete") + FormatField(arguments, "name", prefix: ": "),
                "path_add" => L("S.Guard.Sum.EnvPathAdd") + FormatField(arguments, "path", prefix: ": "),
                "path_remove" => L("S.Guard.Sum.EnvPathRemove") + FormatField(arguments, "path", prefix: ": "),
                "path_clean" => L("S.Guard.Sum.EnvPathClean"),
                _ => L("S.Guard.Sum.Other", toolName)
            } + (IsMachineScope(arguments) ? " " + L("S.Guard.Sum.EnvMachine") : ""),
            "store_apps" => (action == "reset" ? L("S.Guard.Sum.StoreReset") : L("S.Guard.Sum.StoreRemove")) +
                            FormatField(arguments, "name", prefix: ": ") +
                            (action == "remove" && Flag(arguments, "for_new_users") ? " " + L("S.Guard.Sum.StoreForNewUsers") : ""),
            "print_queue" => action switch
            {
                "cancel_job" => L("S.Guard.Sum.PrintCancel", FileToolPaths.Int(arguments, "job_id")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?") +
                                FormatField(arguments, "printer", prefix: ": "),
                "clear_queue" => L("S.Guard.Sum.PrintClear") + FormatField(arguments, "printer", prefix: ": "),
                "restart_spooler" => L(Flag(arguments, "purge") ? "S.Guard.Sum.PrintPurge" : "S.Guard.Sum.PrintRestart"),
                "set_default" => L("S.Guard.Sum.PrintDefault") + FormatField(arguments, "printer", prefix: ": "),
                _ => L("S.Guard.Sum.Other", toolName)
            },
            "shares" => action switch
            {
                "set_private" => L("S.Guard.Sum.SharesPrivate") + FormatField(arguments, "adapter", prefix: ": "),
                "enable_discovery" => L("S.Guard.Sum.SharesDiscovery"),
                "enable_file_sharing" => L("S.Guard.Sum.SharesFiles"),
                "map_drive" => L("S.Guard.Sum.SharesMap") + FormatField(arguments, "letter", prefix: " ", suffix: ":") +
                               FormatField(arguments, "remote_path", prefix: " ← "),
                "unmap_drive" => L("S.Guard.Sum.SharesUnmap") + FormatField(arguments, "letter", prefix: " ", suffix: ":"),
                _ => L("S.Guard.Sum.Other", toolName)
            },
            "bluetooth" => action switch
            {
                "radio_on" => L("S.Guard.Sum.BluetoothOn"),
                "radio_off" => L("S.Guard.Sum.BluetoothOff"),
                "forget" => L("S.Guard.Sum.BluetoothForget") + FormatField(arguments, "device", prefix: ": "),
                _ => L("S.Guard.Sum.Other", toolName)
            },
            "local_users" => action switch
            {
                "enable_user" => L("S.Guard.Sum.UserOn") + FormatField(arguments, "user", prefix: ": "),
                "disable_user" => L("S.Guard.Sum.UserOff") + FormatField(arguments, "user", prefix: ": "),
                "add_to_group" => L("S.Guard.Sum.GroupAdd") + FormatField(arguments, "user", prefix: " ") +
                                  FormatField(arguments, "group", prefix: " → "),
                "remove_from_group" => L("S.Guard.Sum.GroupRemove") + FormatField(arguments, "user", prefix: " ") +
                                       FormatField(arguments, "group", prefix: " ← "),
                _ => L("S.Guard.Sum.Users", action)
            },
            _ => L("S.Guard.Sum.Other", toolName)
        };
    }

    private static string L(string key) => Amarin.Core.Loc.Get(key);

    private static string L(string key, params object[] args) => Amarin.Core.Loc.Format(key, args);

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
            "network" when action is "firewall_disable" or "adapter_disable" or "reset_winsock" or "reset_ip"
                => DangerousRiskLevel.High,
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
            "dns_config" => DangerousRiskLevel.Medium,
            "startup_programs" => DangerousRiskLevel.Medium,
            "windows_update" when action is "install" or "pause" => DangerousRiskLevel.Medium,
            "windows_update" => DangerousRiskLevel.Low,
            "devices" when action is "disable" or "rollback_driver" => DangerousRiskLevel.High,
            "devices" => DangerousRiskLevel.Medium,
            // Открыть RDP — открыть машину для входа по сети; закрыть — оборвать чужие сеансы.
            "remote_access" => DangerousRiskLevel.High,
            "write_clipboard" => DangerousRiskLevel.Low,
            "audio" when action == "restart_services" => DangerousRiskLevel.Medium,
            "file_locks" => Flag(arguments, "force") ? DangerousRiskLevel.High : DangerousRiskLevel.Medium,
            // Права решают, кто что прочтёт и поменяет: любая их правка — высокий риск.
            "permissions" => DangerousRiskLevel.High,
            "environment" => IsMachineScope(arguments) ? DangerousRiskLevel.High : DangerousRiskLevel.Medium,
            "store_apps" when action == "reset" || Flag(arguments, "for_new_users") => DangerousRiskLevel.High,
            "store_apps" => DangerousRiskLevel.Medium,
            "print_queue" when action == "restart_spooler" && Flag(arguments, "purge") => DangerousRiskLevel.High,
            "print_queue" when action is "cancel_job" or "clear_queue" or "restart_spooler" => DangerousRiskLevel.Medium,
            "shares" when action == "enable_file_sharing" => DangerousRiskLevel.High,
            "shares" when action is "set_private" or "enable_discovery" => DangerousRiskLevel.Medium,
            "bluetooth" when action is "radio_off" or "forget" => DangerousRiskLevel.Medium,
            _ => DangerousRiskLevel.Low
        };
    }

    private static bool IsEnvironmentWrite(JsonElement arguments) =>
        ActionOf(arguments) is "set" or "delete" or "path_add" or "path_remove" or "path_clean";

    private static bool IsMachineScope(JsonElement arguments) =>
        FileToolPaths.String(arguments, "scope")?.Trim().Equals("machine", StringComparison.OrdinalIgnoreCase) == true;

    private static bool Flag(JsonElement arguments, string name) => FileToolPaths.Flag(arguments, name);

    /// <summary>Чья громкость меняется: программы или устройства (по умолчанию — вывод по умолчанию).</summary>
    private static string AudioTarget(JsonElement arguments) =>
        FileToolPaths.String(arguments, "app") is { Length: > 0 }
            ? FormatField(arguments, "app", prefix: ": ")
            : FormatField(arguments, "device", prefix: ": ");

    /// <summary>Учётная запись из вызова, а без неё — то, что инструмент возьмёт по умолчанию.</summary>
    private static string Principal(JsonElement arguments, string fallback) =>
        FileToolPaths.String(arguments, "principal")?.Trim() is { Length: > 0 } principal ? Truncate(principal, 80) : fallback;

    private static DangerousRiskLevel ClassifyPowerShellRisk(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("command", out var commandProp))
        {
            return DangerousRiskLevel.Medium;
        }

        var command = commandProp.GetString() ?? string.Empty;

        if (PowerShellCriticalPattern().IsMatch(command))
        {
            return DangerousRiskLevel.Critical;
        }

        if (PowerShellHighRiskPattern().IsMatch(command))
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
        ActionOf(arguments) is "write" or "delete_value" or "delete_key";

    private static bool IsServiceControl(JsonElement arguments) =>
        ActionOf(arguments) is "start" or "stop" or "restart" or "set_start_type";

    private static bool IsFileWrite(JsonElement arguments) =>
        ActionOf(arguments) == "write";

    private static bool IsProcessStop(JsonElement arguments) =>
        ActionOf(arguments) is "stop" or "kill";

    private static bool IsTaskMutation(JsonElement arguments) =>
        ActionOf(arguments) is "create" or "delete" or "enable" or "disable" or "run";

    private static bool IsNetworkMutation(JsonElement arguments) =>
        ActionOf(arguments) is "flush_dns" or "firewall_enable" or "firewall_disable" or "adapter_enable"
            or "adapter_disable" or "wifi_forget" or "reset_winsock" or "reset_ip";

    private static bool IsStartupToggle(JsonElement arguments) =>
        ActionOf(arguments) is "enable" or "disable";

    private static bool IsUpdateMutation(JsonElement arguments) =>
        ActionOf(arguments) is "install" or "hide" or "unhide" or "pause" or "resume";

    private static bool IsDnsMutation(JsonElement arguments) =>
        ActionOf(arguments) is "set_dns" or "reset_dns" or "hosts_add" or "hosts_remove";

    private static bool IsDeviceMutation(JsonElement arguments) =>
        ActionOf(arguments) is "enable" or "disable" or "rollback_driver";

    private static bool IsDefenderAction(JsonElement arguments) =>
        ActionOf(arguments) is "quick_scan" or "update_signatures";

    private static bool IsRdpToggle(JsonElement arguments) =>
        ActionOf(arguments) is "rdp_enable" or "rdp_disable";

    private static int PauseDays(JsonElement arguments) =>
        arguments.TryGetProperty("days", out var days) && days.TryGetInt32(out var value)
            ? Math.Clamp(value, 1, UpdateCommands.MaxPauseDays)
            : 7;

    /// <summary>Список строк от модели одной строкой: «KB1, KB2».</summary>
    private static string FormatList(JsonElement arguments, string field, string prefix = "", string empty = "")
    {
        if (!arguments.TryGetProperty(field, out var value))
        {
            return empty.Length == 0 ? "" : prefix + empty;
        }

        var items = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.ToString()).Where(item => item.Length > 0).ToList()
            : [value.ToString()];
        return items.Count == 0
            ? (empty.Length == 0 ? "" : prefix + empty)
            : prefix + Truncate(string.Join(", ", items), 160);
    }

    private static bool IsVirtualizationMutation(JsonElement arguments) =>
        ActionOf(arguments) is "start_vm" or "stop_vm" or "docker_start" or "docker_stop";

    private static bool IsRollbackRestore(JsonElement arguments) =>
        ActionOf(arguments) == "restore";

    private static bool IsSystemRepairRun(JsonElement arguments) =>
        ActionOf(arguments) is "run_sfc" or "run_dism";

    private static bool IsDiskManagementWrite(JsonElement arguments) =>
        ActionOf(arguments) is "chkdsk_fix";

    private static bool IsDiskSpaceWrite(JsonElement arguments) =>
        ActionOf(arguments) is "cleanup";

    private static bool IsSoftwareInventoryWrite(JsonElement arguments) =>
        ActionOf(arguments) is "install" or "upgrade" or "upgrade_all" or "uninstall";

    private static bool IsFirewallRulesWrite(JsonElement arguments) =>
        ActionOf(arguments) is "enable" or "disable" or "create" or "delete";

    private static bool IsWindowsFeaturesWrite(JsonElement arguments) =>
        ActionOf(arguments) is "enable" or "disable";

    private static bool IsLocalUsersWrite(JsonElement arguments) =>
        ActionOf(arguments) is "enable_user" or "disable_user" or "add_to_group" or "remove_from_group";

    private static bool PowerShellAttemptsDanger(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("command", out var commandProp))
        {
            return false;
        }

        var command = commandProp.GetString() ?? string.Empty;
        return PowerShellDangerousPattern().IsMatch(command);
    }

    /// <summary>
    /// Действие вызова в том виде, в каком его понимает сам инструмент: без пробелов по краям и
    /// в нижнем регистре.
    /// </summary>
    /// <remarks>
    /// Инструменты приводят <c>action</c> к нижнему регистру сами, а проверки здесь сравнивали
    /// строку как есть — и <c>{"action":"WRITE"}</c> исполнялся без вопроса и без снимка.
    /// </remarks>
    internal static string ActionOf(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty("action", out var action) &&
        action.ValueKind == JsonValueKind.String
            ? (action.GetString() ?? "").Trim().ToLowerInvariant()
            : "";

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