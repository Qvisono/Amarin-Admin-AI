using System.Text.Json;

namespace Amarin.Core;

/// <summary>
/// Читает и пишет profiles.json и сопоставляет профиль с его папкой данных.
///
/// Профиль по умолчанию намеренно смотрит в сам корень программы — там всегда жили чаты и
/// настройки. Новые профили не должны переносить ни одной существующей переписки.
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

    /// <summary>Папка данных профиля: корень программы у профиля по умолчанию, иначе подпапка.</summary>
    public string DataRootFor(string profileId) =>
        IsDefault(profileId) ? _root : Path.Combine(_root, "profiles", Sanitize(profileId));

    public static bool IsDefault(string? profileId) =>
        string.IsNullOrWhiteSpace(profileId) ||
        profileId.Equals(DefaultProfileId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Не бросает: битый или отсутствующий файл даёт новый список с одним профилем.</summary>
    public ProfileRegistry Load()
    {
        ProfileRegistry? registry = null;
        try
        {
            // Повреждённый реестр откладывается, и встаёт его копия: без неё пропали бы имена и
            // пароли всех профилей, а их чаты осталось бы только искать по папкам.
            GuardedJsonFile.Read(_file, IsReadable, out var text);
            if (text is not null)
            {
                registry = JsonSerializer.Deserialize<ProfileRegistry>(text, AppJson.Options);
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

        // Профиль по умолчанию есть всегда, а активный id указывает на существующий профиль.
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
        GuardedJsonFile.Write(_file, JsonSerializer.Serialize(registry, AppJson.Options));
    }

    internal static bool IsReadable(string text) =>
        JsonSerializer.Deserialize<ProfileRegistry>(text, AppJson.Options) is not null;

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

    /// <summary>Заводит профиль со своей пустой папкой данных.</summary>
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
    /// Удаляет профиль с его данными. Профиль по умолчанию удалить нельзя: его папка — общий корень
    /// программы, и вместе с ней ушли бы настройки и все остальные профили.
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
            // Запись из списка убрана в любом случае; занятый файл — не повод для ошибки.
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
