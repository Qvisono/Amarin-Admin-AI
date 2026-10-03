using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Переезд на другой профиль без окна. До 1.30.0 это был метод «сумки служб» окна, и проверить
/// его можно было только оконным тестом.
/// </summary>
public sealed class ProfileScopeTests : IDisposable
{
    private readonly string _first = Folder("a");
    private readonly string _second = Folder("b");

    public void Dispose()
    {
        foreach (var root in new[] { _first, _second })
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Временная папка — уберёт система.
            }
        }
    }

    [Fact]
    public void Switching_moves_settings_chats_and_prompts_to_the_other_folder()
    {
        new AppSettingsStore(_second).Save(new AppSettings { ChatModelId = "glm-5" });
        var scope = Scope(_first);
        var firstStore = scope.ChatStore;

        scope.UseProfile(_second);

        Assert.Equal(_second, scope.DataRoot);
        Assert.Equal("glm-5", scope.Settings.ChatModelId);
        Assert.NotSame(firstStore, scope.ChatStore);

        scope.ChatStore.Save(Chat("в-новом"));
        scope.ChatStore.Flush();
        Assert.Contains(scope.ChatStore.List(), entry => entry.Id == "в-новом");
        Assert.DoesNotContain(new ChatStore(_first).List(), entry => entry.Id == "в-новом");
    }

    [Fact]
    public void What_the_old_store_had_not_written_yet_lands_before_the_switch()
    {
        // Прежнее хранилище пишет в фоне, а через мгновение на него уже никто не сошлётся.
        var scope = Scope(_first);
        scope.ChatStore.Save(Chat("недописанный"));

        scope.UseProfile(_second);

        Assert.Contains(new ChatStore(_first).List(), entry => entry.Id == "недописанный");
    }

    [Fact]
    public void Objects_others_hold_are_moved_in_place_not_replaced()
    {
        // Их ссылки уже розданы движку, агенту, инструментам и копиям настроек.
        var scope = Scope(_first);
        var ledger = scope.Ledger;
        var instructions = scope.Instructions;
        var organizer = scope.Organizer;
        var recipes = scope.Recipes;

        scope.UseProfile(_second);

        Assert.Same(ledger, scope.Ledger);
        Assert.Same(instructions, scope.Instructions);
        Assert.Same(organizer, scope.Organizer);
        Assert.Same(recipes, scope.Recipes);

        var folder = scope.Organizer.CreateFolder("Работа");
        Assert.Contains(new ChatOrganizer(_second).Snapshot().Folders, item => item.Id == folder.Id);
        Assert.DoesNotContain(new ChatOrganizer(_first).Snapshot().Folders, item => item.Id == folder.Id);
    }

    [Fact]
    public void A_chat_deleted_after_the_switch_leaves_the_layout_of_the_new_profile()
    {
        // Раскладка слушает удаление у нового хранилища, а не у брошенного.
        var scope = Scope(_first);
        _ = scope.Organizer;
        scope.UseProfile(_second);

        scope.ChatStore.Save(Chat("лишний"));
        scope.ChatStore.Flush();
        var folder = scope.Organizer.CreateFolder("Папка");
        scope.Organizer.MoveToFolder(["лишний"], folder.Id);
        Assert.Equal(folder.Id, scope.Organizer.PlacementOf("лишний").FolderId);

        scope.ChatStore.Delete("лишний");

        Assert.Null(scope.Organizer.PlacementOf("лишний").FolderId);
    }

    [Fact]
    public void The_chosen_key_and_the_secret_list_come_from_the_profile()
    {
        var secrets = new SecretRegistry();
        var keys = new ApiKeyProvider();
        var scope = new ProfileScope(
            _first,
            new AppSettingsStore(_first),
            new AppSettings(),
            keys,
            new SpendLedger(_first),
            new InstructionLibrary(_first),
            environmentKey: "vk-environment-0123456789",
            secrets: secrets);

        scope.ApplyActiveKey();

        Assert.Equal("vk-environment-0123456789", keys.Current);
        Assert.Contains("vk-environment-0123456789", secrets.Current);
    }

    [Fact]
    public void Chats_are_encrypted_by_the_setting_of_the_profile_open_now()
    {
        new AppSettingsStore(_second).Save(new AppSettings { EncryptChats = false });
        var scope = Scope(_first);
        scope.Settings.EncryptChats = true;

        scope.UseProfile(_second);

        Assert.False(scope.ChatStore.Encrypt());
        scope.Settings.EncryptChats = true;
        Assert.True(scope.ChatStore.Encrypt());
    }

    private static ProfileScope Scope(string root) =>
        new(root, new AppSettingsStore(root), new AppSettingsStore(root).Load(), new ApiKeyProvider(), new SpendLedger(root), new InstructionLibrary(root));

    private static ChatSession Chat(string id)
    {
        var now = DateTime.Now;
        return new ChatSession
        {
            Id = id,
            Title = "Чат " + id,
            CreatedAt = now,
            UpdatedAt = now,
            Messages = [new ChatDisplayMessage { Role = "user", Id = id + "-u", CreatedAt = now, Text = "привет" }]
        };
    }

    private static string Folder(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-scope-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "chats"));
        return root;
    }
}
