using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class PerformanceTool : ITool
{
    public string Name => "performance";
    public string Description =>
        "System performance counters: CPU, memory, disk usage, top processes. Read-only diagnostics.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["summary", "cpu", "memory", "disk", "top_processes"],
              "description": "Performance diagnostic action"
            },
            "drive": {
              "type": "string",
              "description": "Drive letter for disk action, e.g. C"
            },
            "top": {
              "type": "integer",
              "description": "Number of processes for top_processes (default 10, max 25)"
            }
          },
          "required": ["action"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("action", out var actionProp))
        {
            return ToolResult.Fail("Missing required parameter: action");
        }

        var top = GetInt(arguments, "top", 10, 1, 25);
        var drive = arguments.TryGetProperty("drive", out var driveProp) &&
                    driveProp.ValueKind == JsonValueKind.String
            ? (driveProp.GetString() ?? "C").TrimEnd(':')
            : "C";

        var action = actionProp.GetString()?.ToLowerInvariant();
        try
        {
            return action switch
            {
                "summary" => await SummaryAsync(cancellationToken),
                "cpu" => await CpuAsync(cancellationToken),
                "memory" => Memory(),
                "disk" => Disk(drive),
                "top_processes" => TopProcesses(top),
                _ => ToolResult.Fail($"Unknown action: {action}")
            };
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Performance error: {ex.Message}");
        }
    }

    private static async Task<ToolResult> SummaryAsync(CancellationToken cancellationToken)
    {
        var cpu = await ReadCpuPercentAsync(sampleMs: 250, cancellationToken);
        var mem = ReadMemory();
        var sb = new StringBuilder();
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        sb.AppendLine($"UptimeDays: {uptime.TotalDays:0.##}");
        sb.AppendLine($"CPU: {(cpu is null ? "n/a" : $"{cpu.Value:0.#}%")}");
        if (mem is not null)
        {
            sb.AppendLine($"Memory: {BytesToGb(mem.UsedPhys)} / {BytesToGb(mem.TotalPhys)} GB ({mem.Load}%)");
        }

        sb.AppendLine($"Processor: {ReadProcessorName()}");
        sb.AppendLine();
        AppendDrives(sb);
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static async Task<ToolResult> CpuAsync(CancellationToken cancellationToken)
    {
        var cpu = await ReadCpuPercentAsync(sampleMs: 1000, cancellationToken);
        var queue = ReadCounter("System", "Processor Queue Length", null);
        var sb = new StringBuilder();
        sb.AppendLine($"Processor Time: {(cpu is null ? "n/a" : $"{cpu.Value:0.##}%")}");
        sb.AppendLine($"Processor Queue Length: {(queue is null ? "n/a" : $"{queue.Value:0.##}")}");
        sb.AppendLine($"Name: {ReadProcessorName()}");
        sb.AppendLine($"LogicalProcessors: {Environment.ProcessorCount}");
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult Memory()
    {
        var mem = ReadMemory();
        if (mem is null)
        {
            return ToolResult.Fail("Не удалось прочитать сведения о памяти.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"TotalGB: {BytesToGb(mem.TotalPhys):0.#}");
        sb.AppendLine($"FreeGB: {BytesToGb(mem.AvailPhys):0.#}");
        sb.AppendLine($"UsedGB: {BytesToGb(mem.UsedPhys):0.#}");
        sb.AppendLine($"CommitGB: {BytesToGb(mem.TotalPage - mem.AvailPage):0.#}");
        sb.AppendLine($"PageFileGB: {BytesToGb(mem.TotalPage):0.#}");
        sb.AppendLine($"Load: {mem.Load}%");
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult Disk(string drive)
    {
        var letter = drive.Trim();
        if (letter.Length == 0)
        {
            letter = "C";
        }

        var root = letter.EndsWith(':') || letter.EndsWith('\\')
            ? letter
            : letter + ":\\";
        if (root.Length == 2)
        {
            root += "\\";
        }

        DriveInfo? info = null;
        try
        {
            info = new DriveInfo(root);
        }
        catch
        {
            // invalid
        }

        if (info is null || !info.IsReady)
        {
            return ToolResult.Fail($"Диск {drive} недоступен.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"DriveLetter: {info.Name}");
        sb.AppendLine($"FileSystemLabel: {info.VolumeLabel}");
        sb.AppendLine($"FileSystem: {info.DriveFormat}");
        sb.AppendLine($"SizeGB: {BytesToGb(info.TotalSize):0.#}");
        sb.AppendLine($"FreeGB: {BytesToGb(info.AvailableFreeSpace):0.#}");

        var diskTime = ReadCounter("LogicalDisk", "% Disk Time", info.Name.TrimEnd('\\'));
        var queue = ReadCounter("LogicalDisk", "Avg. Disk Queue Length", info.Name.TrimEnd('\\'));
        if (diskTime is not null)
        {
            sb.AppendLine($"DiskTime: {diskTime.Value:0.##}%");
        }

        if (queue is not null)
        {
            sb.AppendLine($"AvgQueue: {queue.Value:0.##}");
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult TopProcesses(int top)
    {
        var rows = new List<(int Pid, string Name, double Cpu, double MemMb)>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                rows.Add((
                    process.Id,
                    process.ProcessName,
                    process.TotalProcessorTime.TotalSeconds,
                    process.WorkingSet64 / (1024.0 * 1024.0)));
            }
            catch
            {
                // ignore inaccessible
            }
            finally
            {
                process.Dispose();
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("Id ProcessName CPU_s MemMB");
        foreach (var row in rows.OrderByDescending(r => r.Cpu).Take(top))
        {
            sb.AppendLine($"{row.Pid} {row.Name} {row.Cpu:0.#} {row.MemMb:0.#}");
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static void AppendDrives(StringBuilder sb)
    {
        sb.AppendLine("Name UsedGB FreeGB TotalGB");
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            try
            {
                sb.AppendLine(
                    $"{drive.Name.TrimEnd('\\')} {BytesToGb(drive.TotalSize - drive.AvailableFreeSpace):0.#} {BytesToGb(drive.AvailableFreeSpace):0.#} {BytesToGb(drive.TotalSize):0.#}");
            }
            catch
            {
                // skip
            }
        }
    }

    private static async Task<double?> ReadCpuPercentAsync(int sampleMs, CancellationToken cancellationToken)
    {
        try
        {
            using var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            _ = counter.NextValue();
            await Task.Delay(Math.Clamp(sampleMs, 150, 1500), cancellationToken);
            return counter.NextValue();
        }
        catch
        {
            return null;
        }
    }

    private static double? ReadCounter(string category, string name, string? instance)
    {
        try
        {
            using var counter = instance is null
                ? new PerformanceCounter(category, name, readOnly: true)
                : new PerformanceCounter(category, name, instance, readOnly: true);
            _ = counter.NextValue();
            return counter.NextValue();
        }
        catch
        {
            return null;
        }
    }

    private static string ReadProcessorName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") as string ?? "n/a";
        }
        catch
        {
            return "n/a";
        }
    }

    private sealed record MemoryInfo(ulong TotalPhys, ulong AvailPhys, ulong UsedPhys, ulong TotalPage, ulong AvailPage, uint Load);

    private static MemoryInfo? ReadMemory()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status))
        {
            return null;
        }

        return new MemoryInfo(
            status.ullTotalPhys,
            status.ullAvailPhys,
            status.ullTotalPhys - status.ullAvailPhys,
            status.ullTotalPageFile,
            status.ullAvailPageFile,
            status.dwMemoryLoad);
    }

    private static double BytesToGb(long bytes) => bytes / 1073741824.0;

    private static double BytesToGb(ulong bytes) => bytes / 1073741824.0;

    private static int GetInt(JsonElement args, string name, int defaultValue, int min, int max)
    {
        if (!args.TryGetProperty(name, out var prop) || !prop.TryGetInt32(out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MemoryStatusEx()
        {
            dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        }
    }
}
