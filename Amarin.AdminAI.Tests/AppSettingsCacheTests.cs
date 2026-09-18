using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// <see cref="AppSettingsStore.Load"/> держит текст файла в памяти и перечитывает его только
/// после настоящей правки.
/// </summary>
/// <remarks>
/// Настройки спрашивают движок чата, хост агентов, очередь подтверждений и оба генератора —
/// то есть по нескольку раз за ход, а раньше каждый такой вопрос открывал файл заново.
/// </remarks>
public sealed class AppSettingsCacheTests
{
    [Fact]
    public void A_second_load_does_not_touch_the_file()
    {
        var root = NewTempRoot();
        try
        {
            var store = new AppSettingsStore(root);
            store.Save(new AppSettings { MainPrompt = "первое" });
            Assert.Equal("первое", store.Load().MainPrompt);

            // Подменяем содержимое, сохранив отметку времени: для хранилища файл «не менялся»,
            // и прочитанное раньше обязано остаться в силе.
            var file = Path.Combine(root, "settings.json");
            var stamp = File.GetLastWriteTimeUtc(file);
            File.WriteAllText(file, """{"MainPrompt":"подменённое"}""");
            File.SetLastWriteTimeUtc(file, stamp);

            Assert.Equal("первое", store.Load().MainPrompt);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void A_real_change_is_picked_up()
    {
        var root = NewTempRoot();
        try
        {
            var store = new AppSettingsStore(root);
            store.Save(new AppSettings { MainPrompt = "первое" });
            Assert.Equal("первое", store.Load().MainPrompt);

            var file = Path.Combine(root, "settings.json");
            File.WriteAllText(file, """{"MainPrompt":"второе"}""");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1));

            Assert.Equal("второе", store.Load().MainPrompt);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Saving_refreshes_what_the_next_load_sees()
    {
        var root = NewTempRoot();
        try
        {
            var store = new AppSettingsStore(root);
            store.Save(new AppSettings { MainPrompt = "первое" });
            Assert.Equal("первое", store.Load().MainPrompt);

            store.Save(new AppSettings { MainPrompt = "второе" });
            Assert.Equal("второе", store.Load().MainPrompt);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Every_load_returns_its_own_object()
    {
        var root = NewTempRoot();
        try
        {
            var store = new AppSettingsStore(root);
            store.Save(new AppSettings { MainPrompt = "общий" });

            var mine = store.Load();
            mine.MainPrompt = "правка, которую ещё не сохранили";

            // Страница настроек правит свой объект до нажатия «сохранить», и движок не должен
            // видеть эту правку раньше времени.
            Assert.Equal("общий", store.Load().MainPrompt);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void A_blank_prompt_skips_the_legacy_comparison_but_a_legacy_one_still_migrates()
    {
        var settings = new AppSettings { MainPrompt = "", TechAiPrompt = "" };
        Assert.False(AppSettingsStore.MigrateLegacyChatPrompts(settings));

        var legacy = new AppSettings
        {
            MainPrompt = AppSettingsStore.LegacyPersonalityPrompts[0],
            TechAiPrompt = LegacyTechPrompts.V17
        };

        Assert.True(AppSettingsStore.MigrateLegacyChatPrompts(legacy));
        Assert.Equal("", legacy.MainPrompt);
        Assert.Equal("", legacy.TechAiPrompt);
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Временная папка; ронять из-за неё тест не стоит.
        }
    }
}
