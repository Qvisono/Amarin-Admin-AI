using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

/// <summary>A restore point the model took before changing something on this machine.</summary>
public sealed record SnapshotEntry(string Id, DateTime Created, string Label, string Machine, string Path);

/// <summary>
/// Reads the restore points as objects.
/// </summary>
/// <remarks>
/// <see cref="ChangeRollbackOperations.ListSnapshots"/> already lists the same directories, but it
/// renders them into one string for the model to read. Parsing that text back would make the
/// journal break every time the wording changed, so the directory is read twice on purpose:
/// once for the model, once for the screen.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class RollbackSnapshots
{
    /// <summary>Newest first. Empty when nothing was ever snapshotted.</summary>
    public static IReadOnlyList<SnapshotEntry> List()
    {
        if (!Directory.Exists(ChangeRollbackStore.Root))
        {
            return [];
        }

        var entries = new List<SnapshotEntry>();
        foreach (var dir in Directory.GetDirectories(ChangeRollbackStore.Root))
        {
            if (Read(dir) is { } entry)
            {
                entries.Add(entry);
            }
        }

        entries.Sort((left, right) => right.Created.CompareTo(left.Created));
        return entries;
    }

    private static SnapshotEntry? Read(string dir)
    {
        var id = System.IO.Path.GetFileName(dir);
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        // The folder name is the timestamp, so a snapshot whose meta.json never got written -- a
        // crash mid-snapshot -- is still listed rather than silently dropped.
        var created = ParseId(id);
        var label = "";
        var machine = "";

        var metaPath = System.IO.Path.Combine(dir, "meta.json");
        if (File.Exists(metaPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (doc.RootElement.TryGetProperty("label", out var l))
                {
                    label = l.GetString() ?? "";
                }

                if (doc.RootElement.TryGetProperty("machine", out var m))
                {
                    machine = m.GetString() ?? "";
                }

                if (doc.RootElement.TryGetProperty("created", out var c) &&
                    c.TryGetDateTime(out var stamp))
                {
                    created = stamp;
                }
            }
            catch (JsonException)
            {
                // A truncated meta.json still leaves a usable restore point on disk.
            }
            catch (IOException)
            {
            }
        }

        return new SnapshotEntry(id, created, label, machine, dir);
    }

    private static DateTime ParseId(string id) =>
        DateTime.TryParseExact(id, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTime.MinValue;
}
