using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class DevicesTool : ITool
{
    public string Name => "devices";
    public string Description =>
        "Printers, USB devices, installed drivers, driver problem reports; enable or disable a device and roll " +
        "back its driver by instance_id (from pnp_devices or driver_problems). enable, disable and rollback_driver " +
        "require user confirmation. Disks, keyboards, mice and system devices are never disabled.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["printers", "usb", "drivers", "driver_problems", "pnp_devices", "enable", "disable", "rollback_driver"],
              "description": "Device action"
            },
            "instance_id": {
              "type": "string",
              "description": "Device instance ID for enable/disable/rollback_driver, exactly as pnp_devices shows it"
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

        var action = actionProp.GetString()?.Trim().ToLowerInvariant();
        if (action is "enable" or "disable" or "rollback_driver")
        {
            var instanceId = arguments.TryGetProperty("instance_id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString()?.Trim()
                : null;
            if (!DeviceCommands.IsInstanceId(instanceId))
            {
                return Task.FromResult(ToolResult.Fail(
                    "instance_id is missing or contains wildcards or quotes. Use the exact InstanceId from pnp_devices."));
            }

            return action == "rollback_driver"
                ? Task.Run(() => DriverRollback.Run(instanceId!), cancellationToken)
                : PowerShellHelper.RunAsync(DeviceCommands.SetEnabledScript(instanceId!, action == "enable"), 120,
                    cancellationToken);
        }

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
            : PowerShellHelper.RunAsync(script, 180, cancellationToken);
    }

    private static string Escape(string value) => PowerShellHelper.QuoteLiteral(value);

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