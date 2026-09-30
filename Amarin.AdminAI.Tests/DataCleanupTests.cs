using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Хранение чатов и очистка места (F3).</summary>
public sealed class DataCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-care-" + Guid.NewGuid().ToString("N"));

    public DataCleanupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0);

    private static ChatIndexEntry Entry(string id, int daysAgo, bool pinned = false) =>
        new() { Id = id, Title = id, UpdatedAt = Now.AddDays(-daysAgo), IsPinned = pinned };

    [Fact]
    public void Retention_spares_recent_pinned_busy_and_already_archived_chats()
    {
        var items = new[] { Entry("old", 200), Entry("recent", 10), Entry("pinned", 400, pinned: true), Entry("open", 300), Entry("archived", 300) };
        var organize = new ChatOrganizer.State { Chats = { ["archived"] = new ChatPlacement { Archived = true } } };
        var busy = new HashSet<string> { "open" };

        Assert.Equal(["old"], DataCleanup.PickForRetention(items, organize, new ChatRetention { Mode = RetentionMode.Archive, Days = 180 }, Now, busy));
        Assert.Equal(["old", "archived"], DataCleanup.PickForRetention(items, organize, new ChatRetention { Mode = RetentionMode.Delete, Days = 180 }, Now, busy));
        Assert.Empty(DataCleanup.PickForRetention(items, organize, new ChatRetention(), Now, busy));

        // Опечатка «1 день» не превращается в «удалить почти всё»: меньше недели не бывает.
        var typo = DataCleanup.PickForRetention(
            [.. items, Entry("fresh", 3)], organize, new ChatRetention { Mode = RetentionMode.Delete, Days = 1 }, Now, busy);
        Assert.DoesNotContain("fresh", typo);
        Assert.Contains("recent", typo);
    }

    private string Write(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void Each_cleanup_removes_only_its_own_files()
    {
        var app = Path.Combine(_root, "app");
        var local = Path.Combine(_root, "local");
        var chat = Write("app/chats/c1.json", 100);
        var settings = Write("app/settings.json", 10);
        var export = Write("app/shared/c1.html", 50);
        var cache = Write("app/background.abc.cache.png", 70);
        var background = Write("app/background.png", 80);
        var log = Write("local/crash.log", 30);
        for (var i = 0; i < 7; i++)
        {
            Write($"local/snapshots/2026093{i}-000000/state.json", 5);
        }

        Assert.Equal(new CleanupSize(30, 1), DataCleanup.Measure(CleanupTarget.Logs, app, local, 5));
        Assert.Equal(new CleanupSize(10, 2), DataCleanup.Measure(CleanupTarget.Snapshots, app, local, 5));

        DataCleanup.Run(CleanupTarget.Logs, app, local, 5);
        DataCleanup.Run(CleanupTarget.Shared, app, local, 5);
        DataCleanup.Run(CleanupTarget.BackgroundCache, app, local, 5);
        DataCleanup.Run(CleanupTarget.Snapshots, app, local, 5);

        Assert.False(File.Exists(log));
        Assert.False(File.Exists(export));
        Assert.False(File.Exists(cache));
        Assert.True(File.Exists(chat));
        Assert.True(File.Exists(settings));
        Assert.True(File.Exists(background));

        // Остаются пять последних снимков — по имени, где впереди отметка времени.
        var left = Directory.GetDirectories(Path.Combine(local, "snapshots")).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["20260932-000000", "20260933-000000", "20260934-000000", "20260935-000000", "20260936-000000"], left);
    }

    [Fact]
    public void The_largest_chats_count_their_attachment_folders()
    {
        var chats = Path.Combine(_root, "chats");
        Write("chats/small.json", 10);
        Write("chats/big.json", 20);
        Write("chats/big/attachments/a.png", 500);
        Write("chats/mid.json", 300);

        var largest = DataCleanup.LargestChats(chats, ["small", "big", "mid", "gone"], 2);

        Assert.Equal([new ChatSize("big", 520), new ChatSize("mid", 300)], largest);
    }
}
