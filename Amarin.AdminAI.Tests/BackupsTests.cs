using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Автоматические резервные копии (F1): расписание, чистка и сама копия.</summary>
public sealed class BackupsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-backup-" + Guid.NewGuid().ToString("N"));

    public BackupsTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void A_backup_is_due_by_the_interval_and_not_right_after_a_failure()
    {
        Assert.False(Backups.IsDue(new BackupSettings(), Now));
        Assert.True(Backups.IsDue(new BackupSettings { Enabled = true }, Now));
        Assert.False(Backups.IsDue(new BackupSettings { Enabled = true, LastAt = Now.AddHours(-23) }, Now));
        Assert.True(Backups.IsDue(new BackupSettings { Enabled = true, LastAt = Now.AddDays(-1) }, Now));
        Assert.False(Backups.IsDue(new BackupSettings { Enabled = true, Interval = BackupInterval.Weekly, LastAt = Now.AddDays(-6) }, Now));

        // Сломанные часы (копия «из будущего») не должны запирать расписание навсегда.
        Assert.True(Backups.IsDue(new BackupSettings { Enabled = true, LastAt = Now.AddDays(3) }, Now));

        Assert.False(Backups.IsDue(new BackupSettings { Enabled = true, LastErrorAt = Now.AddMinutes(-10) }, Now));
        Assert.True(Backups.IsDue(new BackupSettings { Enabled = true, LastErrorAt = Now.AddHours(-2) }, Now));
    }

    [Fact]
    public void Pruning_keeps_the_newest_and_never_touches_other_files()
    {
        var names = new[]
        {
            "amarin-backup-20260901-120000" + DataBundle.FileExtension,
            "amarin-backup-20260930-120000" + DataBundle.FileExtension,
            "amarin-backup-20260915-120000" + DataBundle.FileExtension,
            "amarin-backup-20260910-120000" + DataBundle.FileExtension + ".tmp",
            "amarin-backup-mine" + DataBundle.FileExtension,
            "holiday-photos.zip"
        };

        var pruned = Backups.ToPrune(names, keep: 2);

        Assert.Equal(["amarin-backup-20260901-120000" + DataBundle.FileExtension], pruned);
    }

    [Fact]
    public void A_backup_writes_an_archive_and_prunes_only_after_it_is_written()
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(data, "chats"));
        new AppSettingsStore(data).Save(AppSettings.CreateDefault());
        var folder = Path.Combine(_root, "copies");
        Directory.CreateDirectory(folder);
        var old = Path.Combine(folder, "amarin-backup-20200101-000000" + DataBundle.FileExtension);
        File.WriteAllText(old, "old");
        var foreign = Path.Combine(folder, "notes.txt");
        File.WriteAllText(foreign, "keep me");

        var result = Backups.Run(data, ProfileStore.DefaultProfileId, folder, keep: 1, Now, CancellationToken.None);

        Assert.True(File.Exists(result.Path));
        Assert.Equal(Backups.FileName(Now), Path.GetFileName(result.Path));
        Assert.True(result.Bytes > 0);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void An_imported_archive_does_not_bring_backups_or_spending_limits()
    {
        var mine = Path.Combine(_root, "mine");
        var theirs = Path.Combine(_root, "theirs");
        Directory.CreateDirectory(Path.Combine(mine, "chats"));
        Directory.CreateDirectory(Path.Combine(theirs, "chats"));

        var own = AppSettings.CreateDefault();
        own.SpendLimits.DayUsd = 5m;
        new AppSettingsStore(mine).Save(own);

        var other = AppSettings.CreateDefault();
        other.Backup.Enabled = true;
        other.Backup.Folder = @"Z:\\elsewhere";
        new AppSettingsStore(theirs).Save(other);

        var archive = Path.Combine(_root, "settings" + DataBundle.FileExtension);
        new DataBundleExporter(theirs).Write(archive, DataCategory.Settings);
        new DataBundleImporter(mine).Apply(archive, DataCategory.Settings, DataImportMode.Replace);

        var after = new AppSettingsStore(mine).Load();
        Assert.False(after.Backup.Enabled);
        Assert.Null(after.Backup.Folder);
        Assert.Equal(5m, after.SpendLimits.DayUsd);
    }
}
