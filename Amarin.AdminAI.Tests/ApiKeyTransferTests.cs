using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// «Отправить» в меню ключа (1.32.0): ключ ложится в другой профиль без пароля, молча и один раз,
/// а выбор ключа там остаётся прежним.
/// </summary>
public sealed class ApiKeyTransferTests : IDisposable
{
    private const string Secret = "vk-transfer-secret-0123456789";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-transfer-" + Guid.NewGuid().ToString("N"));

    public ApiKeyTransferTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void A_profile_without_keys_gets_the_key_as_its_active_one()
    {
        var target = Profile("empty");

        var result = ApiKeyTransfer.Send(Entry(), target, "", "");

        Assert.Equal(KeyTransferResult.Added, result);
        var keys = Load(target).List();
        var key = Assert.Single(keys);
        Assert.Equal("Рабочий", key.Label);
        Assert.Equal(Secret, key.Secret);
        Assert.Equal(LlmProvider.OpenRouter, key.Provider);
        Assert.True(key.IsActive);
    }

    [Fact]
    public void The_other_profile_keeps_its_own_choice_of_key()
    {
        var target = Profile("busy");
        var store = Load(target);
        var own = store.Add("Свой", "vk-own-key-abcdefghijk")!;

        Assert.Equal(KeyTransferResult.Added, ApiKeyTransfer.Send(Entry(), target, "", ""));

        var keys = Load(target).List();
        Assert.Equal(2, keys.Count);
        Assert.Equal(own.Id, keys.Single(key => key.IsActive).Id);
    }

    [Fact]
    public void A_key_that_is_already_there_is_left_alone()
    {
        var target = Profile("twice");

        Assert.Equal(KeyTransferResult.Added, ApiKeyTransfer.Send(Entry(), target, "", ""));
        Assert.Equal(KeyTransferResult.AlreadyThere, ApiKeyTransfer.Send(Entry(), target, "", ""));

        Assert.Single(Load(target).List());
    }

    [Fact]
    public void A_key_the_environment_already_gives_every_profile_is_not_copied_to_disk()
    {
        var target = Profile("env");

        var result = ApiKeyTransfer.Send(Entry(), target, Secret, "");

        Assert.Equal(KeyTransferResult.AlreadyThere, result);
        Assert.False(File.Exists(Path.Combine(target, "keys.json")));
    }

    [Fact]
    public void An_unreadable_key_or_a_vanished_profile_sends_nothing()
    {
        var broken = Entry() with { Secret = null };

        Assert.Equal(KeyTransferResult.Unreadable, ApiKeyTransfer.Send(broken, Profile("x"), "", ""));
        Assert.Equal(KeyTransferResult.NoProfile, ApiKeyTransfer.Send(Entry(), Path.Combine(_root, "gone"), "", ""));
        Assert.False(Directory.Exists(Path.Combine(_root, "gone")));
    }

    private static ApiKeyEntry Entry() => new("k1", "Рабочий", Secret, ApiKeySource.Stored, true, LlmProvider.OpenRouter);

    private string Profile(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static ApiKeyStore Load(string root)
    {
        var store = new ApiKeyStore(root);
        store.Load();
        return store;
    }
}
