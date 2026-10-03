using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Отложенная запись настроек (<see cref="AppSettingsStore.SaveDeferred"/>): ползунки оформления
/// писали <c>settings.json</c> и его копию на каждый тик прямо на потоке окна.
/// </summary>
public sealed class AppSettingsDeferredTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-deferred-" + Guid.NewGuid().ToString("N"));

    public AppSettingsDeferredTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка — уберёт система.
        }
    }

    private string File => Path.Combine(_root, "settings.json");

    [Fact]
    public void A_deferred_save_does_not_touch_the_disk_yet_but_every_reader_sees_it()
    {
        var store = new AppSettingsStore(_root);
        store.Save(new AppSettings { ChatModelId = "grok-4-6" });

        // Первое чтение само дописывает миграции в файл — снимок берётся после него.
        var settings = store.Load();
        var before = System.IO.File.ReadAllText(File);
        settings.ChatModelId = "glm-5";
        settings.ApprovalMode = ApprovalMode.ReadOnly;
        store.SaveDeferred(settings);

        Assert.Equal(before, System.IO.File.ReadAllText(File));

        // Другой экземпляр того же файла — тоже: иначе только что включённое «Только чтение»
        // прошло бы мимо шлюза, который читает настройки своим хранилищем.
        var other = new AppSettingsStore(_root).Load();
        Assert.Equal("glm-5", other.ChatModelId);
        Assert.Equal(ApprovalMode.ReadOnly, other.ApprovalMode);
    }

    [Fact]
    public void Flushing_writes_the_last_deferred_state()
    {
        var store = new AppSettingsStore(_root);
        var settings = store.Load();
        for (var tick = 1; tick <= 20; tick++)
        {
            settings.Appearance.GlassOpacity = tick / 100.0;
            store.SaveDeferred(settings);
        }

        AppSettingsStore.FlushAll();

        Assert.Contains("0.2", System.IO.File.ReadAllText(File), StringComparison.Ordinal);
        Assert.Equal(0.2, new AppSettingsStore(_root).Load().Appearance.GlassOpacity, 3);
    }

    [Fact]
    public void A_later_immediate_save_is_not_overwritten_by_an_older_deferred_one()
    {
        var store = new AppSettingsStore(_root);
        var older = store.Load();
        older.ChatModelId = "старое";
        store.SaveDeferred(older);

        var newer = new AppSettingsStore(_root).Load();
        newer.ChatModelId = "новое";
        new AppSettingsStore(_root).Save(newer);

        AppSettingsStore.FlushAll();
        Thread.Sleep(AppSettingsStore.DeferDelay + TimeSpan.FromMilliseconds(200));

        Assert.Equal("новое", new AppSettingsStore(_root).Load().ChatModelId);
    }

    [Fact]
    public void Update_builds_on_what_is_still_pending()
    {
        var store = new AppSettingsStore(_root);
        var settings = store.Load();
        settings.ChatModelId = "glm-5";
        store.SaveDeferred(settings);

        // Фоновый перенос трат пишет через Update — и не должен вернуть прежнюю модель.
        new AppSettingsStore(_root).Update(item => item.SpendBackfilledAt = new DateTime(2026, 10, 3));

        var saved = new AppSettingsStore(_root).Load();
        Assert.Equal("glm-5", saved.ChatModelId);
        Assert.NotNull(saved.SpendBackfilledAt);
    }

    [Fact]
    public void A_deferred_write_does_not_bring_back_a_deleted_profile_folder()
    {
        var folder = Path.Combine(_root, "profile");
        Directory.CreateDirectory(folder);
        var store = new AppSettingsStore(folder);
        store.SaveDeferred(store.Load());

        Directory.Delete(folder, recursive: true);
        AppSettingsStore.FlushAll();

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void The_deferred_write_lands_by_itself_after_a_pause()
    {
        var store = new AppSettingsStore(_root);
        var settings = store.Load();
        // Латиницей: JSON кодирует кириллицу как \u0441…, и русская строка в файле не нашлась бы.
        settings.ChatModelId = "landed-by-itself";
        store.SaveDeferred(settings);

        var deadline = DateTime.UtcNow + AppSettingsStore.DeferDelay + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline &&
               !(System.IO.File.Exists(File) && System.IO.File.ReadAllText(File).Contains("landed-by-itself", StringComparison.Ordinal)))
        {
            Thread.Sleep(25);
        }

        Assert.Contains("landed-by-itself", System.IO.File.ReadAllText(File), StringComparison.Ordinal);
    }
}
