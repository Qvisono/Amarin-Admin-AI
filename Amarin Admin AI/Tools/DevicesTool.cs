using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class DevicesTool : ITool
{
    public string Name => "devices";
    public string Description =>
        "Printers, USB devices, installed drivers, and driver problem reports. Read-only diagnostics.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["printers", "usb", "drivers", "driver_problems", "pnp_devices"],
              "description": "Device diagnostic action"
            },
            "filter": {
              "type": "string",
              "description": "Optional name filter substring"
            },
            "max_items": {
              "type": "integer",
              "description": "Max items (default 40, max 150)"
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

        var max = GetInt(arguments, "max_items", 40, 1, 150);
        var filter = arguments.TryGetProperty("filter", out var filterProp) &&
                       filterProp.ValueKind == JsonValueKind.String
            ? filterProp.GetString() ?? ""
            : "";

        var action = actionProp.GetString()?.ToLowerInvariant();
        var script = action switch
        {
            "printers" => PrintersScript(filter, max),
            "usb" => UsbScript(filter, max),
            "drivers" => DriversScript(filter, max),
            "driver_problems" => DriverProblemsScript(max),
            "pnp_devices" => PnpScript(filter, max),
            _ => null
        };

        return script is null
            ? Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            : Task.FromResult(PowerShellHelper.Run(script, 180));
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string PrintersScript(string filter, int max) => $$"""
        Get-Printer -ErrorAction SilentlyContinue |
          Where-Object { '{{Escape(filter)}}' -eq '' -or $_.Name -like '*{{Escape(filter)}}*' } |
          Select-Object -First {{max}} Name, DriverName, PortName, PrinterStatus, Shared | Format-Table -Wrap
        Get-PrintJob -ErrorAction SilentlyContinue | Select-Object -First 10 PrinterName, Id, Status | Format-Table
        """;

    private static string UsbScript(string filter, int max) => $$"""
        Get-PnpDevice -Class USB -ErrorAction SilentlyContinue |
          Where-Object { '{{Escape(filter)}}' -eq '' -or $_.FriendlyName -like '*{{Escape(filter)}}*' } |
          Select-Object -First {{max}} Status, Class, FriendlyName, InstanceId | Format-Table -Wrap
        Get-CimInstance Win32_USBHub -ErrorAction SilentlyContinue | Select-Object -First {{max}} Name, DeviceID | Format-Table
        """;

    private static string DriversScript(string filter, int max) => $$"""
        Get-WindowsDriver -Online -ErrorAction SilentlyContinue |
          Where-Object { '{{Escape(filter)}}' -eq '' -or $_.Driver -like '*{{Escape(filter)}}*' } |
          Select-Object -First {{max}} Driver, ClassName, ProviderName, Date, Version | Format-Table -Wrap
        """;

    private static string DriverProblemsScript(int max) => $$"""
        Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-Kernel-PnP' } -MaxEvents 200 -ErrorAction SilentlyContinue |
          Where-Object { $_.Id -in 219,411 } |
          Select-Object -First {{max}} TimeCreated, Id, Message | Format-List
        pnputil /enum-devices /problem /connected 2>&1
        """;

    private static string PnpScript(string filter, int max) => $$"""
        Get-PnpDevice -ErrorAction SilentlyContinue |
          Where-Object { '{{Escape(filter)}}' -eq '' -or $_.FriendlyName -like '*{{Escape(filter)}}*' } |
          Select-Object -First {{max}} Status, Class, FriendlyName, InstanceId | Format-Table -Wrap
        """;

    private static int GetInt(JsonElement args, string name, int defaultValue, int min, int max)
    {
        if (!args.TryGetProperty(name, out var prop) || !prop.TryGetInt32(out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }
}