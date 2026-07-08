using System.Runtime.Versioning;
using System.Text.Json;

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

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("action", out var actionProp))
        {
            return Task.FromResult(ToolResult.Fail("Missing required parameter: action"));
        }

        var top = GetInt(arguments, "top", 10, 1, 25);
        var drive = arguments.TryGetProperty("drive", out var driveProp) &&
                    driveProp.ValueKind == JsonValueKind.String
            ? (driveProp.GetString() ?? "C").TrimEnd(':')
            : "C";

        var action = actionProp.GetString()?.ToLowerInvariant();
        var script = action switch
        {
            "summary" => SummaryScript(),
            "cpu" => CpuScript(),
            "memory" => MemoryScript(),
            "disk" => DiskScript(drive),
            "top_processes" => TopProcessesScript(top),
            _ => null
        };

        return script is null
            ? Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            : Task.FromResult(PowerShellHelper.Run(script, 90));
    }

    private static string SummaryScript() => """
        $os = Get-CimInstance Win32_OperatingSystem
        $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
        $uptime = (Get-Date) - $os.LastBootUpTime
        $memUsed = [math]::Round(($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / 1MB, 1)
        $memTotal = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
        $memPct = [math]::Round(100 * $memUsed / $memTotal, 1)
        $load = (Get-Counter '\Processor(_Total)\% Processor Time' -ErrorAction SilentlyContinue).CounterSamples.CookedValue
        [PSCustomObject]@{
          UptimeDays = [math]::Round($uptime.TotalDays, 2)
          CPU = "$([math]::Round($load, 1))%"
          Memory = "$memUsed / $memTotal GB ($memPct%)"
          Processor = $cpu.Name
        } | Format-List
        Get-PSDrive -PSProvider FileSystem | Select-Object Name,
          @{N='UsedGB';E={[math]::Round(($_.Used)/1GB,1)}},
          @{N='FreeGB';E={[math]::Round(($_.Free)/1GB,1)}},
          @{N='TotalGB';E={[math]::Round(($_.Used+$_.Free)/1GB,1)}} |
          Format-Table -AutoSize
        """;

    private static string CpuScript() => """
        Get-Counter '\Processor(_Total)\% Processor Time','\System\Processor Queue Length' -SampleInterval 1 -MaxSamples 2 |
          Select-Object -ExpandProperty CounterSamples |
          Group-Object Path | ForEach-Object {
            $avg = ($_.Group | Measure-Object CookedValue -Average).Average
            [PSCustomObject]@{ Counter = $_.Name; Value = [math]::Round($avg, 2) }
          } | Format-Table -AutoSize
        Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, LoadPercentage |
          Format-Table -AutoSize
        """;

    private static string MemoryScript() => """
        $os = Get-CimInstance Win32_OperatingSystem
        $cs = Get-CimInstance Win32_ComputerSystem
        [PSCustomObject]@{
          TotalGB = [math]::Round($cs.TotalPhysicalMemory/1GB, 1)
          FreeGB = [math]::Round($os.FreePhysicalMemory/1MB, 1)
          UsedGB = [math]::Round(($os.TotalVisibleMemorySize - $os.FreePhysicalMemory)/1MB, 1)
          CommitGB = [math]::Round(($os.TotalVirtualMemorySize - $os.FreeVirtualMemory)/1MB, 1)
          PageFileGB = [math]::Round($os.SizeStoredInPagingFiles/1MB, 1)
        } | Format-List
        """;

    private static string DiskScript(string drive) =>
        "$vol = Get-Volume -DriveLetter '" + drive + "' -ErrorAction SilentlyContinue\n" +
        "if ($vol) {\n" +
        "  $vol | Select-Object DriveLetter, FileSystemLabel, FileSystem, HealthStatus,\n" +
        "    @{N='SizeGB';E={[math]::Round($_.Size/1GB,1)}},\n" +
        "    @{N='FreeGB';E={[math]::Round($_.SizeRemaining/1GB,1)}} | Format-List\n" +
        "}\n" +
        "Get-Counter \"\\LogicalDisk(" + drive + ":)\\% Disk Time\",\"\\LogicalDisk(" + drive + ":)\\Avg. Disk Queue Length\" -ErrorAction SilentlyContinue |\n" +
        "  Select-Object -ExpandProperty CounterSamples |\n" +
        "  Format-Table Path, CookedValue -AutoSize";

    private static string TopProcessesScript(int top) =>
        "Get-Process | Sort-Object CPU -Descending | Select-Object -First " + top + " |\n" +
        "  Select-Object Id, ProcessName,\n" +
        "    @{N='CPU_s';E={[math]::Round($_.CPU,1)}},\n" +
        "    @{N='MemMB';E={[math]::Round($_.WorkingSet64/1MB,1)}} |\n" +
        "  Format-Table -AutoSize";

    private static int GetInt(JsonElement args, string name, int defaultValue, int min, int max)
    {
        if (!args.TryGetProperty(name, out var prop) || !prop.TryGetInt32(out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }
}