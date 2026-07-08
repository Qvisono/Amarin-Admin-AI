using System.ServiceProcess;
using System.Text;
using System.Text.Json;

namespace Amarin.Tools;

internal static class ChangeRollbackStore
{
    private const int MaxSnapshots = 20;

    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmarinAdminAI", "snapshots");

    public static void PruneOldSnapshots()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        var dirs = Directory.GetDirectories(Root)
            .OrderDescending()
            .Skip(MaxSnapshots)
            .ToList();

        foreach (var dir in dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // ignore prune failures
            }
        }
    }

    public static List<ServiceSnapshotEntry> LoadServices(string path) =>
        File.Exists(path) ? DeserializeList<ServiceSnapshotEntry>(File.ReadAllText(path)) : [];

    public static List<ServiceSnapshotEntry> CaptureCurrentServices()
    {
        var result = PowerShellHelper.Run(
            "Get-Service | Select-Object Name, Status, StartType | ConvertTo-Json -Depth 3 -Compress");
        if (!result.Success)
        {
            return [];
        }

        return DeserializeList<ServiceSnapshotEntry>(PowerShellHelper.ExtractStdout(result.Output));
    }

    public static string CompareServices(IReadOnlyList<ServiceSnapshotEntry> old, IReadOnlyList<ServiceSnapshotEntry> current)
    {
        var oldMap = old.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();

        foreach (var service in current.OrderBy(s => s.Name))
        {
            if (!oldMap.TryGetValue(service.Name, out var previous))
            {
                continue;
            }

            if (!previous.Status.Equals(service.Status, StringComparison.OrdinalIgnoreCase) ||
                !previous.StartType.Equals(service.StartType, StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine(
                    $"{service.Name}: Status {previous.Status} → {service.Status}, StartType {previous.StartType} → {service.StartType}");
            }
        }

        return sb.Length == 0 ? "Изменений в службах не найдено." : sb.ToString().TrimEnd();
    }

    public static string RestoreServices(IReadOnlyList<ServiceSnapshotEntry> snapshot)
    {
        var sb = new StringBuilder();
        var ok = 0;
        var failed = 0;
        var skipped = 0;

        foreach (var item in snapshot)
        {
            try
            {
                using var service = new ServiceController(item.Name);

                if (!Enum.TryParse<ServiceStartMode>(item.StartType, true, out _))
                {
                    skipped++;
                    sb.AppendLine($"SKIP {item.Name}: неизвестный StartType {item.StartType}");
                    continue;
                }

                var setType = PowerShellHelper.Run(
                    $"Set-Service -Name '{item.Name.Replace("'", "''", StringComparison.Ordinal)}' " +
                    $"-StartupType {item.StartType} -ErrorAction Stop");
                if (!setType.Success)
                {
                    failed++;
                    sb.AppendLine($"FAIL {item.Name}: не удалось установить StartType");
                    continue;
                }

                if (item.Status.Equals("Running", StringComparison.OrdinalIgnoreCase) &&
                    service.Status != ServiceControllerStatus.Running)
                {
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                }
                else if (item.Status.Equals("Stopped", StringComparison.OrdinalIgnoreCase) &&
                         service.Status != ServiceControllerStatus.Stopped)
                {
                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }

                ok++;
                sb.AppendLine($"OK {item.Name}");
            }
            catch (Exception ex)
            {
                failed++;
                sb.AppendLine($"FAIL {item.Name}: {ex.Message}");
            }
        }

        sb.Insert(0, $"Службы: ok={ok}, fail={failed}, skip={skipped}{Environment.NewLine}");
        return sb.ToString().TrimEnd();
    }

    public static List<ScheduledTaskSnapshotEntry> CaptureCurrentScheduledTasks()
    {
        var result = PowerShellHelper.Run(
            "Get-ScheduledTask | Select-Object TaskName, TaskPath, State | ConvertTo-Json -Depth 3 -Compress");
        if (!result.Success)
        {
            return [];
        }

        return DeserializeList<ScheduledTaskSnapshotEntry>(PowerShellHelper.ExtractStdout(result.Output));
    }

    public static string CompareScheduledTasks(
        IReadOnlyList<ScheduledTaskSnapshotEntry> old,
        IReadOnlyList<ScheduledTaskSnapshotEntry> current)
    {
        var oldMap = old.ToDictionary(t => TaskKey(t), StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();

        foreach (var task in current.OrderBy(t => TaskKey(t)))
        {
            var key = TaskKey(task);
            if (!oldMap.TryGetValue(key, out var previous))
            {
                continue;
            }

            if (!previous.State.Equals(task.State, StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"{key}: State {previous.State} → {task.State}");
            }
        }

        return sb.Length == 0 ? "Изменений в задачах планировщика не найдено." : sb.ToString().TrimEnd();
    }

    public static string RestoreScheduledTasks(IReadOnlyList<ScheduledTaskSnapshotEntry> snapshot)
    {
        var sb = new StringBuilder();
        var ok = 0;
        var failed = 0;
        var skipped = 0;

        foreach (var task in snapshot)
        {
            var fullName = BuildTaskFullName(task);
            if (string.IsNullOrWhiteSpace(fullName))
            {
                skipped++;
                continue;
            }

            var action = task.State.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
                ? "/disable"
                : "/enable";

            var result = PowerShellHelper.Run($"schtasks {action} /tn \"{fullName}\" 2>&1", 60);
            if (result.Success)
            {
                ok++;
                sb.AppendLine($"OK {fullName} → {task.State}");
            }
            else
            {
                failed++;
                sb.AppendLine($"FAIL {fullName}: {Truncate(result.Output, 120)}");
            }
        }

        sb.Insert(0, $"Задачи: ok={ok}, fail={failed}, skip={skipped}{Environment.NewLine}");
        return sb.ToString().TrimEnd();
    }

    public static List<StartupProgramSnapshotEntry> CaptureStartupPrograms()
    {
        var script = """
            $items = @()
            Get-CimInstance Win32_StartupCommand -ErrorAction SilentlyContinue | ForEach-Object {
              $items += [PSCustomObject]@{
                Source = 'WMI'
                Name = $_.Name
                Command = $_.Command
                Location = $_.Location
              }
            }
            $runPaths = @(
              @{ Source='HKLM_Run'; Path='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' },
              @{ Source='HKCU_Run'; Path='HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' }
            )
            foreach ($entry in $runPaths) {
              if (Test-Path $entry.Path) {
                Get-ItemProperty $entry.Path -ErrorAction SilentlyContinue |
                  Get-Member -MemberType NoteProperty |
                  Where-Object { $_.Name -notin @('PSPath','PSParentPath','PSChildName','PSDrive','PSProvider') } |
                  ForEach-Object {
                    $name = $_.Name
                    $cmd = (Get-ItemProperty $entry.Path).$name
                    if ($cmd) {
                      $items += [PSCustomObject]@{
                        Source = $entry.Source
                        Name = $name
                        Command = [string]$cmd
                        Location = $entry.Path
                      }
                    }
                  }
              }
            }
            $items | ConvertTo-Json -Depth 4 -Compress
            """;

        var result = PowerShellHelper.Run(script, 120);
        if (!result.Success)
        {
            return [];
        }

        return DeserializeList<StartupProgramSnapshotEntry>(PowerShellHelper.ExtractStdout(result.Output));
    }

    public static string CompareStartupPrograms(
        IReadOnlyList<StartupProgramSnapshotEntry> old,
        IReadOnlyList<StartupProgramSnapshotEntry> current)
    {
        var oldMap = old.ToDictionary(p => StartupKey(p), StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();

        foreach (var program in current.OrderBy(p => StartupKey(p)))
        {
            var key = StartupKey(program);
            if (!oldMap.TryGetValue(key, out var previous))
            {
                sb.AppendLine($"NEW {key}: {Truncate(program.Command, 80)}");
                continue;
            }

            if (!previous.Command.Equals(program.Command, StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"{key}: Command changed");
                sb.AppendLine($"  было: {Truncate(previous.Command, 100)}");
                sb.AppendLine($"  стало: {Truncate(program.Command, 100)}");
            }
        }

        var currentKeys = current.Select(StartupKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var removed in old.Where(p => !currentKeys.Contains(StartupKey(p))))
        {
            sb.AppendLine($"REMOVED {StartupKey(removed)}");
        }

        return sb.Length == 0 ? "Изменений в автозагрузке не найдено." : sb.ToString().TrimEnd();
    }

    public static string SaveJson<T>(IReadOnlyList<T> items, string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(items));
        return path;
    }

    private static string TaskKey(ScheduledTaskSnapshotEntry task) =>
        $"{task.TaskPath?.TrimEnd('\\')}\\{task.TaskName}".TrimStart('\\');

    private static string BuildTaskFullName(ScheduledTaskSnapshotEntry task)
    {
        var path = task.TaskPath?.TrimEnd('\\') ?? string.Empty;
        return string.IsNullOrWhiteSpace(path) || path == "\\"
            ? task.TaskName
            : $"{path}\\{task.TaskName}";
    }

    private static string StartupKey(StartupProgramSnapshotEntry entry) =>
        $"{entry.Source}:{entry.Name}";

    private static List<T> DeserializeList<T>(string json)
    {
        json = ExtractJsonPayload(json);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? [];
        }
        catch (JsonException)
        {
            try
            {
                var single = JsonSerializer.Deserialize<T>(json);
                return single is null ? [] : [single];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    private static string ExtractJsonPayload(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var start = text.IndexOfAny(['[', '{']);
        return start < 0 ? string.Empty : text[start..].TrimEnd();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}