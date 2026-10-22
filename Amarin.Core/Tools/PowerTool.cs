using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Электропитание: схемы, что не даёт уснуть, что разбудило компьютер, износ батареи.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerTool : ITool
{
    public const string ToolName = "power";

    public string Name => ToolName;

    public string Description =>
        "Power and sleep: schemes lists the power plans and the active one; requests shows what keeps the PC or the " +
        "display awake (needs administrator rights); wake shows what woke the PC last time and the timers that will " +
        "wake it; battery reports design and full charge capacity, wear and cycle count. set_scheme switches the " +
        "active plan and needs user confirmation.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["schemes", "requests", "wake", "battery", "set_scheme"],
              "description": "Power operation"
            },
            "scheme": {
              "type": "string",
              "description": "set_scheme: the plan GUID or its exact name as schemes shows it"
            }
          },
          "required": ["action"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var action = DangerousActionGuard.ActionOf(arguments);
        return action switch
        {
            "schemes" => Schemes(),
            "set_scheme" => SetScheme(FileToolPaths.String(arguments, "scheme")?.Trim()),
            "requests" => IsElevated()
                ? Text(await PowerShellHelper.RunAsync("powercfg /requests 2>&1", 60, cancellationToken).ConfigureAwait(false))
                : ToolResult.Fail(Loc.Get("S.Tool.NeedsAdmin")),
            "wake" => await WakeAsync(cancellationToken).ConfigureAwait(false),
            "battery" => await BatteryAsync(cancellationToken).ConfigureAwait(false),
            _ => ToolResult.Fail($"Unknown action: {action}")
        };
    }

    private static ToolResult Schemes()
    {
        var schemes = PowerSchemes.List();
        if (schemes.Count == 0)
        {
            return ToolResult.Fail("Windows returned no power plans.");
        }

        var sb = new StringBuilder();
        foreach (var scheme in schemes)
        {
            sb.AppendLine($"{(scheme.Active ? "* " : "  ")}{scheme.Name}  {scheme.Id}");
        }

        sb.Append("* active");
        return ToolResult.Ok(sb.ToString());
    }

    private static ToolResult SetScheme(string? scheme)
    {
        if (string.IsNullOrEmpty(scheme))
        {
            return ToolResult.Fail("scheme is required: a GUID or a name from schemes.");
        }

        var schemes = PowerSchemes.List();
        var matches = Guid.TryParse(scheme, out var id)
            ? schemes.Where(item => item.Id == id).ToList()
            : schemes.Where(item => item.Name.Equals(scheme, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (matches.Count != 1)
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Power.UnknownScheme", scheme));
        }

        var code = PowerSchemes.SetActive(matches[0].Id);
        return code == 0
            ? ToolResult.Ok($"Active power plan: {matches[0].Name} ({matches[0].Id}).")
            : code == 5
                ? ToolResult.Fail(Loc.Get("S.Tool.NeedsAdmin"))
                : ToolResult.Fail($"Windows refused to switch the plan (code {code}).");
    }

    /// <summary>
    /// Что разбудило в прошлый раз и что разбудит. Таймеры пробуждения Windows отдаёт только
    /// администратору: без прав они не спрашиваются вовсе, иначе их отказ погубил бы и ответ о
    /// последнем пробуждении.
    /// </summary>
    private static async Task<ToolResult> WakeAsync(CancellationToken cancellationToken)
    {
        var elevated = IsElevated();
        var script = "Write-Output '--- last wake ---'\npowercfg /lastwake" +
                     (elevated ? "\nWrite-Output '--- wake timers ---'\npowercfg /waketimers" : "");
        var result = Text(await PowerShellHelper.RunAsync(script, 60, cancellationToken).ConfigureAwait(false));
        return result.Success && !elevated
            ? ToolResult.Ok(result.Output + "\n" + Loc.Get("S.Tool.Power.TimersNeedAdmin"))
            : result;
    }

    private static async Task<ToolResult> BatteryAsync(CancellationToken cancellationToken)
    {
        var status = PowerSchemes.Status();
        if (!status.HasBattery)
        {
            return ToolResult.Ok("This computer has no battery.");
        }

        var file = Path.Combine(Path.GetTempPath(), "amarin-battery-" + Guid.NewGuid().ToString("N")[..8] + ".xml");
        try
        {
            var result = await NativeProcess.RunAsync("powercfg.exe", ["/batteryreport", "/xml", "/output", file], 120,
                cancellationToken).ConfigureAwait(false);
            if (!File.Exists(file))
            {
                return result.Success
                    ? ToolResult.Fail("powercfg did not write the battery report.")
                    : ToolResult.Ok("No battery information: Windows reports no battery for this computer.");
            }

            var (batteries, history) = BatteryReport.Parse(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false));
            return batteries.Count == 0
                ? ToolResult.Ok("This computer has no battery.")
                : ToolResult.Ok(BatteryReport.Format(batteries, history, status));
        }
        catch (XmlException ex)
        {
            return ToolResult.Fail($"The battery report could not be read: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Временный файл уберёт очистка папки TEMP.
            }
        }
    }

    /// <summary>Вывод powercfg: только stdout, он уже в UTF-8 после обёртки PowerShell.</summary>
    private static ToolResult Text(ToolResult result)
    {
        var text = PowerShellHelper.ExtractStdout(result.Output);
        return result.Success ? ToolResult.Ok(text.Length == 0 ? "powercfg returned nothing." : text) : ToolResult.Fail(result.Output);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
