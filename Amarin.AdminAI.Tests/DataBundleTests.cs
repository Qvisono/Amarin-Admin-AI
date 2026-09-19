using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Экспорт и импорт всех данных программы одним архивом.
/// </summary>
/// <remarks>
/// Всё считается по временным папкам: файлы пользователя в <c>%APPDATA%</c> тесты не трогают.
/// Больше половины проверок здесь — про то, чего не должно случиться: пароль не должен уехать в
/// архив, чужой архив не должен писать за пределы папки данных и менять белый список загрузок.
/// </remarks>
public sealed class DataBundleTests
{
    // ───────────────────────── обвязка ─────────────────────────

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Временная папка, уборка по возможности.
        }
    }

    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Encoding.UTF8);
    }

    /// <summary>Чат на диске в том же виде, в каком его пишет <see cref="ChatStore"/>.</summary>
    private static ChatSession SeedChat(string root, string id, string title)
    {
        var session = new ChatSession
        {
            Id = id,
            Title = title,
            CreatedAt = new DateTime(2026, 1, 1),
            UpdatedAt = new DateTime(2026, 1, 2)
        };

        // Flush: хранилище пишет в фоне, а архив читает чаты прямо с диска.
        var store = new ChatStore(root);
        store.Save(session);
        store.Flush();
        return session;
    }

    private static ProfileRegistry SeedProfiles(string root, params UserProfile[] profiles)
    {
        var registry = new ProfileRegistry
        {
            ActiveProfileId = profiles.Length > 0 ? profiles[0].Id : ProfileStore.DefaultProfileId,
            Profiles = profiles.ToList()
        };

        new ProfileStore(root).Save(registry);
        return registry;
    }

    private static UserProfile Profile(string id, string name, bool withPassword = false) => new()
    {
        Id = id,
        Name = name,
        CreatedAt = new DateTime(2026, 1, 1),
        PasswordHash = withPassword ? "hash-" + id : null,
        PasswordSalt = withPassword ? "salt-" + id : null,
        LockOnStartup = withPassword
    };

    private static string Export(string root, DataCategory categories, string? activeProfileId = null)
    {
        var archive = Path.Combine(Path.GetTempPath(), "amarin-bundle-" + Guid.NewGuid().ToString("N") + ".amrnbak");
        new DataBundleExporter(root, activeProfileId).Write(archive, categories);
        return archive;
    }

    private static List<string> EntryNames(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries.Select(entry => entry.FullName).ToList();
    }

    private static string ReadEntry(string archivePath, string name)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static DataBundleManifest Manifest(string archivePath) =>
        JsonSerializer.Deserialize<DataBundleManifest>(
            ReadEntry(archivePath, DataBundle.ManifestName), DataBundle.Json)!;

    /// <summary>Архив, собранный руками: так проверяются враждебные и повреждённые файлы.</summary>
    private static string HandMade(DataBundleManifest manifest, params (string Path, string Body)[] files)
    {
        var archive = Path.Combine(Path.GetTempPath(), "amarin-evil-" + Guid.NewGuid().ToString("N") + ".amrnbak");
        using var stream = new FileStream(archive, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (path, body) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
            writer.Write(body);
        }

        using var description = new StreamWriter(
            zip.CreateEntry(DataBundle.ManifestName).Open(), new UTF8Encoding(false));
        description.Write(JsonSerializer.Serialize(manifest, DataBundle.Json));
        return archive;
    }

    private static DataBundleManifest ManifestFor(params (string Path, DataCategory Category)[] entries) => new()
    {
        AppVersion = "1.0.0",
        CreatedAt = new DateTime(2026, 1, 1),
        ActiveProfileId = ProfileStore.DefaultProfileId,
        Categories = [.. entries.Select(e => e.Category).Distinct()],
        Entries = [.. entries.Select(e => new DataBundleEntry
        {
            Path = e.Path,
            Category = e.Category,
            Bytes = 16
        })]
    };

    // ───────────────────────── раскладка по категориям ─────────────────────────

    [Fact]
    public void Every_app_file_lands_in_exactly_one_export_category()
    {
        Assert.Equal(DataCategory.Chats, DataBundle.CategoryOf("chats/ab12.json"));
        Assert.Equal(DataCategory.Settings, DataBundle.CategoryOf("settings.json"));
        Assert.Equal(DataCategory.Profiles, DataBundle.CategoryOf("profiles.json"));
        Assert.Equal(DataCategory.Appearance, DataBundle.CategoryOf("avatar.png"));
        Assert.Equal(DataCategory.Appearance, DataBundle.CategoryOf("background.jpg"));
        Assert.Equal(DataCategory.Languages, DataBundle.CategoryOf("languages/de.json"));

        // Баланс Venice перетрётся при первом же ходе, экспортированные чаты уже лежат файлами,
        // записка от второго запуска живёт секунды, а недописанный .tmp — вообще не файл.
        Assert.Equal(DataCategory.None, DataBundle.CategoryOf("balance.json"));
        Assert.Equal(DataCategory.None, DataBundle.CategoryOf("shared/chat-1.amrnchat"));
        Assert.Equal(DataCategory.None, DataBundle.CategoryOf("handoff/ab12.json"));
        Assert.Equal(DataCategory.None, DataBundle.CategoryOf("settings.json.tmp"));
    }

    [Fact]
    public void An_archive_holds_only_the_categories_that_were_ticked()
    {
        var root = NewTempRoot();
        try
        {
            SeedChat(root, "aaa", "Проверка сети");
            WriteText(Path.Combine(root, "settings.json"), "{}");
            WriteText(Path.Combine(root, "languages", "de.json"), """{"S.Common.Save":"Sichern"}""");

            var archive = Export(root, DataCategory.Chats);
            try
            {
                var names = EntryNames(archive);
                Assert.Contains("data/chats/aaa.json", names);
                Assert.DoesNotContain("data/settings.json", names);
                Assert.DoesNotContain("data/languages/de.json", names);
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void The_manifest_names_every_file_in_the_archive()
    {
        var root = NewTempRoot();
        try
        {
            SeedChat(root, "aaa", "Первый");
            SeedChat(root, "bbb", "Второй");
            WriteText(Path.Combine(root, "settings.json"), "{}");

            var archive = Export(root, DataCategory.All);
            try
            {
                // Опись работает на импорте белым списком, поэтому она обязана покрывать архив
                // целиком: файл мимо описи — это файл, который никто не проверял.
                var inArchive = EntryNames(archive).Where(n => n != DataBundle.ManifestName).ToHashSet(StringComparer.Ordinal);
                var inManifest = Manifest(archive).Entries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);

                Assert.Equal(inManifest, inArchive);
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void The_suggested_file_name_carries_the_date()
    {
        var name = DataBundle.SuggestedFileName(new DateTime(2026, 9, 14, 12, 3, 0));

        Assert.Equal("amarin-data-20260914-1203.amrnbak", name);
    }

    // ───────────────────────── пароли и профили ─────────────────────────

    [Fact]
    public void Profile_passwords_never_reach_the_archive()
    {
        var root = NewTempRoot();
        try
        {
            SeedProfiles(root, Profile(ProfileStore.DefaultProfileId, "Валера", withPassword: true));

            var archive = Export(root, DataCategory.All);
            try
            {
                var registry = JsonSerializer.Deserialize<ProfileRegistry>(
                    ReadEntry(archive, "data/profiles.json"), AppJson.Options)!;
                var profile = registry.Profiles.Single();

                Assert.Equal("Валера", profile.Name);
                Assert.Null(profile.PasswordHash);
                Assert.Null(profile.PasswordSalt);

                // Замок без хэша бессмыслен, и файл не должен утверждать, что он есть.
                Assert.False(profile.LockOnStartup);

                // ZIP не шифруется: выжимка пароля не должна лежать в архиве вообще нигде.
                var bytes = File.ReadAllBytes(archive);
                Assert.DoesNotContain("hash-default", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Other_profiles_travel_only_when_profiles_are_ticked()
    {
        var root = NewTempRoot();
        try
        {
            SeedProfiles(
                root,
                Profile(ProfileStore.DefaultProfileId, "Основной"),
                Profile("second12", "Второй"));

            SeedChat(root, "mine", "Мой чат");
            SeedChat(Path.Combine(root, "profiles", "second12"), "other", "Чужой чат");

            var withoutProfiles = Export(root, DataCategory.Chats);
            var withProfiles = Export(root, DataCategory.Chats | DataCategory.Profiles);
            try
            {
                // Категория решает, что берём; галочка «Профили» — у кого.
                Assert.DoesNotContain(
                    EntryNames(withoutProfiles),
                    name => name.StartsWith("data/profiles/", StringComparison.Ordinal));

                Assert.Contains("data/profiles/second12/chats/other.json", EntryNames(withProfiles));
            }
            finally
            {
                File.Delete(withoutProfiles);
                File.Delete(withProfiles);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void A_profile_id_that_is_taken_gets_a_new_folder()
    {
        var source = NewTempRoot();
        var target = NewTempRoot();
        try
        {
            SeedProfiles(
                source,
                Profile(ProfileStore.DefaultProfileId, "Хозяин"),
                Profile("second12", "Второй"));
            SeedChat(Path.Combine(source, "profiles", "second12"), "other", "Чужой чат");

            // На принимающей машине id second12 уже занят другим человеком.
            SeedProfiles(
                target,
                Profile(ProfileStore.DefaultProfileId, "Я"),
                Profile("second12", "Мой второй"));
            SeedChat(Path.Combine(target, "profiles", "second12"), "keep", "Мой чат");

            var archive = Export(source, DataCategory.All);
            try
            {
                var result = new DataBundleImporter(target).Apply(
                    archive, DataCategory.All, DataImportMode.Merge);

                Assert.True(result.Ok);
                Assert.Equal(1, result.ProfilesAdded);

                var store = new ProfileStore(target);
                var registry = store.Load();
                var added = registry.Profiles.Single(p => p.Name == "Второй");

                // Совпадение id увело бы два разных профиля в одну папку — DataRootFor строит
                // путь именно по нему.
                Assert.NotEqual("second12", added.Id);
                Assert.NotEqual(store.DataRootFor("second12"), store.DataRootFor(added.Id));

                Assert.NotNull(new ChatStore(store.DataRootFor(added.Id)).TryLoad("other"));
                Assert.NotNull(new ChatStore(store.DataRootFor("second12")).TryLoad("keep"));
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(source);
            Cleanup(target);
        }
    }

    [Fact]
    public void The_active_profile_survives_a_replacing_import()
    {
        var source = NewTempRoot();
        var target = NewTempRoot();
        try
        {
            SeedProfiles(source, Profile(ProfileStore.DefaultProfileId, "Чужой"));
            SeedProfiles(target, Profile(ProfileStore.DefaultProfileId, "Я", withPassword: true));

            var archive = Export(source, DataCategory.All);
            try
            {
                var result = new DataBundleImporter(target).Apply(
                    archive, DataCategory.All, DataImportMode.Replace);

                Assert.True(result.Ok);

                var registry = new ProfileStore(target).Load();

                // Программа, запущенная под профилем, которого нет в списке, — сломанный список.
                var mine = registry.Profiles.Single(p => p.Id == ProfileStore.DefaultProfileId);
                Assert.Equal(ProfileStore.DefaultProfileId, registry.ActiveProfileId);

                // Имя замена забирает из архива — она и есть восстановление копии. А вот пароль
                // остаётся своим: он про эту машину, а не про перенесённые данные.
                Assert.Equal("Чужой", mine.Name);
                Assert.Equal("hash-default", mine.PasswordHash);
                Assert.Equal("salt-default", mine.PasswordSalt);
                Assert.True(mine.LockOnStartup);
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(source);
            Cleanup(target);
        }
    }

    [Fact]
    public void An_imported_profile_can_never_lock_the_program()
    {
        var target = NewTempRoot();
        try
        {
            var registry = new ProfileRegistry
            {
                ActiveProfileId = ProfileStore.DefaultProfileId,
                Profiles =
                [
                    new UserProfile
                    {
                        Id = "intruder01",
                        Name = "Чужой",
                        PasswordHash = "not-mine",
                        PasswordSalt = "not-mine",
                        LockOnStartup = true
                    }
                ]
            };

            var archive = HandMade(
                ManifestFor(("data/profiles.json", DataCategory.Profiles)),
                ("data/profiles.json", JsonSerializer.Serialize(registry, AppJson.Options)));

            try
            {
                var result = new DataBundleImporter(target).Apply(
                    archive, DataCategory.Profiles, DataImportMode.Merge);

                Assert.True(result.Ok);

                // Архив можно собрать руками: хэш с lockOnStartup запер бы программу насмерть.
                var added = new ProfileStore(target).Load().Profiles.Single(p => p.Name == "Чужой");
                Assert.Null(added.PasswordHash);
                Assert.Null(added.PasswordSalt);
                Assert.False(added.LockOnStartup);
                Assert.False(added.IsLocked);
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(target);
        }
    }

    // ───────────────────────── чаты ─────────────────────────

    [Fact]
    public void Merging_a_chat_whose_id_is_taken_keeps_both()
    {
        var source = NewTempRoot();
        var target = NewTempRoot();
        try
        {
            SeedChat(source, "same", "Чат из архива");
            SeedChat(target, "same", "Мой чат");

            var archive = Export(source, DataCategory.Chats);
            try
            {
                var result = new DataBundleImporter(target).Apply(
                    archive, DataCategory.Chats, DataImportMode.Merge);

                Assert.True(result.Ok);
                Assert.Equal(1, result.ChatsAdded);

                var store = new ChatStore(target);

                // Отличить свой же экспортированный чат от чужого с тем же id нельзя, а
                // перезаписать свой разговор чужим — худший из исходов.
                Assert.Equal("Мой чат", store.TryLoad("same")!.Title);

                var titles = store.List().Select(item => item.Title).ToList();
                Assert.Equal(2, titles.Count);
                Assert.Contains(titles, title => title.Contains("Чат из архива", StringComparison.Ordinal));
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(source);
            Cleanup(target);
        }
    }

    [Fact]
    public void Merging_keeps_the_pin_that_lives_only_in_the_index()
    {
        var source = NewTempRoot();
        var target = NewTempRoot();
        try
        {
            SeedChat(source, "pinned", "Закреплённый");
            SeedChat(source, "plain", "Обычный");

            var pinning = new ChatStore(source);
            pinning.SetPinned("pinned", true);
            pinning.Flush();

            var archive = Export(source, DataCategory.Chats);
            try
            {
                new DataBundleImporter(target).Apply(archive, DataCategory.Chats, DataImportMode.Merge);

                // IsPinned лежит только в chats/index.json: без отдельного прохода по индексу
                // закрепления терялись бы молча — в файле самого чата их нет.
                var entry = new ChatStore(target).List().Single(item => item.Id == "pinned");
                Assert.True(entry.IsPinned);
                Assert.False(new ChatStore(target).List().Single(item => item.Id == "plain").IsPinned);
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(source);
            Cleanup(target);
        }
    }

    [Fact]
    public void Replacing_wipes_the_chats_it_replaces()
    {
        var source = NewTempRoot();
        var target = NewTempRoot();
        try
        {
            SeedChat(source, "fromarchive", "Из архива");
            SeedChat(target, "mineonly", "Только мой");

            var archive = Export(source, DataCategory.Chats);
            try
            {
                var result = new DataBundleImporter(target).Apply(
                    archive, DataCategory.Chats, DataImportMode.Replace);

                Assert.True(result.Ok);
                Assert.Equal(1, result.ChatsReplaced);

                var store = new ChatStore(target);
                Assert.Null(store.TryLoad("mineonly"));

                // Замена — это восстановление копии: id обязаны остаться своими, иначе
                // «экспорт → импорт» переставал бы давать ту же историю.
                Assert.Equal("Из архива", store.TryLoad("fromarchive")!.Title);
                Assert.Single(store.List());
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(source);
            Cleanup(target);
        }
    }

    [Fact]
    public void A_damaged_chat_is_skipped_and_the_rest_arrive()
    {
        var source = NewTempRoot();
        var target = NewTempRoot();
        try
        {
            SeedChat(source, "one", "Первый");
            SeedChat(source, "two", "Второй");
            WriteText(Path.Combine(source, "chats", "broken.json"), "{ это не json ");

            var archive = Export(source, DataCategory.Chats);
            try
            {
                var result = new DataBundleImporter(target).Apply(
                    archive, DataCategory.Chats, DataImportMode.Merge);

                // Один битый чат не должен отменять перенос остальных.
                Assert.True(result.Ok);
                Assert.Equal(2, result.ChatsAdded);
                Assert.Equal(1, result.Skipped);
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(source);
            Cleanup(target);
        }
    }

    // ───────────────────────── настройки и безопасность ─────────────────────────

    [Fact]
    public void Merging_leaves_my_settings_alone_and_replacing_does_not()
    {
        var source = NewTempRoot();
        var target = NewTempRoot();
        try
        {
            new AppSettingsStore(source).Save(new AppSettings { MainPrompt = "из архива" });
            new AppSettingsStore(target).Save(new AppSettings { MainPrompt = "моё" });

            var archive = Export(source, DataCategory.Settings);
            try
            {
                new DataBundleImporter(target).Apply(archive, DataCategory.Settings, DataImportMode.Merge);

                // Слить настройки по полям значило бы тихо поменять полсотни переключателей
                // одним кликом. «Взять чужой файл целиком» — это уже замена.
                Assert.Equal("моё", new AppSettingsStore(target).Load().MainPrompt);

                new DataBundleImporter(target).Apply(archive, DataCategory.Settings, DataImportMode.Replace);
                Assert.Equal("из архива", new AppSettingsStore(target).Load().MainPrompt);
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(source);
            Cleanup(target);
        }
    }

    [Fact]
    public void The_download_allowlist_never_comes_from_the_archive()
    {
        var source = NewTempRoot();
        var target = NewTempRoot();
        try
        {
            new AppSettingsStore(source).Save(new AppSettings
            {
                MainPrompt = "из архива",
                DownloadAllowedDomains = ["evil.example"],
                ApprovalMode = ApprovalMode.AlwaysApprove
            });

            new AppSettingsStore(target).Save(new AppSettings
            {
                DownloadAllowedDomains = ["github.com"],
                ApprovalMode = ApprovalMode.Normal
            });

            var archive = Export(source, DataCategory.Settings);
            try
            {
                new DataBundleImporter(target).Apply(archive, DataCategory.Settings, DataImportMode.Replace);

                var settings = new AppSettingsStore(target).Load();

                // Белый список решает, откуда агенту разрешено качать файлы на этот компьютер,
                // а режим подтверждений — спрашивать ли человека перед опасным действием. Ни то,
                // ни другое чужой файл менять не должен, даже в режиме полной замены.
                var domains = settings.DownloadAllowedDomains ?? [];
                Assert.Equal("из архива", settings.MainPrompt);
                Assert.DoesNotContain("evil.example", domains);
                Assert.Contains("github.com", domains);
                Assert.Equal(ApprovalMode.Normal, settings.ApprovalMode);
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(source);
            Cleanup(target);
        }
    }

    [Fact]
    public void The_prompt_library_travels_with_the_settings()
    {
        // Заготовки основного промпта лежат своим файлом, и один раз он уже выпал из архива:
        // незнакомый классификатору файл экспорт молча не берёт.
        var source = NewTempRoot();
        var target = NewTempRoot();
        try
        {
            new PromptLibrary(source).Save(
            [
                new PromptPreset { Id = "a", Name = "Переводчик", Text = "переводи" },
                new PromptPreset { Id = "b", Name = "Редактор", Text = "правь" }
            ]);

            var archive = Export(source, DataCategory.Settings);
            try
            {
                Assert.Contains("data/prompts.json", EntryNames(archive));

                new DataBundleImporter(target).Apply(archive, DataCategory.Settings, DataImportMode.Replace);

                Assert.Equal(
                    ["Переводчик", "Редактор"],
                    new PromptLibrary(target).Load().Select(preset => preset.Name));
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(source);
            Cleanup(target);
        }
    }

    [Fact]
    public void Merging_adds_the_missing_presets_and_doubles_none()
    {
        var source = NewTempRoot();
        var target = NewTempRoot();
        try
        {
            new PromptLibrary(source).Save(
            [
                new PromptPreset { Id = "a", Name = "Переводчик", Text = "переводи" },
                new PromptPreset { Id = "чужой-id", Name = "Редактор", Text = "правь" },
                new PromptPreset { Id = "c", Name = "Новая", Text = "новое" }
            ]);

            new PromptLibrary(target).Save(
            [
                new PromptPreset { Id = "a", Name = "Переводчик", Text = "мой текст" },
                new PromptPreset { Id = "мой-id", Name = "Редактор", Text = "правь" }
            ]);

            var archive = Export(source, DataCategory.Settings);
            try
            {
                new DataBundleImporter(target).Apply(archive, DataCategory.Settings, DataImportMode.Merge);

                var mine = new PromptLibrary(target).Load();

                // Совпадение по id — моя же заготовка, вернувшаяся из копии: её текст остаётся
                // моим. Совпадение по паре «имя + текст» — та же заготовка, заведённая руками
                // на двух машинах; без этой проверки в списке стояли бы две неотличимые плитки.
                Assert.Equal(["Переводчик", "Редактор", "Новая"], mine.Select(preset => preset.Name));
                Assert.Equal("мой текст", mine[0].Text);
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(source);
            Cleanup(target);
        }
    }

    // ───────────────────────── враждебные архивы ─────────────────────────

    [Fact]
    public void An_import_never_writes_outside_the_data_folder()
    {
        var target = NewTempRoot();
        var outside = Path.Combine(Path.GetTempPath(), "amarin-escape-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            SeedChat(target, "keep", "Мой чат");
            var before = File.ReadAllText(Path.Combine(target, "chats", "keep.json"));

            foreach (var path in new[] { "data/../../escape.txt", @"data\..\..\escape.txt", "data/chats/../../../escape.txt" })
            {
                var archive = HandMade(
                    ManifestFor((path, DataCategory.Chats), ("data/chats/good.json", DataCategory.Chats)),
                    (path, "выход наружу"),
                    ("data/chats/good.json", """{"id":"good","title":"Хороший"}"""));

                try
                {
                    var result = new DataBundleImporter(target).Apply(
                        archive, DataCategory.Chats, DataImportMode.Merge);

                    // Архив, пытающийся вылезти за пределы папки данных, не «частично плохой»,
                    // он враждебный: берём из него ноль записей, а не все, кроме одной.
                    Assert.False(result.Ok);
                    Assert.False(File.Exists(outside));
                    Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "escape.txt")));
                    Assert.Null(new ChatStore(target).TryLoad("good"));
                    Assert.Equal(before, File.ReadAllText(Path.Combine(target, "chats", "keep.json")));
                }
                finally
                {
                    File.Delete(archive);
                }
            }
        }
        finally
        {
            Cleanup(target);
            File.Delete(outside);
        }
    }

    [Fact]
    public void A_failed_import_leaves_the_data_untouched()
    {
        var target = NewTempRoot();
        try
        {
            SeedChat(target, "keep", "Мой чат");
            var before = File.ReadAllText(Path.Combine(target, "chats", "keep.json"));

            // Пять исправных записей и одна враждебная последней: если бы раскладка шла сразу на
            // места, к моменту отказа пять чужих чатов уже лежали бы в папке.
            var good = Enumerable.Range(0, 5)
                .Select(i => ($"data/chats/good{i}.json", $$"""{"id":"good{{i}}","title":"Хороший"}"""))
                .ToArray();

            var entries = good
                .Select(pair => (pair.Item1, DataCategory.Chats))
                .Append(("data/../../escape.txt", DataCategory.Chats))
                .ToArray();

            var archive = HandMade(ManifestFor(entries), [.. good, ("data/../../escape.txt", "наружу")]);
            try
            {
                var result = new DataBundleImporter(target).Apply(
                    archive, DataCategory.Chats, DataImportMode.Merge);

                Assert.False(result.Ok);
                Assert.Single(new ChatStore(target).List());
                Assert.Equal(before, File.ReadAllText(Path.Combine(target, "chats", "keep.json")));

                // Временная папка распаковки не должна пережить отказ.
                Assert.Empty(Directory.GetDirectories(target, ".import-*"));
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(target);
        }
    }

    [Fact]
    public void An_archive_that_unpacks_too_much_is_refused()
    {
        var source = NewTempRoot();
        var target = NewTempRoot();
        try
        {
            WriteText(Path.Combine(source, "chats", "big.json"), new string('x', 8000));
            SeedChat(target, "keep", "Мой чат");

            var archive = Export(source, DataCategory.Chats);
            try
            {
                // Потолки — параметр, а не константа: иначе проверить их можно было бы только
                // собрав настоящую zip-бомбу и распаковав гигабайты на диск разработчика.
                var limits = new DataBundleLimits(MaxUnpackedBytes: 1024, MaxEntryBytes: 1024);
                var result = new DataBundleImporter(target, null, limits).Apply(
                    archive, DataCategory.Chats, DataImportMode.Merge);

                Assert.Equal(DataBundleError.TooLarge, result.Error);
                Assert.Single(new ChatStore(target).List());
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(source);
            Cleanup(target);
        }
    }

    [Fact]
    public void A_foreign_zip_is_refused_by_name_not_by_exception()
    {
        var target = NewTempRoot();
        var zip = Path.Combine(Path.GetTempPath(), "amarin-foreign-" + Guid.NewGuid().ToString("N") + ".zip");
        var text = Path.Combine(Path.GetTempPath(), "amarin-plain-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            using (var stream = new FileStream(zip, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("readme.txt").Open()))
            {
                writer.Write("чужой архив");
            }

            File.WriteAllText(text, "это вообще не архив");

            var importer = new DataBundleImporter(target);

            // Непонятный файл — обычный ответ, а не авария: человеку показывают строку, а не
            // текст исключения.
            Assert.Equal(DataBundleError.NotOurArchive, importer.Inspect(zip).Error);
            Assert.Equal(DataBundleError.NotAZip, importer.Inspect(text).Error);
            Assert.Equal(DataBundleError.Unreadable, importer.Inspect(Path.Combine(target, "нет-такого")).Error);
        }
        finally
        {
            Cleanup(target);
            File.Delete(zip);
            File.Delete(text);
        }
    }

    [Fact]
    public void A_newer_format_is_refused_with_an_explanation()
    {
        var target = NewTempRoot();
        try
        {
            var manifest = ManifestFor(("data/chats/one.json", DataCategory.Chats));
            manifest.FormatVersion = 99;

            var archive = HandMade(manifest, ("data/chats/one.json", """{"id":"one"}"""));
            try
            {
                var inspection = new DataBundleImporter(target).Inspect(archive);

                Assert.Equal(DataBundleError.NewerFormat, inspection.Error);
                Assert.Equal("S.Bundle.Error.NewerFormat", DataBundle.ErrorKey(inspection.Error));
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(target);
        }
    }

    // ───────────────────────── круг ─────────────────────────

    [Fact]
    public void An_exported_archive_imports_back_into_the_same_data()
    {
        var source = NewTempRoot();
        var target = NewTempRoot();
        try
        {
            SeedChat(source, "aaa", "Проверка сети");
            SeedChat(source, "bbb", "Разбор диска");
            new ChatStore(source).SetPinned("aaa", true);
            new AppSettingsStore(source).Save(new AppSettings { MainPrompt = "мой промпт" });
            SeedProfiles(source, Profile(ProfileStore.DefaultProfileId, "Валера"));
            WriteText(Path.Combine(source, "languages", "de.json"), """{"S.Common.Save":"Sichern"}""");

            var archive = Export(source, DataCategory.All);
            try
            {
                var result = new DataBundleImporter(target).Apply(
                    archive, DataCategory.All, DataImportMode.Replace);

                Assert.True(result.Ok);
                Assert.Equal(0, result.Skipped);

                // Ради этого всё и затевалось: переустановил Windows — и получил ту же историю.
                var store = new ChatStore(target);
                Assert.Equal("Проверка сети", store.TryLoad("aaa")!.Title);
                Assert.Equal("Разбор диска", store.TryLoad("bbb")!.Title);
                Assert.True(store.List().Single(item => item.Id == "aaa").IsPinned);
                Assert.Equal("мой промпт", new AppSettingsStore(target).Load().MainPrompt);
                Assert.Contains(new ProfileStore(target).Load().Profiles, p => p.Name == "Валера");
                Assert.True(File.Exists(Path.Combine(target, "languages", "de.json")));
            }
            finally
            {
                File.Delete(archive);
            }
        }
        finally
        {
            Cleanup(source);
            Cleanup(target);
        }
    }

    [Fact]
    public void Languages_arrive_without_anything_but_interface_strings()
    {
        var target = NewTempRoot();
        try
        {
            var archive = HandMade(
                ManifestFor(("data/languages/de.json", DataCategory.Languages),
                            ("data/languages/../evil.json", DataCategory.Languages)),
                ("data/languages/de.json", """{"S.Common.Save":"Sichern","VENICE_API_KEY":"secret"}"""));

            try
            {
                var result = new DataBundleImporter(target).Apply(
                    archive, DataCategory.Languages, DataImportMode.Merge);

                // Путь с «..» валит импорт целиком, поэтому до раскладки дело не доходит вовсе.
                Assert.False(result.Ok);
            }
            finally
            {
                File.Delete(archive);
            }

            var clean = HandMade(
                ManifestFor(("data/languages/de.json", DataCategory.Languages)),
                ("data/languages/de.json", """{"S.Common.Save":"Sichern","VENICE_API_KEY":"secret"}"""));

            try
            {
                var result = new DataBundleImporter(target).Apply(
                    clean, DataCategory.Languages, DataImportMode.Merge);

                Assert.True(result.Ok);
                Assert.Equal(1, result.LanguagesAdded);

                // В словарь языка попадают только подписи интерфейса: чужой файл не подсунет
                // сюда ничего другого.
                var saved = File.ReadAllText(Path.Combine(target, "languages", "de.json"));
                Assert.Contains("Sichern", saved, StringComparison.Ordinal);
                Assert.DoesNotContain("VENICE_API_KEY", saved, StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(clean);
            }
        }
        finally
        {
            Cleanup(target);
        }
    }
}
