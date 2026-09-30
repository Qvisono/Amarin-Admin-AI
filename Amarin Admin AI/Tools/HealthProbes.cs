using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;

namespace Amarin.Tools;

/// <summary>Логический диск: сколько всего и сколько свободно.</summary>
internal sealed record DriveHealth(string Name, long TotalBytes, long FreeBytes)
{
    public double FreePercent => TotalBytes <= 0 ? 0 : FreeBytes * 100.0 / TotalBytes;
}

/// <summary>Физический диск по <c>Get-PhysicalDisk</c>: состояние, как его видит Windows.</summary>
internal sealed record PhysicalDiskHealth(string Name, string HealthStatus, string OperationalStatus)
{
    public bool Healthy => HealthStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Нагрузка и аптайм.</summary>
internal sealed record SystemHealth(double? CpuPercent, uint? MemoryLoad, TimeSpan Uptime);

/// <summary>Защитник Windows. <see cref="Available"/> false — стоит другой антивирус или служба недоступна.</summary>
internal sealed record DefenderHealth(bool Available, bool? AntivirusEnabled, bool? RealTimeEnabled, int? SignatureAgeDays);

internal sealed record FirewallHealth(string Profile, bool Enabled);

internal sealed record SecurityHealth(DefenderHealth Defender, IReadOnlyList<FirewallHealth> Firewall);

/// <summary>События за сутки: критические, ошибки и падения программ.</summary>
internal sealed record EventHealth(int Critical, int Errors, int AppCrashes);

/// <summary>Нужна ли перезагрузка и сколько обновлений ждёт; null — не удалось узнать.</summary>
internal sealed record UpdateHealth(bool RebootPending, int? Pending);

/// <summary>
/// Пробы для панели «Состояние ПК» (C4): те же источники, что у инструментов, но ответ —
/// данные, а не текст для модели.
/// </summary>
/// <remarks>
/// Всё, что можно прочесть из .NET (диски, память, аптайм, ключи перезагрузки), читается напрямую:
/// запуск powershell.exe стоит полсекунды, а панель опрашивает пять вещей разом. Остальное — один
/// скрипт на пробу, ответ JSON, разбор — отдельными функциями, чтобы их проверяли тесты без Windows.
/// </remarks>
internal static class HealthProbes
{
    /// <summary>Сколько ждать одну пробу. Поиск обновлений бывает долгим — ему своё.</summary>
    private const int ProbeTimeoutSeconds = 45;
    private const int UpdateSearchTimeoutSeconds = 120;

    public static IReadOnlyList<DriveHealth> Drives()
    {
        var drives = new List<DriveHealth>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    drives.Add(new DriveHealth(drive.Name.TrimEnd('\\'), drive.TotalSize, drive.AvailableFreeSpace));
                }
            }
            catch (IOException)
            {
                // Диск отвалился между перечислением и чтением — просто не показываем.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return drives;
    }

    public const string PhysicalDiskScript = """
        @(Get-PhysicalDisk -ErrorAction SilentlyContinue |
          Select-Object FriendlyName, @{n='HealthStatus';e={"$($_.HealthStatus)"}}, @{n='OperationalStatus';e={"$($_.OperationalStatus)"}}) |
          ConvertTo-Json -Compress
        """;

    public static async Task<IReadOnlyList<PhysicalDiskHealth>?> PhysicalDisksAsync(CancellationToken cancellationToken) =>
        ParsePhysicalDisks(await StdoutAsync(PhysicalDiskScript, ProbeTimeoutSeconds, cancellationToken).ConfigureAwait(false));

    internal static IReadOnlyList<PhysicalDiskHealth>? ParsePhysicalDisks(string? json) =>
        Items(json)?.Select(item => new PhysicalDiskHealth(
                Text(item, "FriendlyName") ?? "?",
                Text(item, "HealthStatus") ?? "",
                Text(item, "OperationalStatus") ?? ""))
            .ToList();

    public static async Task<SystemHealth> SystemAsync(CancellationToken cancellationToken)
    {
        var cpu = await PerformanceTool.ReadCpuPercentAsync(sampleMs: 500, cancellationToken).ConfigureAwait(false);
        return new SystemHealth(cpu, PerformanceTool.ReadMemory()?.Load, TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    public const string SecurityScript = """
        $d = Get-MpComputerStatus -ErrorAction SilentlyContinue
        $f = @(Get-NetFirewallProfile -ErrorAction SilentlyContinue | ForEach-Object { @{ Name = "$($_.Name)"; Enabled = [bool]$_.Enabled } })
        @{
          Defender = if ($d) { @{ AntivirusEnabled = [bool]$d.AntivirusEnabled; RealTimeProtectionEnabled = [bool]$d.RealTimeProtectionEnabled; AntivirusSignatureAge = [int]$d.AntivirusSignatureAge } } else { $null }
          Firewall = $f
        } | ConvertTo-Json -Compress -Depth 4
        """;

    public static async Task<SecurityHealth?> SecurityAsync(CancellationToken cancellationToken) =>
        ParseSecurity(await StdoutAsync(SecurityScript, ProbeTimeoutSeconds, cancellationToken).ConfigureAwait(false));

    internal static SecurityHealth? ParseSecurity(string? json)
    {
        if (Parse(json) is not { ValueKind: JsonValueKind.Object } root)
        {
            return null;
        }

        var defender = root.TryGetProperty("Defender", out var d) && d.ValueKind == JsonValueKind.Object
            ? new DefenderHealth(true, Bool(d, "AntivirusEnabled"), Bool(d, "RealTimeProtectionEnabled"), Int(d, "AntivirusSignatureAge"))
            : new DefenderHealth(false, null, null, null);

        var firewall = root.TryGetProperty("Firewall", out var f)
            ? AsArray(f).Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => new FirewallHealth(Text(item, "Name") ?? "?", Bool(item, "Enabled") ?? false))
                .ToList()
            : [];

        return new SecurityHealth(defender, firewall);
    }

    public const string EventsScript = """
        $since = (Get-Date).AddDays(-1)
        $critical = @(Get-WinEvent -FilterHashtable @{ LogName = 'System','Application'; Level = 1; StartTime = $since } -MaxEvents 500 -ErrorAction SilentlyContinue).Count
        $errors = @(Get-WinEvent -FilterHashtable @{ LogName = 'System','Application'; Level = 2; StartTime = $since } -MaxEvents 500 -ErrorAction SilentlyContinue).Count
        $crashes = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Application Error'; Id = 1000; StartTime = $since } -MaxEvents 200 -ErrorAction SilentlyContinue).Count
        @{ Critical = $critical; Errors = $errors; AppCrashes = $crashes } | ConvertTo-Json -Compress
        """;

    public static async Task<EventHealth?> EventsAsync(CancellationToken cancellationToken) =>
        ParseEvents(await StdoutAsync(EventsScript, ProbeTimeoutSeconds, cancellationToken).ConfigureAwait(false));

    internal static EventHealth? ParseEvents(string? json) =>
        Parse(json) is { ValueKind: JsonValueKind.Object } root
            ? new EventHealth(Int(root, "Critical") ?? 0, Int(root, "Errors") ?? 0, Int(root, "AppCrashes") ?? 0)
            : null;

    /// <summary>Поиск ожидающих обновлений — через COM Центра обновления, как у инструмента.</summary>
    public const string PendingUpdatesScript = """
        $s = New-Object -ComObject Microsoft.Update.Session
        $r = $s.CreateUpdateSearcher().Search("IsInstalled=0 and IsHidden=0 and Type='Software'")
        "$($r.Updates.Count)"
        """;

    public static async Task<UpdateHealth> UpdatesAsync(CancellationToken cancellationToken)
    {
        var reboot = RebootPending();
        var output = await StdoutAsync(PendingUpdatesScript, UpdateSearchTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        return new UpdateHealth(reboot, int.TryParse(output?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : null);
    }

    /// <summary>Ключи, по которым сама Windows помнит, что ждёт перезагрузки.</summary>
    internal static readonly string[] RebootKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"
    ];

    public static bool RebootPending()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        foreach (var path in RebootKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key is not null)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        return false;
    }

    private static async Task<string?> StdoutAsync(string script, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var result = await PowerShellHelper.RunAsync(script, timeoutSeconds, cancellationToken, maxOutput: 64_000)
            .ConfigureAwait(false);
        return result.Success ? PowerShellHelper.ExtractStdout(result.Output) : null;
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// ConvertTo-Json в PowerShell 5.1 отдаёт массив из одного элемента объектом, а не массивом, —
    /// поэтому любой ответ, который может быть списком, читается через эту функцию.
    /// </summary>
    private static IEnumerable<JsonElement> AsArray(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.EnumerateArray(),
        JsonValueKind.Object => [element],
        _ => []
    };

    private static IEnumerable<JsonElement>? Items(string? json) =>
        Parse(json) is { } root ? AsArray(root).Where(item => item.ValueKind == JsonValueKind.Object).ToList() : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
}
