namespace Amarin.Core;

/// <summary>Локальная учётная запись. Всё на этой машине: ни сервера, ни входа в облако.</summary>
public sealed class UserProfile
{
    /// <summary>
    /// Первый профиль — всегда <see cref="ProfileStore.DefaultProfileId"/>, и его данные лежат в
    /// корне программы, как до появления профилей: обновление не переносит и не прячет переписки.
    /// </summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "Гость";

    /// <summary>Имя файла в папке профиля, а не полный путь.</summary>
    public string? AvatarFileName { get; set; }

    /// <summary>Base64 PBKDF2 key; null when no password is set.</summary>
    public string? PasswordHash { get; set; }

    public string? PasswordSalt { get; set; }

    /// <summary>Спрашивать пароль при запуске. Без пароля не имеет смысла.</summary>
    public bool LockOnStartup { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash) && !string.IsNullOrEmpty(PasswordSalt);

    /// <summary>Блокировка действует, только когда пароль действительно задан.</summary>
    public bool IsLocked => LockOnStartup && HasPassword;
}

/// <summary>On-disk shape of profiles.json.</summary>
public sealed class ProfileRegistry
{
    public string ActiveProfileId { get; set; } = "";

    public List<UserProfile> Profiles { get; set; } = [];

    /// <summary>
    /// Профили, кроме открытого, — куда отправить ключ или чат. Открытый исключён не ради порядка:
    /// его данные держат в памяти свои хранилища, и второе хранилище на той же папке затёрло бы их.
    /// </summary>
    public List<UserProfile> Others() => [.. Profiles.Where(profile => profile.Id != ActiveProfileId)];
}
