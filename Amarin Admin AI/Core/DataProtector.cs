using System.Security.Cryptography;
using System.Text;

namespace Amarin.Core;

/// <summary>
/// Шифрование строк средствами Windows — единственное место, где программа прячет секрет
/// на диске.
/// </summary>
/// <remarks>
/// DPAPI на <see cref="DataProtectionScope.CurrentUser"/>: расшифровать сможет только та
/// учётная запись Windows, под которой шифровали. Своего пароля программа не спрашивает —
/// он был бы ещё одним секретом, который человеку пришлось бы где-то держать, а от того, кто
/// уже работает под его учётной записью, такой пароль всё равно не защищает.
/// <para>
/// Добавочная энтропия — не секрет: она просто разводит наши блобы с чужими, чтобы файл,
/// подсунутый из другой программы, не расшифровался здесь как ключ.
/// </para>
/// </remarks>
internal static class DataProtector
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("Amarin Admin AI / venice keys / v1");

    /// <summary>Возвращает base64-блоб либо <c>null</c>, если Windows отказалась шифровать.</summary>
    public static string? Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Разворачивает блоб обратно. <c>null</c> — блоб не наш: папку профиля перенесли на другую
    /// машину или под другого пользователя. Это обычный ответ, а не авария: запись останется
    /// в списке с пометкой «не читается», а человек введёт ключ заново.
    /// </summary>
    public static string? Unprotect(string? blob)
    {
        if (string.IsNullOrWhiteSpace(blob))
        {
            return null;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception exception) when (
            exception is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
