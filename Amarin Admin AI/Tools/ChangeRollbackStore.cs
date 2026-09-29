using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;

namespace Amarin.Tools;

internal static class ChangeRollbackStore
{
    private const int MaxSnapshots = 20;

    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmarinAdminAI", "snapshots");

    public static void PruneOldSnapshots()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        var dirs = Directory.GetDirectories(Root)
            .OrderDescending()
            .Skip(MaxSnapshots)
            .ToList();

        foreach (var dir in dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // ignore prune failures
            }
        }
    }

    public static List<ServiceSnapshotEntry> LoadServices(string path) =>
        File.Exists(path) ? DeserializeList<ServiceSnapshotEntry>(File.ReadAllText(path)) : [];

    public static List<ServiceSnapshotEntry> CaptureCurrentServices()
    {
        var result = new List<ServiceSnapshotEntry>();
        foreach (var service in ServiceController.GetServices())
        {
            using (service)
            {
                try
                {
                    result.Add(new ServiceSnapshotEntry
                    {
                        Name = service.ServiceName,
                        Status = service.Status.ToString(),
                        StartType = service.StartType.ToString()
                    });
                }
                catch
                {
                    // inaccessible service
                }
            }
        }

        return result;
    }

    public static List<ScheduledTaskSnapshotEntry> CaptureCurrentScheduledTasks()
    {
        var result = PowerShellHelper.Run(
            "Get-ScheduledTask | Select-Object TaskName, TaskPath, @{n='State';e={[string]$_.State}} | ConvertTo-Json -Depth 3 -Compress");
        if (!result.Success)
        {
            return [];
        }

        return DeserializeList<ScheduledTaskSnapshotEntry>(PowerShellHelper.ExtractStdout(result.Output));
    }

    public static List<StartupProgramSnapshotEntry> CaptureStartupPrograms()
    {
        var script = """
            $items = @()
            Get-CimInstance Win32_StartupCommand -ErrorAction SilentlyContinue | ForEach-Object {
              $items += [PSCustomObject]@{
                Source = 'WMI'
                Name = $_.Name
                Command = $_.Command
                Location = $_.Location
              }
            }
            $runPaths = @(
              @{ Source='HKLM_Run'; Path='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' },
              @{ Source='HKCU_Run'; Path='HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' }
            )
            foreach ($entry in $runPaths) {
              if (Test-Path $entry.Path) {
                Get-ItemProperty $entry.Path -ErrorAction SilentlyContinue |
                  Get-Member -MemberType NoteProperty |
                  Where-Object { $_.Name -notin @('PSPath','PSParentPath','PSChildName','PSDrive','PSProvider') } |
                  ForEach-Object {
                    $name = $_.Name
                    $cmd = (Get-ItemProperty $entry.Path).$name
                    if ($cmd) {
                      $items += [PSCustomObject]@{
                        Source = $entry.Source
                        Name = $name
                        Command = [string]$cmd
                        Location = $entry.Path
                      }
                    }
                  }
              }
            }
            $items | ConvertTo-Json -Depth 4 -Compress
            """;

        var result = PowerShellHelper.Run(script, 120);
        if (!result.Success)
        {
            return [];
        }

        return DeserializeList<StartupProgramSnapshotEntry>(PowerShellHelper.ExtractStdout(result.Output));
    }

    public static string SaveJson<T>(IReadOnlyList<T> items, string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(items));
        return path;
    }

    private static List<T> DeserializeList<T>(string json)
    {
        json = ExtractJsonPayload(json);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? [];
        }
        catch (JsonException)
        {
            try
            {
                var single = JsonSerializer.Deserialize<T>(json);
                return single is null ? [] : [single];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    private static string ExtractJsonPayload(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var start = text.IndexOfAny(['[', '{']);
        return start < 0 ? string.Empty : text[start..].TrimEnd();
    }

    /// <summary>
    /// Аргументы schtasks, включающие или выключающие задачу.
    /// </summary>
    /// <remarks>
    /// <c>/enable</c> и <c>/disable</c> — ключи команды <c>/change</c>, а не команды сами по
    /// себе: прежняя строка <c>schtasks /enable /tn …</c> отвергалась schtasks как неверный
    /// синтаксис, и откат задач планировщика не срабатывал ни разу.
    /// </remarks>
    internal static IReadOnlyList<string> TaskToggleArguments(string fullName, bool disable) =>
        ["/change", "/tn", fullName, disable ? "/disable" : "/enable"];

    /// <summary>Аргументы schtasks, удаляющие задачу без вопроса.</summary>
    internal static IReadOnlyList<string> TaskDeleteArguments(string fullName) =>
        ["/delete", "/tn", fullName, "/f"];

    /// <summary>
    /// Определение задачи в XML — чтобы откат мог вернуть удалённую или перезаписанную. null,
    /// если такой задачи нет.
    /// </summary>
    internal static string? ExportTaskXml(string fullName)
    {
        var (path, name) = SplitTaskName(fullName);
        var result = PowerShellHelper.Run(
            $"Export-ScheduledTask -TaskPath '{PowerShellHelper.QuoteLiteral(path)}' " +
            $"-TaskName '{PowerShellHelper.QuoteLiteral(name)}' -ErrorAction Stop",
            60,
            maxOutput: 400_000);
        if (!result.Success)
        {
            return null;
        }

        var xml = PowerShellHelper.ExtractStdout(result.Output);
        return xml.Contains("<Task", StringComparison.Ordinal) ? xml : null;
    }

    /// <summary>Регистрирует задачу заново из сохранённой копии, заменяя одноимённую.</summary>
    internal static ToolResult RegisterTaskFromXml(string fullName, string xmlFile)
    {
        var (path, name) = SplitTaskName(fullName);
        return PowerShellHelper.Run(
            $"$xml = Get-Content -LiteralPath '{PowerShellHelper.QuoteLiteral(xmlFile)}' -Raw -Encoding UTF8\n" +
            $"Register-ScheduledTask -Xml $xml -TaskPath '{PowerShellHelper.QuoteLiteral(path)}' " +
            $"-TaskName '{PowerShellHelper.QuoteLiteral(name)}' -Force -ErrorAction Stop | Out-Null",
            120);
    }

    /// <summary>«\Папка\Имя» → («\Папка\», «Имя»): так их принимают командлеты планировщика.</summary>
    internal static (string Path, string Name) SplitTaskName(string fullName)
    {
        var normalized = RollbackRules.TaskFullName(fullName);
        var slash = normalized.LastIndexOf('\\');
        return (normalized[..(slash + 1)], normalized[(slash + 1)..]);
    }

    internal static ToolResult ImportRegistryFile(string regFile) =>
        NativeProcess.Run("reg.exe", ["import", regFile], 120);

    internal static bool TrySetServiceStartType(string serviceName, ServiceStartMode mode, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}",
                writable: true);
            if (key is null)
            {
                error = "не удалось открыть ключ службы";
                return false;
            }

            var startValue = mode switch
            {
                ServiceStartMode.Boot => 0,
                ServiceStartMode.System => 1,
                ServiceStartMode.Automatic => 2,
                ServiceStartMode.Manual => 3,
                ServiceStartMode.Disabled => 4,
                _ => 2
            };

            key.SetValue("Start", startValue, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}