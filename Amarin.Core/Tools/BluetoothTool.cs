using System.Runtime.Versioning;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Bluetooth: радио, сопряжённые устройства и их заряд, включить и выключить радио, забыть
/// устройство.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BluetoothTool : ITool
{
    public const string ToolName = "bluetooth";

    public string Name => ToolName;

    public string Description =>
        "Bluetooth: status shows whether the radio is on, the adapter and the Bluetooth service; devices lists paired " +
        "devices with address, Classic or LE, connection and battery level when the device reports it. radio_on and " +
        "radio_off switch the radio (off disconnects every Bluetooth device, keyboards and mice included), forget " +
        "unpairs a device so it has to be paired again. Changes need user confirmation.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["status", "devices", "radio_on", "radio_off", "forget"],
              "description": "Bluetooth operation"
            },
            "device": {
              "type": "string",
              "description": "forget: the Address or the Id of a device exactly as devices shows it"
            }
          },
          "required": ["action"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var action = DangerousActionGuard.ActionOf(arguments);
        string script;
        switch (action)
        {
            case "status":
                script = BluetoothCommands.StatusScript;
                break;
            case "devices":
                script = BluetoothCommands.DevicesScript;
                break;
            case "radio_on" or "radio_off":
                script = BluetoothCommands.RadioScript(action == "radio_on");
                break;
            case "forget":
                var device = FileToolPaths.String(arguments, "device")?.Trim();
                if (!BluetoothCommands.IsDevice(device))
                {
                    return ToolResult.Fail("device is required: the Address or the Id exactly as devices shows it.");
                }

                script = BluetoothCommands.ForgetScript(device!);
                break;
            default:
                return ToolResult.Fail($"Unknown action: {action}");
        }

        var result = await PowerShellHelper.RunAsync(script, 120, cancellationToken).ConfigureAwait(false);
        var text = PowerShellHelper.ExtractStdout(result.Output);
        if (result.Output.Contains("WINRT_TIMEOUT", StringComparison.Ordinal))
        {
            return ToolResult.Fail(Loc.Get("S.Tool.Bluetooth.Timeout"));
        }

        if (!result.Success)
        {
            return PowerShellHelper.IsAccessDenied(result.Output) ? ToolResult.Fail(Loc.Get("S.Tool.NeedsAdmin")) : result;
        }

        if (text.StartsWith("NO_RADIO", StringComparison.Ordinal))
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Bluetooth.NoRadio", text[8..].Trim()));
        }

        if (text.Contains("NOT_FOUND", StringComparison.Ordinal))
        {
            return ToolResult.Fail(Loc.Get("S.Tool.Bluetooth.NotFound"));
        }

        if (text.StartsWith("RESULT ", StringComparison.Ordinal))
        {
            return Outcome(action, text[7..].Trim());
        }

        return ToolResult.Ok(text);
    }

    /// <summary>Ответ WinRT на просьбу: «Allowed»/«Unpaired» — успех, отказ — с тем, что делать.</summary>
    private static ToolResult Outcome(string action, string reply)
    {
        var status = reply.Split(' ', 2)[0];
        if (action == "forget")
        {
            var name = reply.Length > status.Length ? reply[(status.Length + 1)..] : "";
            return status is "Unpaired" or "AlreadyUnpaired"
                ? ToolResult.Ok($"{name} forgotten: it has to be paired again to connect.")
                : status == "AccessDenied"
                    ? ToolResult.Fail(Loc.Get("S.Tool.NeedsAdmin"))
                    : ToolResult.Fail($"Windows did not unpair the device: {status}.");
        }

        return status == "Allowed"
            ? ToolResult.Ok($"Bluetooth radio turned {(action == "radio_on" ? "on" : "off")}.")
            : ToolResult.Fail(Loc.Format("S.Tool.Bluetooth.Denied", status));
    }
}
