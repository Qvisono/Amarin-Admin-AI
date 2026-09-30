using System.Security.Cryptography;
using System.Text;

namespace Amarin.Core;

/// <summary>
/// Шифрование переписки на диске средствами Windows (DPAPI) — для чатов, их описи и строк
/// журнала аудита.
/// </summary>
/// <remarks>
/// <para>
/// DPAPI на <see cref="DataProtectionScope.CurrentUser"/>, как и у ключей (<see cref="DataProtector"/>):
/// файл читается только под той учётной записью Windows, под которой записан. От того, кто
/// уже вошёл под ней, это не защищает, зато копия папки на флешке, в облаке или в резервной
/// копии чужому человеку ничего не скажет.
/// </para>
/// <para>
/// Зашифрованный файл узнаётся по метке в начале, поэтому оба формата читаются всегда:
/// включение и выключение галочки не делает старые файлы нечитаемыми, а переписываются они
/// в фоне (<see cref="ChatStore.EnsureFormat"/>). Своя добавочная энтропия разводит эти блобы
/// с блобами ключей: файл ключей, подсунутый вместо чата, не расшифруется как чат.
/// </para>
/// </remarks>
public static class AtRestCipher
{
    /// <summary>Метка зашифрованного файла. Не JSON и не BOM — с обычным файлом не спутать.</summary>
    internal static readonly byte[] FileMagic = "AMRN-DPAPI1\n"u8.ToArray();

    /// <summary>Метка зашифрованной строки журнала: строка журнала — JSON, начинается с «{».</summary>
    internal const string LinePrefix = "!amrn-dpapi1:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Amarin Admin AI / chats / v1");

    /// <summary>Файл начинается с метки шифрования.</summary>
    public static bool IsEncrypted(ReadOnlySpan<byte> content) => content.StartsWith(FileMagic);

    /// <summary>Шифрует текст в содержимое файла; null — Windows отказалась шифровать.</summary>
    public static byte[]? EncryptFile(string text)
    {
        if (Protect(Encoding.UTF8.GetBytes(text)) is not { } blob)
        {
            return null;
        }

        var result = new byte[FileMagic.Length + blob.Length];
        FileMagic.CopyTo(result, 0);
        blob.CopyTo(result, FileMagic.Length);
        return result;
    }

    /// <summary>
    /// Текст файла в любом из двух форматов. null — файл зашифрован, но не для этой учётной
    /// записи Windows или повреждён.
    /// </summary>
    public static string? DecryptFile(byte[] content)
    {
        if (!IsEncrypted(content))
        {
            return DecodeText(content);
        }

        return Unprotect(content.AsSpan(FileMagic.Length).ToArray()) is { } plain
            ? Encoding.UTF8.GetString(plain)
            : null;
    }

    /// <summary>Шифрует строку журнала; null — Windows отказалась шифровать.</summary>
    public static string? EncryptLine(string line) =>
        Protect(Encoding.UTF8.GetBytes(line)) is { } blob ? LinePrefix + Convert.ToBase64String(blob) : null;

    /// <summary>Строка журнала в любом из двух форматов; null — не расшифровывается.</summary>
    public static string? DecryptLine(string line)
    {
        if (!line.StartsWith(LinePrefix, StringComparison.Ordinal))
        {
            return line;
        }

        try
        {
            return Unprotect(Convert.FromBase64String(line[LinePrefix.Length..])) is { } plain
                ? Encoding.UTF8.GetString(plain)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Текст из байтов UTF-8, с BOM или без — так его пишет <see cref="AppDataFile"/>.</summary>
    internal static string DecodeText(byte[] content)
    {
        var preamble = Encoding.UTF8.Preamble;
        return content.AsSpan().StartsWith(preamble)
            ? Encoding.UTF8.GetString(content, preamble.Length, content.Length - preamble.Length)
            : Encoding.UTF8.GetString(content);
    }

    /// <remarks>
    /// Вне Windows DPAPI нет (<see cref="PlatformNotSupportedException"/>): там программа не
    /// работает, а тесты видят «не удалось зашифровать» и проверяют запасной путь.
    /// </remarks>
    private static byte[]? Protect(byte[] plain)
    {
        try
        {
            return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static byte[]? Unprotect(byte[] blob)
    {
        try
        {
            return ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
