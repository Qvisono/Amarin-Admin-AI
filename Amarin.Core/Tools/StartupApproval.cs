using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>Где лежит элемент автозагрузки.</summary>
internal enum StartupLocation
{
    UserRun,
    MachineRun,
    MachineRun32,
    UserFolder,
    CommonFolder
}

/// <summary>Элемент автозагрузки и его состояние в «Диспетчере задач».</summary>
internal sealed record StartupItem(StartupLocation Location, string Name, string Command, bool Enabled);

/// <summary>
/// Включение и выключение автозагрузки так же, как это делает «Диспетчер задач»: отметкой в
/// <c>Explorer\StartupApproved</c>, а не удалением записи.
/// </summary>
/// <remarks>
/// <para>
/// Запись в <c>Run</c> или ярлык в папке остаются на месте — выключенный элемент виден в
/// «Диспетчере задач» и включается обратно там же. Удаление потеряло бы саму команду запуска.
/// </para>
/// <para>
/// Значение — двоичное, 12 байт: первый байт чётный — включено (02, 06), нечётный — выключено
/// (03), байты 4–11 — FILETIME выключения. Отсутствующее значение — включено.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class StartupApproval
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    /// <summary>Разделы, которые правит это действие, — их снимает снимок отката.</summary>
    internal static readonly string[] ApprovedKeys =
    [
        @"HKCU\" + ApprovedRoot + "Run",
        @"HKCU\" + ApprovedRoot + "StartupFolder",
        @"HKLM\" + ApprovedRoot + "Run",
        @"HKLM\" + ApprovedRoot + "Run32",
        @"HKLM\" + ApprovedRoot + "StartupFolder"
    ];

    public static bool TryLocation(string? value, out StartupLocation location)
    {
        location = default;
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "hkcu_run" => Set(StartupLocation.UserRun, out location),
            "hklm_run" => Set(StartupLocation.MachineRun, out location),
            "hklm_run32" => Set(StartupLocation.MachineRun32, out location),
            "user_folder" => Set(StartupLocation.UserFolder, out location),
            "common_folder" => Set(StartupLocation.CommonFolder, out location),
            _ => false
        };

        static bool Set(StartupLocation value, out StartupLocation location)
        {
            location = value;
            return true;
        }
    }

    public static string Code(StartupLocation location) => location switch
    {
        StartupLocation.UserRun => "hkcu_run",
        StartupLocation.MachineRun => "hklm_run",
        StartupLocation.MachineRun32 => "hklm_run32",
        StartupLocation.UserFolder => "user_folder",
        _ => "common_folder"
    };

    /// <summary>Раздел StartupApproved для места: (корень, путь).</summary>
    public static (RegistryHive Hive, string SubKey) ApprovedKey(StartupLocation location) => location switch
    {
        StartupLocation.UserRun => (RegistryHive.CurrentUser, ApprovedRoot + "Run"),
        StartupLocation.MachineRun => (RegistryHive.LocalMachine, ApprovedRoot + "Run"),
        StartupLocation.MachineRun32 => (RegistryHive.LocalMachine, ApprovedRoot + "Run32"),
        StartupLocation.UserFolder => (RegistryHive.CurrentUser, ApprovedRoot + "StartupFolder"),
        _ => (RegistryHive.LocalMachine, ApprovedRoot + "StartupFolder")
    };

    /// <summary>Значение отметки: включено — нули после 02, выключено — 03 и время выключения.</summary>
    public static byte[] Mark(bool enabled, DateTime utcNow)
    {
        var value = new byte[12];
        value[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled)
        {
            BitConverter.GetBytes(utcNow.ToFileTimeUtc()).CopyTo(value, 4);
        }

        return value;
    }

    public static bool IsEnabled(byte[]? mark) => mark is not { Length: > 0 } || (mark[0] & 1) == 0;

    /// <summary>Все элементы автозагрузки из Run-разделов и папок, с состоянием.</summary>
    public static List<StartupItem> List()
    {
        var items = new List<StartupItem>();
        AddRun(items, StartupLocation.UserRun, RegistryHive.CurrentUser, RunKey);
        AddRun(items, StartupLocation.MachineRun, RegistryHive.LocalMachine, RunKey);
        AddRun(items, StartupLocation.MachineRun32, RegistryHive.LocalMachine, Run32Key);
        AddFolder(items, StartupLocation.UserFolder, Environment.GetFolderPath(Environment.SpecialFolder.Startup));
        AddFolder(items, StartupLocation.CommonFolder, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
        return items;
    }

    public static string Format(IReadOnlyList<StartupItem> items)
    {
        if (items.Count == 0)
        {
            return Loc.Get("S.Tool.Startup.Empty");
        }

        var text = new StringBuilder("location | name | enabled | command\n");
        foreach (var item in items)
        {
            text.Append(Code(item.Location)).Append(" | ").Append(item.Name).Append(" | ")
                .Append(item.Enabled ? "yes" : "no").Append(" | ").Append(item.Command).Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Включает или выключает элемент. Элемента, которого нет, не трогает.</summary>
    public static ToolResult SetEnabled(StartupLocation location, string name, bool enabled, DateTime utcNow)
    {
        var item = List().FirstOrDefault(entry =>
            entry.Location == location && entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Startup.NotFound", name, Code(location)));
        }

        var (hive, subKey) = ApprovedKey(location);
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.CreateSubKey(subKey, writable: true);
            key.SetValue(item.Name, Mark(enabled, utcNow), RegistryValueKind.Binary);
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Fail(Loc.Get("S.Tool.NeedsAdmin"));
        }
        catch (System.Security.SecurityException)
        {
            return ToolResult.Fail(Loc.Get("S.Tool.NeedsAdmin"));
        }

        return ToolResult.Ok(Loc.Format(enabled ? "S.Tool.Startup.Enabled" : "S.Tool.Startup.Disabled",
            item.Name, Code(location)));
    }

    private static void AddRun(List<StartupItem> items, StartupLocation location, RegistryHive hive, string subKey)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var run = root.OpenSubKey(subKey);
            if (run is null)
            {
                return;
            }

            var marks = ReadMarks(location);
            foreach (var name in run.GetValueNames().Where(name => name.Length > 0))
            {
                items.Add(new StartupItem(location, name, run.GetValue(name)?.ToString() ?? "",
                    IsEnabled(marks.GetValueOrDefault(name))));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Нет доступа к разделу — его элементы просто не показываются.
        }
    }

    private static void AddFolder(List<StartupItem> items, StartupLocation location, string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        var marks = ReadMarks(location);
        foreach (var file in Directory.EnumerateFiles(folder).Where(file => !file.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileName(file);
            items.Add(new StartupItem(location, name, file, IsEnabled(marks.GetValueOrDefault(name))));
        }
    }

    private static Dictionary<string, byte[]> ReadMarks(StartupLocation location)
    {
        var marks = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var (hive, subKey) = ApprovedKey(location);
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(subKey);
            foreach (var name in key?.GetValueNames() ?? [])
            {
                if (key!.GetValue(name) is byte[] value)
                {
                    marks[name] = value;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }

        return marks;
    }
}
