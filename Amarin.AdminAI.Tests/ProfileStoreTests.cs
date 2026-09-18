using Amarin.Core;

namespace Amarin.AdminAI.Tests;

public sealed class ProfileStoreTests
{
    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-prof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public void Existing_chats_stay_where_they_are_when_profiles_appear()
    {
        // The whole point of the default profile: an upgrade must not move a single chat.
        var root = NewTempRoot();
        try
        {
            var chats = new ChatStore(root);
            var existing = chats.CreateNew("grok-4-6");
            existing.Title = "Старая беседа";
            existing.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "привет" });
            chats.Save(existing);
            chats.Flush();
            var fileBefore = Path.Combine(root, "chats", existing.Id + ".json");
            Assert.True(File.Exists(fileBefore));

            var store = new ProfileStore(root);
            var registry = store.Load();
            store.Save(registry);

            // The default profile's data root is the app root itself.
            Assert.Equal(root, store.DataRootFor(ProfileStore.DefaultProfileId));
            Assert.True(File.Exists(fileBefore));

            // Adding a second profile must not disturb the first one's files either.
            store.Create(registry, "Второй");
            Assert.True(File.Exists(fileBefore));

            var reloaded = new ChatStore(store.DataRootFor(ProfileStore.DefaultProfileId));
            var loaded = reloaded.TryLoad(existing.Id);
            Assert.NotNull(loaded);
            Assert.Equal("Старая беседа", loaded!.Title);
            Assert.Single(reloaded.List());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void A_new_profile_starts_with_its_own_empty_chat_list()
    {
        var root = NewTempRoot();
        try
        {
            var defaultChats = new ChatStore(root);
            var session = defaultChats.CreateNew("grok-4-6");
            session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "привет" });
            defaultChats.Save(session);
            defaultChats.Flush();

            var store = new ProfileStore(root);
            var registry = store.Load();
            var second = store.Create(registry, "Второй");

            var secondChats = new ChatStore(store.DataRootFor(second.Id));
            Assert.Empty(secondChats.List());
            Assert.Single(defaultChats.List());
            Assert.NotEqual(store.DataRootFor(second.Id), store.DataRootFor(ProfileStore.DefaultProfileId));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Registry_roundtrips()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ProfileStore(root);
            var registry = store.Load();
            var created = store.Create(registry, "Рабочий");
            created.LockOnStartup = true;
            var (hash, salt) = PasswordHash.Create("секрет123");
            created.PasswordHash = hash;
            created.PasswordSalt = salt;
            registry.ActiveProfileId = created.Id;
            store.Save(registry);

            var loaded = new ProfileStore(root).Load();

            Assert.Equal(created.Id, loaded.ActiveProfileId);
            var profile = Assert.Single(loaded.Profiles, p => p.Id == created.Id);
            Assert.Equal("Рабочий", profile.Name);
            Assert.True(profile.HasPassword);
            Assert.True(profile.IsLocked);
            Assert.True(PasswordHash.Verify("секрет123", profile.PasswordHash, profile.PasswordSalt));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Missing_or_corrupt_registry_falls_back_to_a_usable_default()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ProfileStore(root);

            var fresh = store.Load();
            Assert.Single(fresh.Profiles);
            Assert.Equal(ProfileStore.DefaultProfileId, fresh.ActiveProfileId);

            File.WriteAllText(Path.Combine(root, "profiles.json"), "{ this is not json");
            var recovered = store.Load();
            Assert.Single(recovered.Profiles);
            Assert.Equal(ProfileStore.DefaultProfileId, recovered.ActiveProfileId);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Active_id_pointing_at_a_deleted_profile_falls_back_to_default()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ProfileStore(root);
            var registry = store.Load();
            var second = store.Create(registry, "Второй");
            registry.ActiveProfileId = second.Id;
            store.Save(registry);

            store.Delete(registry, second.Id);

            var loaded = new ProfileStore(root).Load();
            Assert.Equal(ProfileStore.DefaultProfileId, loaded.ActiveProfileId);
            Assert.DoesNotContain(loaded.Profiles, p => p.Id == second.Id);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void The_default_profile_can_never_be_deleted()
    {
        // Its directory is the shared app root — deleting it would take everything else too.
        var root = NewTempRoot();
        try
        {
            var store = new ProfileStore(root);
            var registry = store.Load();

            Assert.False(store.Delete(registry, ProfileStore.DefaultProfileId));
            Assert.True(Directory.Exists(root));
            Assert.Contains(registry.Profiles, p => ProfileStore.IsDefault(p.Id));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Deleting_a_profile_removes_only_its_own_data()
    {
        var root = NewTempRoot();
        try
        {
            var defaultChats = new ChatStore(root);
            var kept = defaultChats.CreateNew("grok-4-6");
            kept.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "остаётся" });
            defaultChats.Save(kept);
            defaultChats.Flush();

            var store = new ProfileStore(root);
            var registry = store.Load();
            var second = store.Create(registry, "Второй");
            var secondRoot = store.DataRootFor(second.Id);

            Assert.True(store.Delete(registry, second.Id));

            Assert.False(Directory.Exists(secondRoot));
            Assert.Single(defaultChats.List());
            Assert.NotNull(new ChatStore(root).TryLoad(kept.Id));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Password_verification_rejects_the_wrong_password()
    {
        var (hash, salt) = PasswordHash.Create("правильный");

        Assert.True(PasswordHash.Verify("правильный", hash, salt));
        Assert.False(PasswordHash.Verify("неправильный", hash, salt));
        Assert.False(PasswordHash.Verify("", hash, salt));
        Assert.False(PasswordHash.Verify(null, hash, salt));
        Assert.False(PasswordHash.Verify("правильный", hash, "bm90LXRoZS1zYWx0"));
        Assert.False(PasswordHash.Verify("правильный", null, null));
    }

    [Fact]
    public void Each_password_gets_its_own_salt()
    {
        var first = PasswordHash.Create("одинаковый");
        var second = PasswordHash.Create("одинаковый");

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void Lock_needs_an_actual_password()
    {
        var profile = new UserProfile { Id = "x", LockOnStartup = true };

        // A lock flag with no password would otherwise produce an unopenable dialog.
        Assert.False(profile.HasPassword);
        Assert.False(profile.IsLocked);
    }
}
