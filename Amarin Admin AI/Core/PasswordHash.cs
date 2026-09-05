using System.Security.Cryptography;
using System.Text;

namespace Amarin.Core;

/// <summary>
/// PBKDF2-SHA256 hashing for the local startup lock.
///
/// This gates access to the app window only — it is NOT encryption. The chats and settings on
/// disk stay plain JSON that anyone with file access can read. Do not present it as protection
/// of the data itself.
/// </summary>
internal static class PasswordHash
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    public static (string Hash, string Salt) Create(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Derive(password, salt);
        return (Convert.ToBase64String(key), Convert.ToBase64String(salt));
    }

    public static bool Verify(string? password, string? hash, string? salt)
    {
        if (string.IsNullOrEmpty(password) ||
            string.IsNullOrEmpty(hash) ||
            string.IsNullOrEmpty(salt))
        {
            return false;
        }

        try
        {
            var expected = Convert.FromBase64String(hash);
            var actual = Derive(password, Convert.FromBase64String(salt));
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            // Corrupted profiles.json — treat as "no match" rather than crashing the launch.
            return false;
        }
    }

    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
}
