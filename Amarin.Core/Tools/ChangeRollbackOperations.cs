using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
internal static class ChangeRollbackOperations
{
    /// <summary>
    /// Формат снимка. 2 — реестр состоянием (<c>registry_state.json</c>), дописываемым по ходу
    /// запроса; у снимков без номера реестр лежит экспортом <c>.reg</c>.
    /// </summary>
    internal const int SnapshotFormat = 2;

    internal const string RegistryStateFile = "registry_state.json";
    internal const string SessionFile = "session.json";

    /// <summary>
    /// Разделы, которые снимаются всегда: автозагрузка. Всё дерево служб больше не снимается —
    /// тип запуска и состояние служб лежат в <c>services.json</c>, а импорт экспорта
    /// <c>HKLM\…\Services</c> целиком возвращал поверх драйверов, обновлённых с тех пор, их
    /// прежние настройки.
    /// </summary>
    internal static readonly string[] DefaultRegistryPaths =
    [
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
        // Отметки «Диспетчера задач» для 32-битных Run и папок автозагрузки: их правит
        // startup_programs disable/enable, и без них откат не вернул бы выключенный ярлык.
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder"
    ];

    private static readonly JsonSerializerOptions Json = new() { MaxDepth = AppJson.MaxDepth };

    /// <summary>Дописывание снимка — чтение, правка и запись одного файла.</summary>
    private static readonly Lock Gate = new();

    public static SnapshotResult CreateSnapshot(string label, IReadOnlyList<string>? extraRegistryPaths = null)
    {
        using var _ = PerfLog.Measure("undo_snapshot");
        string? dir = null;

        try
        {
            Directory.CreateDirectory(ChangeRollbackStore.Root);
            var id = NewSnapshotId(out dir);

            AppDataFile.WriteAtomic(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(new
            {
                id,
                created = DateTime.Now,
                label,
                machine = Environment.MachineName,
                format = SnapshotFormat
            }));

            var paths = new List<RegistryPath>();
            var skipped = new List<string>();
            foreach (var raw in DefaultRegistryPaths.Concat(extraRegistryPaths ?? []))
            {
                if (RegistryPath.TryParse(raw, out var path) &&
                    !(path.Root == "HKLM" && SensitivePaths.IsSecretHive(path.SubKey)))
                {
                    paths.Add(path);
                }
                else
                {
                    skipped.Add(raw);
                }
            }

            List<ServiceSnapshotEntry> services = [];
            List<ScheduledTaskSnapshotEntry> tasks = [];
            List<StartupProgramSnapshotEntry> startup = [];
            var registry = new RegistrySnapshotSet();

            Parallel.Invoke(
                () => services = ChangeRollbackStore.CaptureCurrentServices(),
                () => tasks = ChangeRollbackStore.CaptureCurrentScheduledTasks(),
                () => startup = ChangeRollbackStore.CaptureStartupPrograms(),
                () =>
                {
                    foreach (var path in paths.DistinctBy(path => path.Canonical, StringComparer.OrdinalIgnoreCase))
                    {
                        registry.Add(RegistryStateIo.Capture(path, deep: true));
                    }
                });

            ChangeRollbackStore.SaveJson(services, Path.Combine(dir, "services.json"));
            ChangeRollbackStore.SaveJson(tasks, Path.Combine(dir, "scheduled_tasks.json"));
            ChangeRollbackStore.SaveJson(startup, Path.Combine(dir, "startup_programs.json"));
            SaveRegistry(dir, registry);

            ChangeRollbackStore.PruneOldSnapshots();

            var message = new StringBuilder()
                .AppendLine($"Snapshot created: {id}")
                .AppendLine($"Path: {dir}")
                .AppendLine($"Services: {services.Count}, Tasks: {tasks.Count}, Startup: {startup.Count}")
                .AppendLine($"Label: {(string.IsNullOrWhiteSpace(label) ? "(none)" : label)}")
                .AppendLine("Registry keys captured:");
            foreach (var root in registry.Roots)
            {
                message.AppendLine($"  {root.Path}: {Describe(root)}");
            }

            foreach (var raw in skipped)
            {
                message.AppendLine($"  skipped (not a registry key path, or a protected hive): {raw}");
            }

            return new SnapshotResult(true, id, message.ToString().TrimEnd());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Любой сбой снимка — ответ, а не авария: без снимка человеку скажут, что откатить
            // будет нечем, а ход продолжится. Прежде здесь тоже ловилось всё.
            if (dir is not null)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // Недописанный снимок в списке не страшен: его папку уберёт очистка старых.
                }
            }

            return new SnapshotResult(false, string.Empty, $"Не удалось создать снимок: {ex.Message}");
        }
    }

    /// <summary>
    /// Дописывает в снимок то, что сейчас изменит вызов: раздел реестра, копию задачи, имя
    /// службы. Зовётся перед каждым изменением, а не только перед первым.
    /// </summary>
    /// <remarks>
    /// Снимок один на запрос, и прежде вторая правка реестра за запрос — в другой раздел — в
    /// него не попадала: откатывать её было нечем. Снимок, которого нет на диске (подменённый
    /// в тестах), не трогается и не создаётся.
    /// </remarks>
    public static void ExtendBeforeMutation(string snapshotId, string toolName, JsonElement arguments)
    {
        var dir = ExistingDir(snapshotId);
        if (dir is null)
        {
            return;
        }

        var action = DangerousActionGuard.ActionOf(arguments);
        try
        {
            switch (toolName.Trim().ToLowerInvariant())
            {
                case "registry" when action is "write" or "delete_value" or "delete_key":
                    if (RegistryPath.TryParse(StringArg(arguments, "path"), out var path, requireSubKey: false) &&
                        path.SubKey.Length > 0)
                    {
                        var capture = action == "delete_key"
                            ? RegistryStateIo.Capture(path, deep: true)
                            : RegistryStateIo.CaptureForWrite(path);
                        lock (Gate)
                        {
                            var set = new RegistrySnapshotSet(LoadRegistry(dir));
                            if (set.Add(capture))
                            {
                                SaveRegistry(dir, set);
                            }
                        }
                    }

                    break;

                case "scheduled_task" when action is "delete" or "create":
                    if (StringArg(arguments, "task_name") is { Length: > 0 } taskName)
                    {
                        SaveTaskCopy(dir, RollbackRules.TaskFullName(taskName));
                    }

                    break;

                // Имя службы у инструмента — service_name. До 1.28.0 здесь читалось «name», поля
                // не находилось никогда, и откат не знал, какие службы трогал запрос.
                case "windows_service" when action is "start" or "stop" or "restart" or "set_start_type":
                    if ((StringArg(arguments, "service_name") ?? StringArg(arguments, "name")) is { Length: > 0 } service)
                    {
                        UpdateSession(dir, record => record.TouchedServices.Add(service.Trim()));
                        if (action == "set_start_type")
                        {
                            // Start и DelayedAutostart — значения ключа службы; «с задержкой»
                            // типом запуска службы не видно, и вернуть его можно только так.
                            ExtendRegistry(dir, ServiceCommands.RegistryKey(service.Trim()));
                        }
                    }

                    break;

                case "windows_update" when action is "pause" or "resume":
                    ExtendRegistry(dir, UpdateCommands.SettingsKey);
                    break;

                case "remote_access" when action is "rdp_enable" or "rdp_disable":
                    ExtendRegistry(dir, SecurityCommands.TerminalServerKey);
                    if (CaptureFirewallRules() is { } rules)
                    {
                        AddUndo(dir, rules);
                    }

                    break;

                case "dns_config" when action is "set_dns" or "reset_dns":
                    if (StringArg(arguments, "adapter") is { } alias && UndoCommands.IsInterfaceAlias(alias) &&
                        CaptureDns(alias.Trim()) is { } dns)
                    {
                        AddUndo(dir, dns);
                    }

                    break;

                case "dns_config" when action is "hosts_add" or "hosts_remove":
                    if (!HasUndo(dir, UndoKind.HostsFile, "") && File.Exists(NetworkCommands.HostsPath))
                    {
                        var copy = "hosts." + Guid.NewGuid().ToString("N")[..8] + ".bak";
                        File.Copy(NetworkCommands.HostsPath, Path.Combine(dir, copy));
                        AddUndo(dir, new UndoStep { Kind = UndoKind.HostsFile, File = copy });
                    }

                    break;

                case "network" when action is "adapter_enable" or "adapter_disable":
                    if (StringArg(arguments, "adapter") is { } adapter && UndoCommands.IsInterfaceAlias(adapter))
                    {
                        var state = PowerShellHelper.Run(NetworkCommands.AdapterStateScript(adapter.Trim()), 60);
                        if (state.Success)
                        {
                            AddUndo(dir, new UndoStep
                            {
                                Kind = UndoKind.Adapter,
                                Target = adapter.Trim(),
                                Enabled = PowerShellHelper.ExtractStdout(state.Output)
                                    .Contains("Up", StringComparison.OrdinalIgnoreCase)
                            });
                        }
                    }

                    break;

                case "network" when action == "wifi_forget":
                    if (StringArg(arguments, "wifi_profile") is { } wifi && NetworkCommands.IsWifiProfileName(wifi) &&
                        ExportWifi(dir, wifi.Trim()) is { } xml)
                    {
                        AddUndo(dir, new UndoStep { Kind = UndoKind.WifiProfile, Target = wifi.Trim(), File = xml });
                    }

                    break;

                case "devices" when action is "enable" or "disable":
                    if (StringArg(arguments, "instance_id") is { } device && DeviceCommands.IsInstanceId(device))
                    {
                        var state = PowerShellHelper.Run(DeviceCommands.StateScript(device.Trim()), 60);
                        if (state.Success)
                        {
                            AddUndo(dir, new UndoStep
                            {
                                Kind = UndoKind.Device,
                                Target = device.Trim(),
                                Enabled = !PowerShellHelper.ExtractStdout(state.Output)
                                    .Contains("disabled", StringComparison.OrdinalIgnoreCase)
                            });
                        }
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Снимок останется без этого раздела — хуже, но не повод не выполнять вызов:
            // человек его уже разрешил.
            PerfLog.Write($"undo_extend_failed {ex.Message}");
        }
    }

    private static void ExtendRegistry(string dir, string key)
    {
        if (!RegistryPath.TryParse(key, out var path, requireSubKey: false) || path.SubKey.Length == 0)
        {
            return;
        }

        var capture = RegistryStateIo.CaptureForWrite(path);
        lock (Gate)
        {
            var set = new RegistrySnapshotSet(LoadRegistry(dir));
            if (set.Add(capture))
            {
                SaveRegistry(dir, set);
            }
        }
    }

    /// <summary>
    /// Дописывает обратный шаг. Второй шаг про ту же цель не пишется: прежнее состояние — у
    /// первого, снятого до первой правки за запрос.
    /// </summary>
    private static void AddUndo(string dir, UndoStep step) =>
        UpdateSession(dir, record =>
        {
            if (!record.UndoSteps.Any(existing => existing.SameTargetAs(step)))
            {
                record.UndoSteps.Add(step);
            }
        });

    private static bool HasUndo(string dir, UndoKind kind, string target) =>
        LoadSession(dir).UndoSteps.Any(step => step.Kind == kind &&
                                               string.Equals(step.Target, target, StringComparison.OrdinalIgnoreCase));

    private static UndoStep? CaptureDns(string alias)
    {
        var result = PowerShellHelper.Run(NetworkCommands.DnsStateScript(alias), 60);
        if (!result.Success)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(PowerShellHelper.ExtractStdout(result.Output));
            var servers = document.RootElement.TryGetProperty("Static", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(item => item.GetString() ?? "").Where(UndoCommands.IsIpAddress).ToList()
                : [];
            return new UndoStep { Kind = UndoKind.Dns, Target = alias, Values = servers, Enabled = servers.Count == 0 };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static UndoStep? CaptureFirewallRules()
    {
        var result = PowerShellHelper.Run(SecurityCommands.RdpFirewallStateScript, 60);
        if (!result.Success)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(PowerShellHelper.ExtractStdout(result.Output));
            if (!document.RootElement.TryGetProperty("Rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var step = new UndoStep { Kind = UndoKind.FirewallRules, Target = SecurityCommands.RdpFirewallGroup };
            foreach (var rule in rules.EnumerateArray())
            {
                if (rule.TryGetProperty("Name", out var name) && name.GetString() is { Length: > 0 } text)
                {
                    step.Values.Add(text);
                    step.States.Add(rule.TryGetProperty("Enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True);
                }
            }

            return step.Values.Count > 0 ? step : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Экспорт профиля Wi-Fi в папку снимка. Ключ остаётся зашифрованным.</summary>
    /// <remarks>
    /// Без <c>key=clear</c>: пароль сети в открытом виде на диске не нужен — экспорт с защищённым
    /// ключом ставится обратно на этой же машине.
    /// </remarks>
    private static string? ExportWifi(string dir, string profile)
    {
        var temp = Path.Combine(dir, "wifi-" + Guid.NewGuid().ToString("N")[..8]);
        if (!NetworkCommands.IsNetshPath(temp))
        {
            return null;
        }

        Directory.CreateDirectory(temp);
        try
        {
            var result = NativeProcess.RunRawAsync("netsh", NetworkCommands.WifiExportArguments(profile, temp), 60)
                .GetAwaiter().GetResult();
            var xml = result.Success ? Directory.GetFiles(temp, "*.xml").FirstOrDefault() : null;
            if (xml is null)
            {
                return null;
            }

            var name = Path.GetFileName(temp) + ".xml";
            File.Move(xml, Path.Combine(dir, name));
            return name;
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>После удачного изменения: запоминает, что создал запрос, — это откат удалит.</summary>
    public static void RecordAfterMutation(string snapshotId, string toolName, JsonElement arguments)
    {
        var dir = ExistingDir(snapshotId);
        if (dir is null ||
            !toolName.Trim().Equals("scheduled_task", StringComparison.OrdinalIgnoreCase) ||
            DangerousActionGuard.ActionOf(arguments) != "create" ||
            StringArg(arguments, "task_name") is not { Length: > 0 } taskName)
        {
            return;
        }

        try
        {
            UpdateSession(dir, record => record.CreatedTasks.Add(RollbackRules.TaskFullName(taskName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            PerfLog.Write($"undo_record_failed {ex.Message}");
        }
    }

    public static ToolResult ListSnapshots()
    {
        if (!Directory.Exists(ChangeRollbackStore.Root))
        {
            return ToolResult.Ok("No snapshots yet.");
        }

        var lines = Directory.GetDirectories(ChangeRollbackStore.Root)
            .Select(Path.GetFileName)
            .OrderDescending()
            .Select(FormatSnapshotLine);

        return ToolResult.Ok(string.Join(Environment.NewLine, lines));
    }

    public static ToolResult SnapshotInfo(string snapshotId)
    {
        var dir = ResolveSnapshotDir(snapshotId, out var error);
        if (dir is null)
        {
            return ToolResult.Fail(error!);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Snapshot: {Path.GetFileName(dir)}");
        sb.AppendLine($"Path: {dir}");

        foreach (var file in new[] { "meta.json", "services.json", "scheduled_tasks.json", "startup_programs.json", SessionFile })
        {
            var path = Path.Combine(dir, file);
            sb.AppendLine(File.Exists(path) ? $"{file}: {new FileInfo(path).Length} bytes" : $"{file}: missing");
        }

        if (File.Exists(Path.Combine(dir, RegistryStateFile)))
        {
            sb.AppendLine("registry keys:");
            foreach (var root in LoadRegistry(dir))
            {
                sb.AppendLine($"  {root.Path}: {Describe(root)}");
            }
        }

        var regDir = Path.Combine(dir, "registry");
        if (Directory.Exists(regDir))
        {
            sb.AppendLine($"registry exports (old format): {Directory.GetFiles(regDir, "*.reg").Length}");
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    /// <summary>Что вернул бы откат — без изменений.</summary>
    public static ToolResult CompareSnapshot(string snapshotId)
    {
        var plan = Plan(snapshotId, out var error);
        return plan is null ? ToolResult.Fail(error!) : ToolResult.Ok(RollbackText.Summary(plan));
    }

    public static ToolResult RestoreSnapshot(string snapshotId)
    {
        using var _ = PerfLog.Measure("undo_restore");
        var plan = Plan(snapshotId, out var error);
        if (plan is null)
        {
            return ToolResult.Fail(error!);
        }

        if (plan.IsEmpty)
        {
            return ToolResult.Ok(RollbackText.Summary(plan));
        }

        var outcomes = Apply(plan);
        var text = RollbackText.Result(outcomes);
        if (plan.Notes.Count > 0)
        {
            text += Environment.NewLine + Environment.NewLine +
                    string.Join(Environment.NewLine, plan.Notes.Select(note => "– " + note));
        }

        return outcomes.All(outcome => outcome.Success) ? ToolResult.Ok(text) : ToolResult.Fail(text);
    }

    /// <summary>
    /// Сравнивает снимок с нынешним состоянием. null — снимка нет; <paramref name="error"/> тогда
    /// объясняет почему.
    /// </summary>
    public static RollbackPlan? Plan(string snapshotId, out string? error)
    {
        using var _ = PerfLog.Measure("undo_plan");
        var dir = ResolveSnapshotDir(snapshotId, out error);
        if (dir is null)
        {
            return null;
        }

        var plan = new RollbackPlan { SnapshotId = Path.GetFileName(dir) };
        var notes = new List<string>();
        var session = LoadSession(dir);

        List<RegistryChange> registry = [];
        List<ServiceChange> services = [];
        List<TaskChange> tasks = [];
        var registryNotes = new List<string>();
        var serviceNotes = new List<string>();
        var taskNotes = new List<string>();
        var startupNotes = new List<string>();

        Parallel.Invoke(
            () =>
            {
                if (File.Exists(Path.Combine(dir, RegistryStateFile)))
                {
                    var changes = LoadRegistry(dir)
                        .SelectMany(root => RegistryDiff.Plan(root, RegistryStateIo.CaptureLike(root)));
                    registry = RollbackRules.FilterRegistry(changes, registryNotes);
                }
            },
            () =>
            {
                var file = Path.Combine(dir, "services.json");
                if (File.Exists(file))
                {
                    services = RollbackRules.PlanServices(
                        ChangeRollbackStore.LoadServices(file),
                        ChangeRollbackStore.CaptureCurrentServices(),
                        session.TouchedServices.ToHashSet(StringComparer.OrdinalIgnoreCase),
                        serviceNotes);
                }
            },
            () =>
            {
                var file = Path.Combine(dir, "scheduled_tasks.json");
                if (File.Exists(file))
                {
                    tasks = RollbackRules.PlanTasks(
                        DeserializeTasks(file),
                        ChangeRollbackStore.CaptureCurrentScheduledTasks(),
                        session.CreatedTasks.ToHashSet(StringComparer.OrdinalIgnoreCase),
                        session.TaskCopies
                            .Where(pair => File.Exists(Path.Combine(dir, pair.Value)))
                            .ToDictionary(pair => pair.Key, pair => Path.Combine(dir, pair.Value),
                                StringComparer.OrdinalIgnoreCase),
                        taskNotes);
                }
            },
            () =>
            {
                var file = Path.Combine(dir, "startup_programs.json");
                if (File.Exists(file))
                {
                    RollbackRules.NoteStartup(
                        DeserializeStartup(file), ChangeRollbackStore.CaptureStartupPrograms(), startupNotes);
                }
            });

        plan.Registry.AddRange(registry);
        plan.Services.AddRange(services);
        plan.Tasks.AddRange(tasks);

        // Обратные шаги — в обратном порядке: последняя правка отменяется первой.
        plan.Undo.AddRange(Enumerable.Reverse(session.UndoSteps));
        plan.SnapshotDirectory = dir;

        // Снимок старого формата: состояния реестра нет, есть только экспорт. Он вернётся
        // импортом, как раньше, — кроме дерева служб, которое затёрло бы обновлённые драйверы.
        var regDir = Path.Combine(dir, "registry");
        if (!File.Exists(Path.Combine(dir, RegistryStateFile)) && Directory.Exists(regDir))
        {
            foreach (var file in Directory.GetFiles(regDir, "*.reg").Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(file).StartsWith("HKLM_SYSTEM_CurrentControlSet_Services",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                plan.LegacyRegFiles.Add(file);
            }

            notes.Add(Loc.Get("S.Rollback.Note.Legacy"));
        }

        plan.Notes.AddRange(notes.Concat(registryNotes).Concat(serviceNotes).Concat(taskNotes).Concat(startupNotes));
        return plan;
    }

    /// <summary>Применяет план по порядку: реестр, службы, задачи. Сбой шага не останавливает остальные.</summary>
    public static IReadOnlyList<RollbackOutcome> Apply(RollbackPlan plan)
    {
        using var _ = PerfLog.Measure("undo_apply");
        var outcomes = new List<RollbackOutcome>();
        outcomes.AddRange(RegistryStateIo.Apply(plan.Registry));

        foreach (var file in plan.LegacyRegFiles)
        {
            var result = ChangeRollbackStore.ImportRegistryFile(file);
            outcomes.Add(new RollbackOutcome(
                Loc.Format("S.Rollback.Legacy.Import", Path.GetFileName(file)),
                result.Success,
                result.Success ? null : Truncate(result.Output, 200)));
        }

        foreach (var change in plan.Services)
        {
            outcomes.AddRange(ApplyService(change));
        }

        foreach (var change in plan.Tasks)
        {
            outcomes.Add(ApplyTask(change));
        }

        foreach (var step in plan.Undo)
        {
            outcomes.Add(ApplyUndo(step, plan.SnapshotDirectory));
        }

        return outcomes;
    }

    private static IEnumerable<RollbackOutcome> ApplyService(ServiceChange change)
    {
        var lines = RollbackText.Describe(change).ToList();
        var index = 0;

        if (change.StartTypeTo is not null)
        {
            var line = lines[index++];
            if (!Enum.TryParse<ServiceStartMode>(change.StartTypeTo, true, out var mode) ||
                !ChangeRollbackStore.TrySetServiceStartType(change.Name, mode, out var error))
            {
                yield return new RollbackOutcome(line, false, Loc.Get("S.Rollback.Service.StartTypeFailed"));
            }
            else
            {
                yield return new RollbackOutcome(line, true, error);
            }
        }

        if (change.Run == ServiceRunAction.None)
        {
            yield break;
        }

        var runLine = lines[index];
        string? failure = null;
        try
        {
            using var service = new ServiceController(change.Name);
            if (change.Run == ServiceRunAction.Start && service.Status != ServiceControllerStatus.Running)
            {
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
            else if (change.Run == ServiceRunAction.Stop && service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or System.ServiceProcess.TimeoutException)
        {
            failure = ex.Message;
        }

        yield return new RollbackOutcome(runLine, failure is null, failure);
    }

    /// <summary>
    /// Обратный шаг. Команда собирается из состояния, сохранённого в снимке, и проверяется здесь
    /// же — снимок лежит в папке, куда пишет пользователь, и доверять ему как коду нельзя.
    /// </summary>
    private static RollbackOutcome ApplyUndo(UndoStep step, string? snapshotDir)
    {
        var line = UndoCommands.Describe(step);
        if (string.IsNullOrEmpty(snapshotDir))
        {
            return new RollbackOutcome(line, false, Loc.Get("S.Rollback.Undo.Invalid"));
        }

        try
        {
            switch (step.Kind)
            {
                case UndoKind.HostsFile:
                    if (UndoCommands.SnapshotFile(step, snapshotDir) is not { } copy)
                    {
                        return new RollbackOutcome(line, false, Loc.Get("S.Rollback.Undo.Invalid"));
                    }

                    File.Copy(copy, NetworkCommands.HostsPath, overwrite: true);
                    return new RollbackOutcome(line, true, null);

                case UndoKind.WifiProfile:
                    if (UndoCommands.WifiRestoreArguments(step, snapshotDir) is not { } netsh)
                    {
                        return new RollbackOutcome(line, false, Loc.Get("S.Rollback.Undo.Invalid"));
                    }

                    var added = NativeProcess.RunRawAsync("netsh", netsh, 60).GetAwaiter().GetResult();
                    return new RollbackOutcome(line, added.Success, added.Success ? null : Truncate(added.Output, 200));

                default:
                    if (UndoCommands.Script(step, snapshotDir) is not { } script)
                    {
                        return new RollbackOutcome(line, false, Loc.Get("S.Rollback.Undo.Invalid"));
                    }

                    var result = PowerShellHelper.Run(script, 120);
                    return new RollbackOutcome(line, result.Success, result.Success ? null : Truncate(result.Output, 200));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RollbackOutcome(line, false, ex.Message);
        }
    }

    private static RollbackOutcome ApplyTask(TaskChange change)
    {
        var line = RollbackText.Describe(change);
        ToolResult result = change.Kind switch
        {
            TaskChangeKind.Enable => NativeProcess.Run(
                "schtasks.exe", ChangeRollbackStore.TaskToggleArguments(change.FullName, disable: false), 60),
            TaskChangeKind.Disable => NativeProcess.Run(
                "schtasks.exe", ChangeRollbackStore.TaskToggleArguments(change.FullName, disable: true), 60),
            TaskChangeKind.Delete => NativeProcess.Run(
                "schtasks.exe", ChangeRollbackStore.TaskDeleteArguments(change.FullName), 60),
            _ => ChangeRollbackStore.RegisterTaskFromXml(change.FullName, change.XmlFile!)
        };

        return new RollbackOutcome(line, result.Success, result.Success ? null : Truncate(result.Output, 200));
    }

    public static IReadOnlyList<string>? ParseExtraRegistryPaths(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("include_registry_paths", out var pathsProp) ||
            pathsProp.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var paths = new List<string>();
        foreach (var item in pathsProp.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var path = item.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
            }
        }

        return paths.Count == 0 ? null : paths;
    }

    /// <summary>Копия задачи до удаления или перезаписи — чтобы откат мог её вернуть.</summary>
    private static void SaveTaskCopy(string dir, string fullName)
    {
        lock (Gate)
        {
            if (LoadSession(dir).TaskCopies.Keys.Any(name =>
                    string.Equals(name, fullName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
        }

        var xml = ChangeRollbackStore.ExportTaskXml(fullName);
        if (xml is null)
        {
            // Задачи с таким именем ещё нет — копировать нечего.
            return;
        }

        var file = Path.Combine("tasks", Hash(fullName) + ".xml");
        Directory.CreateDirectory(Path.Combine(dir, "tasks"));
        AppDataFile.WriteAtomic(Path.Combine(dir, file), xml);
        UpdateSession(dir, record => record.TaskCopies.TryAdd(fullName, file));
    }

    private static void UpdateSession(string dir, Action<SnapshotSessionRecord> change)
    {
        lock (Gate)
        {
            var record = LoadSession(dir);
            change(record);
            AppDataFile.WriteAtomic(Path.Combine(dir, SessionFile), JsonSerializer.Serialize(record, Json));
        }
    }

    internal static SnapshotSessionRecord LoadSession(string dir)
    {
        var path = Path.Combine(dir, SessionFile);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<SnapshotSessionRecord>(File.ReadAllText(path), Json) ?? new()
                : new SnapshotSessionRecord();
        }
        catch (JsonException)
        {
            return new SnapshotSessionRecord();
        }
    }

    internal static List<RegistryKeyState> LoadRegistry(string dir)
    {
        var path = Path.Combine(dir, RegistryStateFile);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<RegistryKeyState>>(File.ReadAllText(path), Json) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void SaveRegistry(string dir, RegistrySnapshotSet set) =>
        AppDataFile.WriteAtomic(Path.Combine(dir, RegistryStateFile), JsonSerializer.Serialize(set.Roots, Json));

    private static string Describe(RegistryKeyState root)
    {
        if (!root.Exists)
        {
            return "absent (rollback removes it if it appears)";
        }

        var (keys, values) = Count(root);
        var text = root.Shallow ? $"{values} values" : $"{keys} keys, {values} values";
        return root.Truncated ? text + " (partial)" : text;
    }

    private static (int Keys, int Values) Count(RegistryKeyState node)
    {
        var keys = 1;
        var values = node.Values.Count;
        foreach (var child in node.SubKeys)
        {
            var (k, v) = Count(child);
            keys += k;
            values += v;
        }

        return (keys, values);
    }

    /// <summary>
    /// Имя папки — время до секунды, а два снимка в одну секунду делили бы папку и переписывали
    /// файлы друг друга.
    /// </summary>
    private static string NewSnapshotId(out string dir)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        for (var attempt = 1; ; attempt++)
        {
            var id = attempt == 1 ? stamp : $"{stamp}_{attempt}";
            dir = Path.Combine(ChangeRollbackStore.Root, id);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                return id;
            }
        }
    }

    private static string? ExistingDir(string snapshotId)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return null;
        }

        var dir = Path.Combine(ChangeRollbackStore.Root, Path.GetFileName(snapshotId.Trim()));
        return Directory.Exists(dir) ? dir : null;
    }

    private static string? ResolveSnapshotDir(string snapshotId, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            error = "Missing required parameter: snapshot_id";
            return null;
        }

        var id = Path.GetFileName(snapshotId.Trim());
        var dir = Path.Combine(ChangeRollbackStore.Root, id);
        if (!Directory.Exists(dir))
        {
            error = $"Snapshot not found: {id}";
            return null;
        }

        return dir;
    }

    private static string FormatSnapshotLine(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        var metaPath = Path.Combine(ChangeRollbackStore.Root, id, "meta.json");
        if (!File.Exists(metaPath))
        {
            return id;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            var label = doc.RootElement.TryGetProperty("label", out var l) ? l.GetString() : "";
            return string.IsNullOrWhiteSpace(label) ? id : $"{id} - {label}";
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return id;
        }
    }

    private static string? StringArg(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToUpperInvariant())))[..16];

    private static List<ScheduledTaskSnapshotEntry> DeserializeTasks(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<List<ScheduledTaskSnapshotEntry>>(File.ReadAllText(path)) ?? []
            : [];

    private static List<StartupProgramSnapshotEntry> DeserializeStartup(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<List<StartupProgramSnapshotEntry>>(File.ReadAllText(path)) ?? []
            : [];

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}

/// <summary>Что запрос успел сделать после снимка — то, чего по одному состоянию не узнать.</summary>
internal sealed class SnapshotSessionRecord
{
    /// <summary>Службы, которые запускал или останавливал запрос: им откат вернёт и состояние.</summary>
    public List<string> TouchedServices { get; set; } = [];

    /// <summary>Задачи, созданные запросом: откат их удалит (или вернёт перезаписанные).</summary>
    public List<string> CreatedTasks { get; set; } = [];

    /// <summary>Копии XML задач до удаления или перезаписи: полное имя → файл в папке снимка.</summary>
    public Dictionary<string, string> TaskCopies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Обратные шаги по порядку записи: DNS, hosts, адаптеры, устройства, правила, Wi-Fi.</summary>
    public List<UndoStep> UndoSteps { get; set; } = [];
}

internal sealed record SnapshotResult(bool Success, string SnapshotId, string Message);
