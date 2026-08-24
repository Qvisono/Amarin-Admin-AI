using System.Text;
using Microsoft.Win32;

namespace Amarin.Tools;

internal static class InstalledProgramsCatalog
{
    private static readonly object CacheGate = new();
    private static List<Entry>? _cache;
    private static long _cacheTicks;
    private const int CacheTtlMs = 60_000;

    internal readonly record struct Entry(
        string Name,
        string Version,
        string Publisher,
        string Date,
        string Hive);

    public static void Invalidate()
    {
        lock (CacheGate)
        {
            _cache = null;
        }
    }

    public static List<Entry> Query(string? filter, int max)
    {
        List<Entry> all;
        lock (CacheGate)
        {
            if (_cache is not null && Environment.TickCount64 - _cacheTicks < CacheTtlMs)
            {
                all = _cache;
            }
            else
            {
                all = LoadAll();
                _cache = all;
                _cacheTicks = Environment.TickCount64;
            }
        }

        IEnumerable<Entry> query = all;
        if (!string.IsNullOrEmpty(filter))
        {
            query = query.Where(r =>
                r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Publisher.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Version.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        return query.Take(max).ToList();
    }

    private static List<Entry> LoadAll()
    {
        var rows = new List<Entry>(256);

        Collect(Registry.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "HKLM", rows);
        Collect(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "HKLM-WOW64", rows);
        Collect(Registry.CurrentUser,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "HKCU", rows);
        Collect(Registry.CurrentUser,
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "HKCU-WOW64", rows);

        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .GroupBy(r => r.Name + "|" + r.Version, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string FormatTable(IReadOnlyList<Entry> list, int maxShown)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Name | Version | Publisher | InstallDate | Hive");
        sb.AppendLine(new string('-', 80));
        foreach (var row in list)
        {
            sb.AppendLine($"{row.Name} | {row.Version} | {row.Publisher} | {row.Date} | {row.Hive}");
        }

        sb.AppendLine();
        sb.AppendLine($"Всего (показано до {maxShown}): {list.Count}. Источник: реестр Uninstall (не Win32_Product).");
        return sb.ToString().TrimEnd();
    }

    private static void Collect(
        RegistryKey hive,
        string subKey,
        string hiveLabel,
        List<Entry> rows)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey, writable: false);
            if (key is null)
            {
                return;
            }

            foreach (var name in key.GetSubKeyNames())
            {
                try
                {
                    using var item = key.OpenSubKey(name, writable: false);
                    if (item is null)
                    {
                        continue;
                    }

                    var displayName = item.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    var systemComponent = item.GetValue("SystemComponent");
                    if (systemComponent is int sc && sc == 1)
                    {
                        continue;
                    }

                    var version = item.GetValue("DisplayVersion") as string ?? "";
                    var publisher = item.GetValue("Publisher") as string ?? "";
                    var date = item.GetValue("InstallDate") as string ?? "";
                    rows.Add(new Entry(displayName.Trim(), version.Trim(), publisher.Trim(), date.Trim(), hiveLabel));
                }
                catch
                {
                    // ignore individual key errors
                }
            }
        }
        catch
        {
            // hive may be inaccessible
        }
    }
}
