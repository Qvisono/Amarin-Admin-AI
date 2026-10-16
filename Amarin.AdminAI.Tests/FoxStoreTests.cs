using System.Text;
using Amarin.Core;
using Amarin.Fox;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Память Розы на диске (1.35.0): <c>fox/state.json</c> и <c>fox/diary.json</c> папки профиля.
/// </summary>
public sealed class FoxStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-fox-store-" + Guid.NewGuid().ToString("N"));

    public FoxStoreTests() => Directory.CreateDirectory(_root);

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

    private static FoxDiaryEntry Entry(string date, string text) =>
        new() { Date = date, Text = text, Annoyed = 2, Pleased = 7, Mood = "happy", WrittenAt = new DateTime(2026, 10, 13) };

    [Fact]
    public void State_and_diary_survive_a_restart()
    {
        using (var store = new FoxStore(_root, () => false))
        {
            store.Update(state =>
            {
                state.Affection = 42;
                state.PetsTotal = 7;
                state.Milestones.Add("first-meet");
                state.Today.Date = "2026-10-13";
                state.Today.Topics["science"] = 3;

                // D4: воля, отказы прийти, кусь — то же state.json, и архив увозит его целиком.
                state.NextActivityAt = new DateTime(2026, 10, 13, 12, 5, 0);
                state.Energy = 33;
                state.RefusedCalls = 1;
                state.Today.Bites = 2;
                state.Today.CallRefusals = 1;

                // Вторая волна: память для общения — тот же state.json.
                FoxMemory.AddExchange(state, "как дела?", "Отлично!", new DateTime(2026, 10, 13, 11, 0, 0));
                FoxMemory.Remember(state, "Любит дождь", new DateTime(2026, 10, 13, 11, 1, 0));
            });
            store.Write(Entry("2026-10-12", "Today he talked about stars."));
            store.Write(Entry("2026-10-11", "Quiet day."));
        }

        using var again = new FoxStore(_root, () => false);
        var snapshot = again.Snapshot();
        Assert.Equal(42, snapshot.Affection);
        Assert.Equal(7, snapshot.PetsTotal);
        Assert.Equal(["first-meet"], snapshot.Milestones);
        Assert.Equal(3, snapshot.Today.Topics["science"]);
        Assert.Equal(new DateTime(2026, 10, 13, 12, 5, 0), snapshot.NextActivityAt);
        Assert.Equal(33, snapshot.Energy);
        Assert.Equal(1, snapshot.RefusedCalls);
        Assert.Equal((2, 1), (snapshot.Today.Bites, snapshot.Today.CallRefusals));
        Assert.Equal(("как дела?", "Отлично!"), (snapshot.Conversation[0].Human, snapshot.Conversation[0].Rosa));
        Assert.Equal("Любит дождь", Assert.Single(snapshot.Notes).Text);
        Assert.Equal(["2026-10-11", "2026-10-12"], again.Diary().Select(entry => entry.Date));
    }

    /// <summary>Запись за тот же день заменяет прежнюю: в дневнике один день — одна запись.</summary>
    [Fact]
    public void A_second_entry_for_the_same_day_replaces_the_first()
    {
        using var store = new FoxStore(_root, () => false);
        store.Write(Entry("2026-10-12", "draft"));
        store.Write(Entry("2026-10-12", "final"));

        Assert.Equal("final", Assert.Single(store.Diary()).Text);
    }

    /// <summary>
    /// При шифровании переписки дневник шифруется тем же DPAPI: он её пересказывает. Секрет —
    /// латиницей: кириллицу JSON записал бы как <c>\u04…</c>, и проверка прошла бы и без шифра.
    /// </summary>
    [Fact]
    public void Encrypted_profiles_keep_rosa_encrypted_on_disk()
    {
        using (var store = new FoxStore(_root, () => true))
        {
            store.Write(Entry("2026-10-12", "SecretNoteAboutTheHuman"));
            store.Update(state => state.Today.Events.Add("SecretEventMarker"));
        }

        foreach (var path in new[] { FoxData.DiaryPath(_root), FoxData.StatePath(_root) })
        {
            var bytes = File.ReadAllBytes(path);
            Assert.True(AtRestCipher.IsEncrypted(bytes));
            Assert.DoesNotContain("Secret", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        }

        using var again = new FoxStore(_root, () => true);
        Assert.Equal("SecretNoteAboutTheHuman", Assert.Single(again.Diary()).Text);
        Assert.Contains("SecretEventMarker", again.Snapshot().Today.Events);
    }

    /// <summary>Повреждённая память не роняет Розу: знакомство начинается заново, файл — рядом.</summary>
    [Fact]
    public void A_broken_state_file_starts_over_and_is_kept_aside()
    {
        Directory.CreateDirectory(Path.Combine(_root, FoxData.FolderName));
        File.WriteAllText(FoxData.StatePath(_root), "{ not json");

        using var store = new FoxStore(_root, () => false);
        var snapshot = store.Snapshot();

        Assert.Equal(FoxState.StartAffection, snapshot.Affection);
        Assert.False(File.Exists(FoxData.StatePath(_root)));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, FoxData.FolderName), FoxData.StateFileName + ".broken-*"));
    }

    /// <summary>
    /// Файл, зашифрованный не для этой учётной записи Windows, тоже откладывается рядом: прежде он
    /// молча затирался первой же записью новой памяти.
    /// </summary>
    [Fact]
    public void An_unreadable_state_file_is_kept_aside_and_not_overwritten()
    {
        Directory.CreateDirectory(Path.Combine(_root, FoxData.FolderName));
        byte[] foreign = [.. AtRestCipher.FileMagic, 1, 2, 3, 4, 5, 6, 7, 8];
        File.WriteAllBytes(FoxData.StatePath(_root), foreign);

        using (var store = new FoxStore(_root, () => false))
        {
            Assert.Equal(FoxState.StartAffection, store.Snapshot().Affection);
            store.Update(state => state.Affection = 33);
        }

        var aside = Assert.Single(Directory.GetFiles(Path.Combine(_root, FoxData.FolderName), FoxData.StateFileName + ".broken-*"));
        Assert.Equal(foreign, File.ReadAllBytes(aside));
        Assert.Equal(33, new FoxStore(_root, () => false).Snapshot().Affection);
    }

    /// <summary>
    /// Сбросы идут по одной: снимок, снятый раньше, не ложится на диск поверх более нового, даже
    /// если его запись застряла, пока второй сброс уже снял свой.
    /// </summary>
    [Fact]
    public void A_slow_flush_never_lands_after_a_newer_one()
    {
        var written = new List<string>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool Write(string path, string text, bool encrypt)
        {
            if (!entered.IsSet)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }

            lock (written)
            {
                written.Add(text);
            }

            return true;
        }

        using var store = new FoxStore(_root, () => false, TimeSpan.FromHours(1), Write);
        store.Update(state => state.Boredom = 1);
        var older = new Thread(store.Flush);
        older.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        store.Update(state => state.Boredom = 2);
        var newer = new Thread(store.Flush);
        newer.Start();

        // Без очереди сбросов второй успел бы записать своё, пока первый стоит.
        _ = newer.Join(TimeSpan.FromMilliseconds(300));
        release.Set();
        older.Join();
        newer.Join();

        var last = System.Text.Json.JsonSerializer.Deserialize<FoxState>(written[^1], AppJson.Options)!;
        Assert.Equal(2, last.Boredom);
    }

    /// <summary>Смена профиля уносит отложенное в прежний и начинает читать новый.</summary>
    [Fact]
    public void Switching_profiles_writes_the_old_one_and_reads_the_new_one()
    {
        var other = Path.Combine(_root, "profiles", "work");
        using var store = new FoxStore(_root, () => false, TimeSpan.FromHours(1));
        store.Update(state => state.Affection = 77);

        store.UseRoot(other, () => false);
        Assert.Equal(FoxState.StartAffection, store.Snapshot().Affection);
        store.Update(state => state.Affection = -50);
        store.Flush();

        Assert.Equal(77, new FoxStore(_root, () => false).Snapshot().Affection);
        Assert.Equal(-50, new FoxStore(other, () => false).Snapshot().Affection);
    }

    /// <summary>
    /// Пока идёт импорт, живая Роза не пишет: иначе её копия из памяти затёрла бы приехавшее.
    /// После импорта память перечитывается с диска.
    /// </summary>
    [Fact]
    public void A_running_import_is_not_overwritten_and_is_picked_up_afterwards()
    {
        using var store = new FoxStore(_root, () => false, TimeSpan.FromHours(1));
        store.Update(state => state.Affection = 10);

        store.BeginImport();
        store.Update(state => state.Affection = 11);
        FoxData.Write(FoxData.StatePath(_root), """{ "affection": 90, "petsTotal": 5 }""", encrypt: false);
        store.Flush();
        store.EndImport();

        var snapshot = store.Snapshot();
        Assert.Equal(90, snapshot.Affection);
        Assert.Equal(5, snapshot.PetsTotal);
    }

    /// <summary>Перемены пишутся сами через паузу — без явного сброса.</summary>
    [Fact]
    public void Changes_are_written_after_a_pause()
    {
        using var store = new FoxStore(_root, () => false, TimeSpan.FromMilliseconds(30));
        store.Update(state => state.PetsTotal = 3);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(FoxData.StatePath(_root)) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        Assert.Equal(3, new FoxStore(_root, () => false).Snapshot().PetsTotal);
    }

    /// <summary>«Начать дружбу заново» обнуляет знакомство, но дневник оставляет — это память человека.</summary>
    [Fact]
    public void Starting_over_resets_the_friendship_but_keeps_the_diary()
    {
        using var store = new FoxStore(_root, () => false);
        store.Update(state =>
        {
            state.Affection = -80;
            state.Cold = true;
            state.Milestones.Add("pets-100");
            FoxMemory.Remember(state, "Любит дождь", DateTime.Now);
        });
        store.Write(Entry("2026-10-12", "He threw me again."));

        var now = new DateTime(2026, 10, 13, 9, 0, 0);
        store.Reset(now);

        var snapshot = store.Snapshot();
        Assert.Equal(FoxState.StartAffection, snapshot.Affection);
        Assert.False(snapshot.Cold);
        Assert.Empty(snapshot.Milestones);
        Assert.Empty(snapshot.Notes);
        Assert.Equal(now, snapshot.MetAt);
        Assert.Single(store.Diary());
    }
}

