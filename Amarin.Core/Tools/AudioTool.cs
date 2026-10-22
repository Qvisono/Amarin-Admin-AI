using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Звук: устройства вывода и ввода, устройство по умолчанию, громкость и звук устройства или
/// отдельной программы, перезапуск звуковых служб.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioTool : ITool
{
    public const string ToolName = "audio";

    /// <summary>
    /// Службы звука: построитель конечных точек и сама «Windows Audio», которая от него зависит.
    /// Остальные зависимые (службы производителя звуковой карты) поднимаются, если работали.
    /// </summary>
    private const string EndpointBuilder = "AudioEndpointBuilder";

    public string Name => ToolName;

    public string Description =>
        "Sound: devices lists output and input devices with state, default and communications device, volume and " +
        "mute; sessions lists the volume of each program that plays sound. set_default makes a device the default, " +
        "set_volume and set_mute change a device or one program, restart_services restarts the Windows audio services " +
        "(sound stops for a few seconds). Changes need user confirmation.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["devices", "sessions", "set_default", "set_volume", "set_mute", "restart_services"],
              "description": "Sound operation"
            },
            "device": {
              "type": "string",
              "description": "Device id or name as devices shows it. set_volume and set_mute use the default output device when neither device nor app is given"
            },
            "app": {
              "type": "string",
              "description": "set_volume and set_mute: a program from sessions, by process name or PID; its own volume changes, not the device's"
            },
            "volume": {
              "type": "integer",
              "description": "set_volume: 0-100"
            },
            "mute": {
              "type": "boolean",
              "description": "set_mute: true turns the sound off, false turns it back on"
            },
            "role": {
              "type": "string",
              "enum": ["default", "communications", "all"],
              "description": "set_default: default device, default communications device, or both (default: default)"
            }
          },
          "required": ["action"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        Task.Run(() => Execute(arguments), cancellationToken);

    private static ToolResult Execute(JsonElement arguments)
    {
        var action = DangerousActionGuard.ActionOf(arguments);
        try
        {
            return action switch
            {
                "devices" => Devices(),
                "sessions" => Sessions(),
                "set_default" => SetDefault(arguments),
                "set_volume" or "set_mute" => SetLevel(arguments, action == "set_volume"),
                "restart_services" => RestartServices(),
                _ => ToolResult.Fail($"Unknown action: {action}")
            };
        }
        catch (COMException ex)
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Audio.Failed", $"0x{ex.HResult:X8}"));
        }
        catch (Exception ex) when (ex is InvalidCastException or InvalidComObjectException)
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Audio.Failed", ex.Message));
        }
    }

    private static ToolResult Devices()
    {
        var devices = CoreAudio.Devices();
        if (devices.Count == 0)
        {
            return ToolResult.Ok(AudioServiceRunning() ? "No sound devices." : Loc.Get("S.Tool.Audio.ServiceStopped"));
        }

        var sb = new StringBuilder();
        foreach (var output in new[] { true, false })
        {
            sb.AppendLine(output ? "Output devices:" : "Input devices:");
            foreach (var device in devices.Where(item => item.Output == output).OrderByDescending(item => item.IsDefault))
            {
                var marks = (device.IsDefault ? " [default]" : "") + (device.IsCommunications ? " [communications]" : "");
                var level = device.Volume is { } volume ? $"  volume {volume}%" + (device.Muted == true ? " muted" : "") : "";
                sb.AppendLine($"  {device.Name}{marks}  {device.State}{level}");
                sb.AppendLine($"    id: {device.Id}");
            }
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult Sessions()
    {
        var sessions = CoreAudio.Sessions();
        if (sessions.Count == 0)
        {
            return ToolResult.Ok("No program has an open sound session.");
        }

        var sb = new StringBuilder();
        foreach (var group in sessions.GroupBy(session => session.DeviceName))
        {
            sb.AppendLine($"{group.Key}:");
            foreach (var session in group)
            {
                var who = session.SystemSounds ? session.App : $"{session.App} (PID {session.Pid})";
                sb.AppendLine($"  {who}: {session.Volume}%{(session.Muted ? ", muted" : "")}{(session.Active ? ", playing" : "")}");
            }
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult SetDefault(JsonElement arguments)
    {
        if (!TryDevice(FileToolPaths.String(arguments, "device"), out var device, out var error))
        {
            return ToolResult.Fail(error);
        }

        if (device.State != "active")
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Audio.NotActive", device.Name, device.State));
        }

        var role = FileToolPaths.String(arguments, "role")?.Trim().ToLowerInvariant() ?? "default";
        if (role is not ("default" or "communications" or "all"))
        {
            return ToolResult.Fail("role must be default, communications or all. Do not retry with the same value.");
        }

        CoreAudio.SetDefault(device.Id, normal: role is "default" or "all", communications: role is "communications" or "all");
        var what = role switch
        {
            "communications" => "default communications device",
            "all" => "default device and default communications device",
            _ => "default device"
        };
        return ToolResult.Ok($"{device.Name} is now the {what} for {(device.Output ? "output" : "input")}.");
    }

    private static ToolResult SetLevel(JsonElement arguments, bool volumeChange)
    {
        int? volume = null;
        bool? mute = null;
        if (volumeChange)
        {
            if (FileToolPaths.Int(arguments, "volume") is not { } level || level is < 0 or > 100)
            {
                return ToolResult.Fail("volume is required: an integer from 0 to 100.");
            }

            volume = level;
        }
        else
        {
            if (!arguments.TryGetProperty("mute", out var muteProp) || muteProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return ToolResult.Fail("mute is required: true or false.");
            }

            mute = muteProp.GetBoolean();
        }

        var what = volume is { } value ? $"volume {value}%" : mute == true ? "muted" : "unmuted";
        if (FileToolPaths.String(arguments, "app")?.Trim() is { Length: > 0 } app)
        {
            var name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
            var pid = int.TryParse(name, out var number) ? number : (int?)null;
            var changed = CoreAudio.SetApp(
                (sessionPid, sessionApp) => sessionPid == pid ||
                                            sessionApp.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                            ProcessName(sessionPid).Equals(name, StringComparison.OrdinalIgnoreCase),
                volume,
                mute);
            return changed == 0
                ? ToolResult.Fail(Loc.Format("S.Tool.Audio.NoApp", app))
                : ToolResult.Ok($"{app}: {what} ({changed} sound session{(changed == 1 ? "" : "s")}).");
        }

        if (!TryDevice(FileToolPaths.String(arguments, "device"), out var device, out var error))
        {
            return ToolResult.Fail(error);
        }

        CoreAudio.SetDevice(device.Id, volume, mute);
        return ToolResult.Ok($"{device.Name}: {what}.");
    }

    private static ToolResult RestartServices()
    {
        try
        {
            using var builder = new ServiceController(EndpointBuilder);
            var dependents = builder.DependentServices
                .Where(service => service.Status == ServiceControllerStatus.Running)
                .Select(service => service.ServiceName)
                .ToList();

            if (builder.Status != ServiceControllerStatus.Stopped)
            {
                // Stop останавливает и зависимые: «Windows Audio» без построителя не работает.
                builder.Stop();
                builder.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }

            builder.Start();
            builder.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            foreach (var name in dependents.Prepend("Audiosrv").Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var service = new ServiceController(name);
                if (service.Status != ServiceControllerStatus.Running)
                {
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                }
            }

            return ToolResult.Ok($"Audio services restarted: {EndpointBuilder}, {string.Join(", ", dependents.Prepend("Audiosrv").Distinct(StringComparer.OrdinalIgnoreCase))}.");
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 5 })
        {
            return ToolResult.Fail(Loc.Get("S.Tool.NeedsAdmin"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            return ToolResult.Fail($"Audio services did not restart: {ex.Message}");
        }
    }

    /// <summary>Устройство по id, имени или единственному совпадению части имени; пусто — вывод по умолчанию.</summary>
    private static bool TryDevice(string? raw, out AudioDevice device, out string error)
    {
        device = null!;
        error = "";
        var devices = CoreAudio.Devices();
        var wanted = raw?.Trim() ?? "";
        List<AudioDevice> matches;
        if (wanted.Length == 0)
        {
            matches = devices.Where(item => item.Output && item.IsDefault).ToList();
        }
        else
        {
            matches = devices.Where(item => item.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                matches = devices.Where(item => item.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (matches.Count == 0)
            {
                matches = devices.Where(item => item.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }

        if (matches.Count == 1)
        {
            device = matches[0];
            return true;
        }

        error = matches.Count == 0
            ? Loc.Format("S.Tool.Audio.NoDevice", wanted.Length == 0 ? "default output" : wanted)
            : Loc.Format("S.Tool.Audio.Ambiguous", wanted, string.Join("; ", matches.Select(item => $"{item.Name} ({(item.Output ? "output" : "input")}) id {item.Id}")));
        return false;
    }

    private static bool AudioServiceRunning()
    {
        try
        {
            using var service = new ServiceController("Audiosrv");
            return service.Status == ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "";
        }
    }
}
