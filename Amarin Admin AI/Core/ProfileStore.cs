using System.Text.Json;

namespace Amarin.Core;

/// <summary>
/// Reads and writes profiles.json and maps a profile to its data directory.
///
/// The default profile deliberately points at the app root itself, which is where chats and
/// settings have always lived. Adding profiles must never relocate an existing conversation.
/// </summary>
public sealed class ProfileStore
{
    public const string DefaultProfileId = "default";

    private readonly string _root;
    private readonly string _file;

    public ProfileStore(string? rootDirectory = null)
    {
        _root = string.IsNullOrWhiteSpace(rootDirectory) ? AppPaths.Root : rootDirectory;
        _file = Path.Combine(_root, "profiles.json");
    }

    public string FilePath => _file;

    /// <summary>Data directory for a profile: the app root for the default one, a subfolder otherwise.</summary>
    public string DataRootFor(string profileId) =>
        IsDefault(profileId) ? _root : Path.Combine(_root, "profiles", Sanitize(profileId));

    public static bool IsDefault(string? profileId) =>
        string.IsNullOrWhiteSpace(profileId) ||
        profileId.Equals(DefaultProfileId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Never throws: a damaged or missing file yields a fresh single-profile registry.</summary>
    public ProfileRegistry Load()
    {
        ProfileRegistry? registry = null;
        try
        {
            if (File.Exists(_file))
            {
                registry = JsonSerializer.Deserialize<ProfileRegistry>(
                    File.ReadAllText(_file), AppJson.Options);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            registry = null;
        }

        registry ??= new ProfileRegistry();
        if (registry.Profiles.Count == 0)
        {
            registry.Profiles.Add(new UserProfile { Id = DefaultProfileId, Name = "Гость" });
        }

        // Guarantee the default profile exists and that the active id resolves to something.
        if (!registry.Profiles.Any(p => IsDefault(p.Id)))
        {
            registry.Profiles.Insert(0, new UserProfile { Id = DefaultProfileId, Name = "Гость" });
        }

        if (!registry.Profiles.Any(p => p.Id == registry.ActiveProfileId))
        {
            registry.ActiveProfileId = DefaultProfileId;
        }

        return registry;
    }

    public void Save(ProfileRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Directory.CreateDirectory(_root);
        AppDataFile.WriteAtomic(_file, JsonSerializer.Serialize(registry, AppJson.Options));
    }

    /// <summary>
    /// Профиль, под которым работают сейчас. Никогда не бросает и никогда не отдаёт <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Пустой список раньше означал исключение по индексу. <see cref="Load"/> его не допускает —
    /// заводского «Гостя» он подставляет сам, — но реестр можно собрать и мимо него, и тогда
    /// падало не здесь, а там, куда исключение доезжало: на применении оформления при выдаче
    /// окну служб. Пустой реестр — это ровно «профилей ещё нет», то есть заводской профиль,
    /// чья папка и так совпадает с корнем данных.
    /// </remarks>
    public UserProfile Active(ProfileRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Profiles.FirstOrDefault(p => p.Id == registry.ActiveProfileId)
               ?? registry.Profiles.FirstOrDefault()
               ?? new UserProfile { Id = DefaultProfileId, Name = "Гость" };
    }

    /// <summary>Creates a profile with its own empty data directory.</summary>
    public UserProfile Create(ProfileRegistry registry, string name)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var profile = new UserProfile
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = string.IsNullOrWhiteSpace(name) ? "Новый профиль" : name.Trim()
        };

        registry.Profiles.Add(profile);
        Directory.CreateDirectory(Path.Combine(DataRootFor(profile.Id), "chats"));
        Save(registry);
        return profile;
    }

    /// <summary>
    /// Removes a profile and its data. The default profile cannot be deleted — its directory is
    /// the shared app root, so deleting it would take settings and every other profile with it.
    /// </summary>
    public bool Delete(ProfileRegistry registry, string profileId)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (IsDefault(profileId))
        {
            return false;
        }

        var profile = registry.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null)
        {
            return false;
        }

        registry.Profiles.Remove(profile);
        if (registry.ActiveProfileId == profileId)
        {
            registry.ActiveProfileId = DefaultProfileId;
        }

        try
        {
            var directory = DataRootFor(profileId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The registry entry is gone either way; a locked file is not worth failing over.
        }

        Save(registry);
        return true;
    }

    private static string Sanitize(string id)
    {
        var clean = new string(id.Where(char.IsLetterOrDigit).ToArray());
        return clean.Length == 0 ? "profile" : clean;
    }
}
