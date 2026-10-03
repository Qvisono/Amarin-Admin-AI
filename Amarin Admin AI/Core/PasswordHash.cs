using System.Security.Cryptography;
using System.Text;

namespace Amarin.Core;

/// <summary>
/// Хэш PBKDF2-SHA256 для пароля при запуске и экрана автоблокировки.
///
/// Он закрывает только окно программы — это НЕ шифрование, и пароль ключом не служит никогда.
/// Чаты на диске шифруются только при <see cref="AppSettings.EncryptChats"/>, и тогда DPAPI
/// (<see cref="AtRestCipher"/>), а не этим паролем; настройки остаются открытым JSON. Выдавать
/// пароль за защиту самих данных нельзя.
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
            // Битый profiles.json — считаем «не совпало», а не роняем запуск.
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
