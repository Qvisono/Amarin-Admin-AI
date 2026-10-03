using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Amarin.Tools;

public sealed class SystemInfoTool : ITool
{
    public string Name => "system_info";
    public string Description =>
        "Collect Windows system information: OS version, hardware, uptime, environment, admin status.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "scope": {
              "type": "string",
              "enum": ["summary", "environment", "drives", "network"],
              "description": "Information scope to collect"
            }
          }
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var scope = "summary";
        if (arguments.TryGetProperty("scope", out var scopeProp) &&
            scopeProp.ValueKind == JsonValueKind.String)
        {
            scope = scopeProp.GetString() ?? "summary";
        }

        try
        {
            var result = scope.ToLowerInvariant() switch
            {
                "environment" => GetEnvironmentInfo(),
                "drives" => GetDriveInfo(),
                "network" => GetNetworkInfo(),
                _ => GetSummary()
            };

            return Task.FromResult(ToolResult.Ok(result));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"System info error: {ex.Message}"));
        }
    }

    private static string GetSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Machine: {Environment.MachineName}");
        sb.AppendLine($"User: {Environment.UserDomainName}\\{Environment.UserName}");
        sb.AppendLine($"Admin: {IsAdministrator()}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Architecture: {RuntimeInformation.OSArchitecture} / {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"Processors: {Environment.ProcessorCount}");
        sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"64-bit Process: {Environment.Is64BitProcess}");
        sb.AppendLine($"Uptime: {TimeSpan.FromMilliseconds(Environment.TickCount64):dd\\.hh\\:mm\\:ss}");
        sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        return sb.ToString().TrimEnd();
    }

    private static string GetEnvironmentInfo()
    {
        var sb = new StringBuilder();
        foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>().OrderBy(k => k))
        {
            sb.AppendLine($"{key}={Environment.GetEnvironmentVariable(key)}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string GetDriveInfo()
    {
        var sb = new StringBuilder();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            sb.AppendLine(
                $"{drive.Name} {drive.DriveType} | {drive.DriveFormat} | " +
                $"{drive.VolumeLabel} | {FormatBytes(drive.AvailableFreeSpace)} free / {FormatBytes(drive.TotalSize)} total");
        }

        return sb.Length == 0 ? "No drives found." : sb.ToString().TrimEnd();
    }

    private static string GetNetworkInfo()
    {
        var sb = new StringBuilder();
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up))
        {
            sb.AppendLine($"{nic.Name} ({nic.NetworkInterfaceType})");
            var props = nic.GetIPProperties();
            foreach (var addr in props.UnicastAddresses)
            {
                sb.AppendLine($"  {addr.Address}");
            }
        }

        return sb.Length == 0 ? "No active network interfaces." : sb.ToString().TrimEnd();
    }

    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}