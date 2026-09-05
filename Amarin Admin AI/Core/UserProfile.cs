namespace Amarin.Core;

/// <summary>A local account. Everything is on this machine; there is no server or sign-in.</summary>
public sealed class UserProfile
{
    /// <summary>
    /// The first profile is always <see cref="ProfileStore.DefaultProfileId"/> and its data
    /// lives in the app root that shipped before profiles existed, so upgrading never moves
    /// or hides an existing conversation.
    /// </summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "Гость";

    /// <summary>File name inside the profile's own folder, not a full path.</summary>
    public string? AvatarFileName { get; set; }

    /// <summary>Base64 PBKDF2 key; null when no password is set.</summary>
    public string? PasswordHash { get; set; }

    public string? PasswordSalt { get; set; }

    /// <summary>Ask for the password on launch. Meaningless without a password set.</summary>
    public bool LockOnStartup { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash) && !string.IsNullOrEmpty(PasswordSalt);

    /// <summary>Locking only takes effect once a password actually exists.</summary>
    public bool IsLocked => LockOnStartup && HasPassword;
}

/// <summary>On-disk shape of profiles.json.</summary>
public sealed class ProfileRegistry
{
    public string ActiveProfileId { get; set; } = "";

    public List<UserProfile> Profiles { get; set; } = [];
}
