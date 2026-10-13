using System.IO.Compression;
using System.Text.Json;
using Amarin.Core;
using Amarin.Fox;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Данные Розы в архиве, учёте места и «Удалить все данные» (1.35.0): память и дневник едут вместе
/// с чатами, дневники сливаются по дням, чужая память не затирает свою.
/// </summary>
public sealed class FoxDataTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "amarin-fox-data-" + Guid.NewGuid().ToString("N"));

    public FoxDataTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Root(string name)
    {
        var root = Path.Combine(_work, name);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Seed(string root, double affection, bool encrypt, params (string Date, string Text)[] days)
    {
        using var store = new FoxStore(root, () => encrypt);
        store.Update(state => state.Affection = affection);
        foreach (var (date, text) in days)
        {
            store.Write(new FoxDiaryEntry { Date = date, Text = text });
        }

        var chats = new ChatStore(root);
        chats.Save(new ChatSession { Id = "c" + Guid.NewGuid().ToString("N")[..6], Title = "chat" });
        chats.Flush();
    }

    private string Export(string root)
    {
        var archive = Path.Combine(_work, Guid.NewGuid().ToString("N") + DataBundle.FileExtension);
        new DataBundleExporter(root, null).Write(archive, DataCategory.Chats);
        return archive;
    }

    private static FoxStore Open(string root) => new(root, () => false);

    [Theory]
    [InlineData("fox/state.json")]
    [InlineData("fox/diary.json")]
    [InlineData("profiles/work0001/fox/diary.json")]
    public void Rosa_travels_with_the_chats(string relative) =>
        Assert.Equal(DataCategory.Chats, DataBundle.CategoryOf(relative));

    [Fact]
    public void Rosa_counts_toward_the_chats_on_the_disk_report() =>
        Assert.Equal(DataUsage.ChatsKey, DataUsage.ClassifyAppFile("fox/state.json"));

    [Fact]
    public void Delete_all_data_takes_rosa_too() =>
        Assert.Contains(FoxData.FolderName, ProfileDataWiper.DefaultProfileFolders);

    /// <summary>
    /// В архив память и дневник едут открытым текстом даже у шифрующего профиля: DPAPI на другой
    /// машине не расшифровать. А в число чатов на окне экспорта они не входят.
    /// </summary>
    [Fact]
    public void Export_opens_rosa_and_does_not_count_her_as_chats()
    {
        var source = Root("source");
        Seed(source, 55, encrypt: true, ("2026-10-12", "SecretDiaryLine"));

        var archive = Export(source);
        using (var zip = ZipFile.OpenRead(archive))
        {
            using var reader = new StreamReader(zip.GetEntry(DataBundle.DataPrefix + "fox/diary.json")!.Open());
            Assert.Contains("SecretDiaryLine", reader.ReadToEnd(), StringComparison.Ordinal);
            Assert.NotNull(zip.GetEntry(DataBundle.DataPrefix + "fox/state.json"));
        }

        var plan = new DataBundleExporter(source, null).Plan(DataCategory.Chats);
        Assert.Equal(1, plan.ByCategory.Single(info => info.Category == DataCategory.Chats).Items);
    }

    /// <summary>На новой машине Роза приезжает целиком: тот же характер, тот же дневник.</summary>
    [Fact]
    public void Import_into_a_profile_without_rosa_brings_her_whole()
    {
        var source = Root("source");
        Seed(source, 64, encrypt: false, ("2026-10-11", "first"), ("2026-10-12", "second"));
        var target = Root("target");

        var result = new DataBundleImporter(target).Apply(Export(source), DataCategory.Chats, DataImportMode.Merge);

        Assert.Equal(DataBundleError.None, result.Error);
        using var store = Open(target);
        Assert.Equal(64, store.Snapshot().Affection);
        Assert.Equal(["2026-10-11", "2026-10-12"], store.Diary().Select(entry => entry.Date));
    }

    /// <summary>
    /// В слиянии своя Роза остаётся своей — отношения у каждого профиля свои, — а дневник берёт
    /// недостающие дни. Повторный импорт ничего не удваивает.
    /// </summary>
    [Fact]
    public void Merge_keeps_my_rosa_and_adds_missing_diary_days()
    {
        var source = Root("source");
        Seed(source, -40, encrypt: false, ("2026-10-11", "theirs-11"), ("2026-10-12", "theirs-12"));
        var target = Root("target");
        Seed(target, 80, encrypt: false, ("2026-10-12", "mine-12"), ("2026-10-13", "mine-13"));
        var archive = Export(source);

        new DataBundleImporter(target).Apply(archive, DataCategory.Chats, DataImportMode.Merge);
        new DataBundleImporter(target).Apply(archive, DataCategory.Chats, DataImportMode.Merge);

        using var store = Open(target);
        Assert.Equal(80, store.Snapshot().Affection);
        Assert.Equal(
            [("2026-10-11", "theirs-11"), ("2026-10-12", "mine-12"), ("2026-10-13", "mine-13")],
            store.Diary().Select(entry => (entry.Date, entry.Text)));
    }

    /// <summary>Замена — восстановление копии: память из архива, у совпавших дней — записи архива.</summary>
    [Fact]
    public void Replace_takes_the_archived_rosa_and_keeps_days_only_I_have()
    {
        var source = Root("source");
        Seed(source, -40, encrypt: false, ("2026-10-12", "theirs-12"));
        var target = Root("target");
        Seed(target, 80, encrypt: false, ("2026-10-12", "mine-12"), ("2026-10-13", "mine-13"));

        new DataBundleImporter(target).Apply(Export(source), DataCategory.Chats, DataImportMode.Replace);

        using var store = Open(target);
        Assert.Equal(-40, store.Snapshot().Affection);
        Assert.Equal(
            [("2026-10-12", "theirs-12"), ("2026-10-13", "mine-13")],
            store.Diary().Select(entry => (entry.Date, entry.Text)));
    }

    /// <summary>Шифрующий профиль получает Розу из архива уже зашифрованной.</summary>
    [Fact]
    public void Import_writes_rosa_by_the_target_profile_encryption_rule()
    {
        var source = Root("source");
        Seed(source, 30, encrypt: false, ("2026-10-12", "PlainInArchive"));
        var target = Root("target");
        var settings = AppSettings.CreateDefault();
        settings.EncryptChats = true;
        new AppSettingsStore(target).Save(settings);

        new DataBundleImporter(target).Apply(Export(source), DataCategory.Chats, DataImportMode.Merge);

        Assert.True(AtRestCipher.IsEncrypted(File.ReadAllBytes(FoxData.DiaryPath(target))));
        Assert.True(AtRestCipher.IsEncrypted(File.ReadAllBytes(FoxData.StatePath(target))));
        using var store = new FoxStore(target, () => true);
        Assert.Equal("PlainInArchive", Assert.Single(store.Diary()).Text);
    }

    [Fact]
    public void Diary_merge_survives_a_broken_side_and_drops_undated_entries()
    {
        var mine = """[ { "date": "2026-10-12", "text": "mine" }, { "text": "no date" }, { "date": "12.10.2026", "text": "bad date" } ]""";

        var merged = JsonSerializer.Deserialize<List<FoxDiaryEntry>>(FoxData.MergeDiary(mine, "{ broken", incomingWins: true), AppJson.Options)!;

        Assert.Equal("mine", Assert.Single(merged).Text);
        Assert.Equal("[]", FoxData.MergeDiary(null, null, incomingWins: false).Replace(" ", "", StringComparison.Ordinal));
    }
}
