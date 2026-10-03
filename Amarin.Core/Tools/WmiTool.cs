using System.Management;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Management.Infrastructure;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class WmiTool : ITool
{
    public string Name => "wmi_query";
    public string Description =>
        "Query Windows WMI/CIM for hardware, BIOS, installed programs, drivers, and system details.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "scope": {
              "type": "string",
              "enum": ["os", "bios", "cpu", "memory", "disks", "gpu", "installed_programs", "drivers", "battery"],
              "description": "Information category to query"
            },
            "filter": {
              "type": "string",
              "description": "Optional substring filter for programs/drivers"
            }
          },
          "required": ["scope"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!arguments.TryGetProperty("scope", out var scopeProp))
            {
                return Task.FromResult(ToolResult.Fail("Missing required parameter: scope"));
            }

            var scope = scopeProp.GetString()?.ToLowerInvariant();
            arguments.TryGetProperty("filter", out var filterProp);
            var filter = filterProp.ValueKind == JsonValueKind.String ? filterProp.GetString() : null;

            return scope switch
            {
                "os" => Task.FromResult(Query("SELECT Caption,Version,BuildNumber,OSArchitecture,LastBootUpTime FROM Win32_OperatingSystem")),
                "bios" => Task.FromResult(Query("SELECT Manufacturer,SMBIOSBIOSVersion,ReleaseDate,SerialNumber FROM Win32_BIOS")),
                "cpu" => Task.FromResult(Query("SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed FROM Win32_Processor")),
                "memory" => Task.FromResult(Query("SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem")),
                "disks" => Task.FromResult(Query("SELECT Model,Size,InterfaceType,MediaType FROM Win32_DiskDrive")),
                "gpu" => Task.FromResult(Query("SELECT Name,AdapterRAM,DriverVersion FROM Win32_VideoController")),
                "installed_programs" => Task.FromResult(QueryPrograms(filter)),
                "drivers" => Task.FromResult(QueryDrivers(filter)),
                "battery" => Task.FromResult(Query("SELECT EstimatedChargeRemaining,BatteryStatus FROM Win32_Battery")),
                _ => Task.FromResult(ToolResult.Fail($"Unknown scope: {scope}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"WMI error: {ex.Message}"));
        }
    }

    private static ToolResult Query(string wql)
    {
        try
        {
            return QueryCim(wql);
        }
        catch
        {
            return QueryDcom(wql);
        }
    }

    private static ToolResult QueryCim(string wql)
    {
        var sb = new StringBuilder();
        using var session = CimSession.Create(null);
        var count = 0;
        foreach (var instance in session.QueryInstances(@"root\cimv2", "WQL", wql))
        {
            using (instance)
            {
                if (count++ >= 50)
                {
                    sb.AppendLine("… [обрезано]");
                    break;
                }

                foreach (var prop in instance.CimInstanceProperties)
                {
                    sb.AppendLine($"{prop.Name}: {prop.Value}");
                }

                sb.AppendLine();
            }
        }

        return ToolResult.Ok(sb.Length == 0 ? "Нет данных." : sb.ToString().TrimEnd());
    }

    private static ToolResult QueryDcom(string wql)
    {
        var sb = new StringBuilder();
        using var searcher = new ManagementObjectSearcher(wql);
        var count = 0;
        foreach (var obj in searcher.Get())
        {
            if (count++ >= 50)
            {
                sb.AppendLine("… [обрезано]");
                break;
            }

            foreach (var prop in obj.Properties)
            {
                sb.AppendLine($"{prop.Name}: {prop.Value}");
            }

            sb.AppendLine();
        }

        return ToolResult.Ok(sb.Length == 0 ? "Нет данных." : sb.ToString().TrimEnd());
    }

    private static ToolResult QueryPrograms(string? filter)
    {
        var entries = InstalledProgramsCatalog.Query(filter, max: 80);
        if (entries.Count == 0)
        {
            return ToolResult.Ok("Программы не найдены.");
        }

        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            sb.AppendLine($"{entry.Name} | {entry.Version} | {entry.Publisher}");
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult QueryDrivers(string? filter)
    {
        try
        {
            return QueryDriversCim(filter);
        }
        catch
        {
            return QueryDriversDcom(filter);
        }
    }

    private static ToolResult QueryDriversCim(string? filter)
    {
        var sb = new StringBuilder();
        using var session = CimSession.Create(null);
        var count = 0;
        foreach (var instance in session.QueryInstances(
                     @"root\cimv2",
                     "WQL",
                     "SELECT DeviceName,DriverVersion,Manufacturer FROM Win32_PnPSignedDriver"))
        {
            using (instance)
            {
                var name = instance.CimInstanceProperties["DeviceName"]?.Value?.ToString() ?? "";
                if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (count++ >= 80)
                {
                    break;
                }

                sb.AppendLine(
                    $"{name} | {instance.CimInstanceProperties["DriverVersion"]?.Value} | {instance.CimInstanceProperties["Manufacturer"]?.Value}");
            }
        }

        return ToolResult.Ok(sb.Length == 0 ? "Драйверы не найдены." : sb.ToString().TrimEnd());
    }

    private static ToolResult QueryDriversDcom(string? filter)
    {
        var sb = new StringBuilder();
        using var searcher = new ManagementObjectSearcher("SELECT DeviceName,DriverVersion,Manufacturer FROM Win32_PnPSignedDriver");
        var count = 0;
        foreach (var obj in searcher.Get())
        {
            var name = obj["DeviceName"]?.ToString() ?? "";
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (count++ >= 80)
            {
                break;
            }

            sb.AppendLine($"{name} | {obj["DriverVersion"]} | {obj["Manufacturer"]}");
        }

        return ToolResult.Ok(sb.Length == 0 ? "Драйверы не найдены." : sb.ToString().TrimEnd());
    }
}