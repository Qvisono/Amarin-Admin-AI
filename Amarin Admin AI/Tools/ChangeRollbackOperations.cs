using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
internal static class ChangeRollbackOperations
{
    private static readonly string[] DefaultRegistryPaths =
    [
        @"HKLM\SYSTEM\CurrentControlSet\Services",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
    ];

    public static SnapshotResult CreateSnapshot(string label, IReadOnlyList<string>? extraRegistryPaths = null)
    {
        string? dir = null;

        try
        {
            Directory.CreateDirectory(ChangeRollbackStore.Root);

            var id = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            dir = Path.Combine(ChangeRollbackStore.Root, id);
            Directory.CreateDirectory(dir);

            File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(new
            {
                id,
                created = DateTime.Now,
                label,
                machine = Environment.MachineName
            }));

            var services = ChangeRollbackStore.CaptureCurrentServices();
            ChangeRollbackStore.SaveJson(services, Path.Combine(dir, "services.json"));

            var tasks = ChangeRollbackStore.CaptureCurrentScheduledTasks();
            ChangeRollbackStore.SaveJson(tasks, Path.Combine(dir, "scheduled_tasks.json"));

            var startup = ChangeRollbackStore.CaptureStartupPrograms();
            ChangeRollbackStore.SaveJson(startup, Path.Combine(dir, "startup_programs.json"));

            var regPaths = new List<string>(DefaultRegistryPaths);
            if (extraRegistryPaths is not null)
            {
                regPaths.AddRange(extraRegistryPaths.Where(p => !string.IsNullOrWhiteSpace(p)));
            }

            var regDir = Path.Combine(dir, "registry");
            Directory.CreateDirectory(regDir);
            var regLog = new StringBuilder();

            foreach (var regPath in regPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var fileName = regPath.Replace('\\', '_').Replace(':', '_') + ".reg";
                var outFile = Path.Combine(regDir, fileName);
                var result = PowerShellHelper.Run($"reg export \"{regPath}\" \"{outFile}\" /y 2>&1");
                regLog.AppendLine($"{regPath}: {(result.Success ? "ok" : "failed")}");
            }

            ChangeRollbackStore.PruneOldSnapshots();

            var message =
                $"Snapshot created: {id}\nPath: {dir}\n" +
                $"Services: {services.Count}, Tasks: {tasks.Count}, Startup: {startup.Count}\n" +
                $"Label: {(string.IsNullOrWhiteSpace(label) ? "(none)" : label)}\nRegistry exports:\n{regLog}";

            return new SnapshotResult(true, id, message);
        }
        catch (Exception ex)
        {
            if (dir is not null)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // ignore cleanup failures
                }
            }

            return new SnapshotResult(false, string.Empty, $"Не удалось создать снимок: {ex.Message}");
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

        foreach (var file in new[] { "meta.json", "services.json", "scheduled_tasks.json", "startup_programs.json" })
        {
            var path = Path.Combine(dir, file);
            sb.AppendLine(File.Exists(path) ? $"{file}: {new FileInfo(path).Length} bytes" : $"{file}: missing");
        }

        var regDir = Path.Combine(dir, "registry");
        if (Directory.Exists(regDir))
        {
            sb.AppendLine($"registry exports: {Directory.GetFiles(regDir, "*.reg").Length}");
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    public static ToolResult CompareSnapshot(string snapshotId)
    {
        var dir = ResolveSnapshotDir(snapshotId, out var error);
        if (dir is null)
        {
            return ToolResult.Fail(error!);
        }

        var sb = new StringBuilder();
        sb.AppendLine("=== Services ===");
        var oldServices = ChangeRollbackStore.LoadServices(Path.Combine(dir, "services.json"));
        var currentServices = ChangeRollbackStore.CaptureCurrentServices();
        sb.AppendLine(ChangeRollbackStore.CompareServices(oldServices, currentServices));

        sb.AppendLine();
        sb.AppendLine("=== Scheduled Tasks ===");
        var oldTasks = DeserializeTasks(Path.Combine(dir, "scheduled_tasks.json"));
        var currentTasks = ChangeRollbackStore.CaptureCurrentScheduledTasks();
        sb.AppendLine(ChangeRollbackStore.CompareScheduledTasks(oldTasks, currentTasks));

        sb.AppendLine();
        sb.AppendLine("=== Startup Programs ===");
        var oldStartup = DeserializeStartup(Path.Combine(dir, "startup_programs.json"));
        var currentStartup = ChangeRollbackStore.CaptureStartupPrograms();
        sb.AppendLine(ChangeRollbackStore.CompareStartupPrograms(oldStartup, currentStartup));

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    public static ToolResult RestoreSnapshot(string snapshotId)
    {
        var dir = ResolveSnapshotDir(snapshotId, out var error);
        if (dir is null)
        {
            return ToolResult.Fail(error!);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Restoring snapshot {Path.GetFileName(dir)}...");

        var servicesFile = Path.Combine(dir, "services.json");
        if (File.Exists(servicesFile))
        {
            var services = ChangeRollbackStore.LoadServices(servicesFile);
            sb.AppendLine(ChangeRollbackStore.RestoreServices(services));
        }

        var tasksFile = Path.Combine(dir, "scheduled_tasks.json");
        if (File.Exists(tasksFile))
        {
            sb.AppendLine();
            sb.AppendLine(ChangeRollbackStore.RestoreScheduledTasks(DeserializeTasks(tasksFile)));
        }

        var regDir = Path.Combine(dir, "registry");
        if (Directory.Exists(regDir))
        {
            var regLog = new StringBuilder();
            var ok = 0;
            var failed = 0;

            foreach (var regFile in Directory.GetFiles(regDir, "*.reg"))
            {
                var import = PowerShellHelper.Run($"reg import \"{regFile}\" 2>&1");
                if (import.Success)
                {
                    ok++;
                    regLog.AppendLine($"OK {Path.GetFileName(regFile)}");
                }
                else
                {
                    failed++;
                    regLog.AppendLine($"FAIL {Path.GetFileName(regFile)}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("=== Registry ===");
            sb.AppendLine($"ok={ok}, fail={failed}");
            sb.Append(regLog);
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
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
            return string.IsNullOrWhiteSpace(label) ? id : $"{id} — {label}";
        }
        catch
        {
            return id;
        }
    }

    private static List<ScheduledTaskSnapshotEntry> DeserializeTasks(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<List<ScheduledTaskSnapshotEntry>>(File.ReadAllText(path)) ?? []
            : [];

    private static List<StartupProgramSnapshotEntry> DeserializeStartup(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<List<StartupProgramSnapshotEntry>>(File.ReadAllText(path)) ?? []
            : [];
}

internal sealed record SnapshotResult(bool Success, string SnapshotId, string Message);