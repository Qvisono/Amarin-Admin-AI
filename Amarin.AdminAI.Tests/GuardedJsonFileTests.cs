using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Повреждённый файл настроек, профилей или ключей не должен ни молча становиться заводским, ни
/// затираться первым сохранением. До 1.28.0 именно так и было: <c>catch</c> возвращал заводские
/// настройки, и любой переключатель переписывал ими файл человека.
/// </summary>
[Collection(DataFileIncidentsCollection.Name)]
public sealed class GuardedJsonFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-guarded-" + Guid.NewGuid().ToString("N"));

    public GuardedJsonFileTests()
    {
        Directory.CreateDirectory(_root);
        DataFileIncidents.Drain();
    }

    public void Dispose()
    {
        DataFileIncidents.Drain();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_successful_write_leaves_a_readable_backup_beside_the_file()
    {
        var path = Path.Combine(_root, "settings.json");

        GuardedJsonFile.Write(path, """{"a":1}""");

        Assert.Equal("""{"a":1}""", File.ReadAllText(GuardedJsonFile.BackupPath(path)));
    }

    [Fact]
    public void A_damaged_file_is_set_aside_and_the_backup_takes_its_place()
    {
        var path = Path.Combine(_root, "settings.json");
        GuardedJsonFile.Write(path, """{"a":1}""");
        File.WriteAllText(path, "{\"a\":1,,,");

        var state = GuardedJsonFile.Read(path, IsJsonObject, out var text, new DateTime(2026, 9, 30, 10, 15, 0));

        Assert.Equal(GuardedFileState.RestoredFromBackup, state);
        Assert.Equal("""{"a":1}""", text);
        Assert.Equal("""{"a":1}""", File.ReadAllText(path));
        var broken = path + ".broken-20260930-101500";
        Assert.Equal("{\"a\":1,,,", File.ReadAllText(broken));

        var incident = Assert.Single(DataFileIncidents.Drain());
        Assert.Equal("settings.json", incident.FileName);
        Assert.Equal(GuardedFileState.RestoredFromBackup, incident.State);
        Assert.Equal(broken, incident.BrokenCopy);
    }

    [Fact]
    public void Without_a_usable_backup_the_damaged_file_is_still_kept()
    {
        var path = Path.Combine(_root, "keys.json");
        File.WriteAllText(path, "not json");
        File.WriteAllText(GuardedJsonFile.BackupPath(path), "also broken");

        var state = GuardedJsonFile.Read(path, IsJsonObject, out var text);

        Assert.Equal(GuardedFileState.Reset, state);
        Assert.Null(text);
        Assert.False(File.Exists(path));
        var kept = Assert.Single(Directory.GetFiles(_root, "keys.json.broken-*"));
        Assert.Equal("not json", File.ReadAllText(kept));
        Assert.Equal(GuardedFileState.Reset, Assert.Single(DataFileIncidents.Drain()).State);
    }

    [Fact]
    public void A_missing_file_is_not_brought_back_from_its_backup()
    {
        // Удалённый вручную settings.json — это сброс по желанию человека, а не повреждение.
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(GuardedJsonFile.BackupPath(path), """{"a":1}""");

        var state = GuardedJsonFile.Read(path, IsJsonObject, out var text);

        Assert.Equal(GuardedFileState.Missing, state);
        Assert.Null(text);
        Assert.False(File.Exists(path));
        Assert.Empty(DataFileIncidents.Drain());
    }

    [Fact]
    public void Damaged_settings_come_back_from_the_backup_and_survive_the_next_save()
    {
        var store = new AppSettingsStore(_root);
        var settings = store.Load();
        settings.NotifySound = !settings.NotifySound;
        var chosen = settings.NotifySound;
        store.Save(settings);
        File.WriteAllText(store.FilePath, "{ \"notifySound\": ");

        var fresh = new AppSettingsStore(_root);
        var loaded = fresh.Load();
        fresh.Save(loaded);

        Assert.Equal(chosen, loaded.NotifySound);
        var broken = Assert.Single(Directory.GetFiles(_root, "settings.json.broken-*"));
        Assert.Equal("{ \"notifySound\": ", File.ReadAllText(broken));
        Assert.Single(DataFileIncidents.Drain());
    }

    [Fact]
    public void Damaged_profiles_come_back_from_the_backup()
    {
        var store = new ProfileStore(_root);
        var registry = store.Load();
        registry.Profiles.Add(new UserProfile { Id = "work", Name = "Работа" });
        store.Save(registry);
        File.WriteAllText(Path.Combine(_root, "profiles.json"), "[[[");

        var loaded = new ProfileStore(_root).Load();

        Assert.Contains(loaded.Profiles, profile => profile.Id == "work");
        Assert.Single(DataFileIncidents.Drain());
    }

    [Fact]
    public void Damaged_keys_come_back_from_the_backup_without_being_decrypted()
    {
        var store = new ApiKeyStore(_root);
        store.Load();
        store.Save();
        var backup = File.ReadAllText(Path.Combine(_root, "keys.json.bak"));
        File.WriteAllText(Path.Combine(_root, "keys.json"), "{\"keys\": [ {");

        new ApiKeyStore(_root).Load();

        Assert.Equal(backup, File.ReadAllText(Path.Combine(_root, "keys.json")));
        Assert.Single(DataFileIncidents.Drain());
    }

    [Theory]
    [InlineData("settings.json.bak")]
    [InlineData("keys.json.bak")]
    [InlineData("profiles.json.bak")]
    [InlineData("settings.json.broken-20260930-101500")]
    [InlineData("profiles/work/keys.json.broken-20260930-101500")]
    public void Backups_and_damaged_copies_never_travel_in_the_data_archive(string relative) =>
        Assert.Equal(DataCategory.None, DataBundle.CategoryOf(relative));

    [Fact]
    public void Wiping_the_default_profile_takes_the_backups_with_it()
    {
        foreach (var name in new[] { "settings.json.bak", "keys.json.bak", "settings.json.broken-20260930-101500" })
        {
            File.WriteAllText(Path.Combine(_root, name), "{}");
        }

        var result = ProfileDataWiper.Wipe(_root, isDefaultProfile: true, includeAudit: false);

        Assert.True(result.Ok);
        Assert.Empty(Directory.GetFiles(_root, "*.bak"));
        Assert.Empty(Directory.GetFiles(_root, "*.broken-*"));
    }

    [Fact]
    public void The_message_names_the_file_and_where_the_damaged_copy_went()
    {
        var text = DataFileIncidentText.Describe(
        [
            new DataFileIncident("settings.json", GuardedFileState.RestoredFromBackup, @"C:\data\settings.json.broken-1"),
            new DataFileIncident("keys.json", GuardedFileState.Reset, "")
        ]);

        Assert.Contains("settings.json", text, StringComparison.Ordinal);
        Assert.Contains(@"C:\data\settings.json.broken-1", text, StringComparison.Ordinal);
        Assert.Contains("keys.json", text, StringComparison.Ordinal);
    }

    private static bool IsJsonObject(string text)
    {
        using var document = System.Text.Json.JsonDocument.Parse(text);
        return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
    }
}

/// <summary>Тесты, которые наполняют общий на процесс <see cref="DataFileIncidents"/>.</summary>
[CollectionDefinition(Name)]
public sealed class DataFileIncidentsCollection
{
    public const string Name = "DataFileIncidents";
}
