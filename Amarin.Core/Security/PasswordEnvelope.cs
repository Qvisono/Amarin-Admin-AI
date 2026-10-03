using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Amarin.Core;

/// <summary>Почему конверт не открылся.</summary>
public enum EnvelopeFailure
{
    /// <summary>Пароль не тот.</summary>
    WrongPassword,

    /// <summary>Файл обрезан, испорчен или переставлен по кускам.</summary>
    Damaged
}

/// <summary>Конверт не открылся: неверный пароль или испорченный файл.</summary>
public sealed class EnvelopeException(EnvelopeFailure failure)
    : Exception(failure == EnvelopeFailure.WrongPassword ? "Wrong password." : "The file is damaged.")
{
    public EnvelopeFailure Failure { get; } = failure;
}

/// <summary>
/// Архив данных под паролем: AES-256-GCM блоками по мегабайту, ключ из пароля — PBKDF2-SHA256.
/// </summary>
/// <remarks>
/// <para>
/// Блоками, а не целиком: архив с перепиской и картинками весит сотни мегабайт, и держать его
/// в памяти ради одного вызова шифра нельзя. Каждый блок несёт свой тег, а в проверяемые данные
/// входят заголовок, номер блока и признак последнего блока — поэтому переставленные блоки,
/// отрезанный хвост и дописанный мусор обнаруживаются, а не читаются как «архив поменьше».
/// </para>
/// <para>
/// В заголовке лежит проверочное значение ключа (HMAC от постоянной строки). Оно не облегчает
/// подбор — тот и так проверяется на первом блоке, — зато отличает неверный пароль от
/// испорченного файла, и человеку можно сказать, что именно не так.
/// </para>
/// <para>
/// Формат: <c>AMRNENC1</c>, версия, число итераций, соль (16), приставка нонса (8),
/// проверочное значение (16); затем блоки: признак последнего (1), длина шифртекста (4),
/// шифртекст, тег (16). Нонс блока — приставка и номер блока: у одного ключа он не
/// повторяется, а ключ на каждый архив свой благодаря случайной соли.
/// </para>
/// </remarks>
public static class PasswordEnvelope
{
    /// <summary>Итерации PBKDF2 — рекомендация OWASP для SHA-256 на 2023 год.</summary>
    public const int Iterations = 600_000;

    public const int BlockSize = 1 << 20;

    private static readonly byte[] Magic = "AMRNENC1"u8.ToArray();
    private const byte Version = 1;
    private const int SaltBytes = 16;
    private const int PrefixBytes = 8;
    private const int CheckBytes = 16;
    private const int TagBytes = 16;

    /// <summary>
    /// Потолок итераций при чтении: файл задаёт их сам, и подсунутый архив с миллиардом
    /// итераций подвешивал бы программу на часы.
    /// </summary>
    private const int MaxIterations = 10_000_000;

    private static readonly int HeaderBytes = Magic.Length + 1 + 4 + SaltBytes + PrefixBytes + CheckBytes;
    private static readonly byte[] CheckLabel = "Amarin envelope password check"u8.ToArray();

    /// <summary>Файл начинается с метки конверта.</summary>
    public static bool IsSealed(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[8];
            return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length &&
                   head.SequenceEqual(Magic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Шифрует поток целиком.</summary>
    /// <param name="iterations">Меньше заводского — только в тестах: иначе каждый шёл бы секунду.</param>
    public static void Seal(Stream plain, Stream output, string password, int iterations = Iterations)
    {
        ArgumentNullException.ThrowIfNull(plain);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var prefix = RandomNumberGenerator.GetBytes(PrefixBytes);
        var key = DeriveKey(password, salt, iterations);
        try
        {
            var header = BuildHeader(iterations, salt, prefix, Check(key));
            output.Write(header);

            using var aes = new AesGcm(key, TagBytes);
            var current = new byte[BlockSize];
            var next = new byte[BlockSize];
            var currentLength = plain.ReadAtLeast(current, BlockSize, throwOnEndOfStream: false);
            var cipher = new byte[BlockSize];
            var tag = new byte[TagBytes];
            Span<byte> length = stackalloc byte[4];

            for (long index = 0; ; index++)
            {
                // Последний ли блок, узнаём, заглянув вперёд: признак входит в проверяемые данные.
                var nextLength = currentLength < BlockSize
                    ? 0
                    : plain.ReadAtLeast(next, BlockSize, throwOnEndOfStream: false);
                var final = nextLength == 0;

                aes.Encrypt(
                    Nonce(prefix, index),
                    current.AsSpan(0, currentLength),
                    cipher.AsSpan(0, currentLength),
                    tag,
                    Aad(header, index, final));

                output.WriteByte(final ? (byte)1 : (byte)0);
                BinaryPrimitives.WriteInt32LittleEndian(length, currentLength);
                output.Write(length);
                output.Write(cipher, 0, currentLength);
                output.Write(tag);

                if (final)
                {
                    return;
                }

                (current, next) = (next, current);
                currentLength = nextLength;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Расшифровывает конверт в поток.</summary>
    /// <exception cref="EnvelopeException">Неверный пароль или испорченный файл.</exception>
    public static void Open(Stream sealedStream, Stream output, string password)
    {
        ArgumentNullException.ThrowIfNull(sealedStream);
        ArgumentNullException.ThrowIfNull(output);

        var header = new byte[HeaderBytes];
        if (sealedStream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length ||
            !header.AsSpan(0, Magic.Length).SequenceEqual(Magic) ||
            header[Magic.Length] != Version)
        {
            throw new EnvelopeException(EnvelopeFailure.Damaged);
        }

        var offset = Magic.Length + 1;
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset, 4));
        offset += 4;
        var salt = header.AsSpan(offset, SaltBytes).ToArray();
        offset += SaltBytes;
        var prefix = header.AsSpan(offset, PrefixBytes).ToArray();
        offset += PrefixBytes;
        var check = header.AsSpan(offset, CheckBytes);

        if (iterations is < 1 or > MaxIterations)
        {
            throw new EnvelopeException(EnvelopeFailure.Damaged);
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new EnvelopeException(EnvelopeFailure.WrongPassword);
        }

        var key = DeriveKey(password, salt, iterations);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(Check(key), check))
            {
                throw new EnvelopeException(EnvelopeFailure.WrongPassword);
            }

            using var aes = new AesGcm(key, TagBytes);
            var cipher = new byte[BlockSize];
            var plain = new byte[BlockSize];
            var tag = new byte[TagBytes];
            Span<byte> lengthBytes = stackalloc byte[4];

            for (long index = 0; ; index++)
            {
                var flag = sealedStream.ReadByte();
                if (flag is not (0 or 1) ||
                    sealedStream.ReadAtLeast(lengthBytes, 4, throwOnEndOfStream: false) != 4)
                {
                    // Сюда приводит и отрезанный хвост: последнего блока так и не было.
                    throw new EnvelopeException(EnvelopeFailure.Damaged);
                }

                var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                if (length is < 0 or > BlockSize ||
                    sealedStream.ReadAtLeast(cipher.AsSpan(0, length), length, throwOnEndOfStream: false) != length ||
                    sealedStream.ReadAtLeast(tag, TagBytes, throwOnEndOfStream: false) != TagBytes)
                {
                    throw new EnvelopeException(EnvelopeFailure.Damaged);
                }

                var final = flag == 1;
                try
                {
                    aes.Decrypt(
                        Nonce(prefix, index),
                        cipher.AsSpan(0, length),
                        tag,
                        plain.AsSpan(0, length),
                        Aad(header, index, final));
                }
                catch (AuthenticationTagMismatchException)
                {
                    throw new EnvelopeException(EnvelopeFailure.Damaged);
                }

                output.Write(plain, 0, length);
                if (!final)
                {
                    continue;
                }

                // После последнего блока ничего быть не должно.
                if (sealedStream.ReadByte() != -1)
                {
                    throw new EnvelopeException(EnvelopeFailure.Damaged);
                }

                return;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Шифрует файл в файл: сначала во временный рядом, затем переименованием.</summary>
    public static void SealFile(string plainPath, string sealedPath, string password, int iterations = Iterations)
    {
        var temporary = sealedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = File.OpenRead(plainPath))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Seal(input, output, password, iterations);
            }

            File.Move(temporary, sealedPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>Расшифровывает файл в файл; при неудаче недописанный результат удаляется.</summary>
    /// <exception cref="EnvelopeException">Неверный пароль или испорченный файл.</exception>
    public static void OpenFile(string sealedPath, string plainPath, string password)
    {
        try
        {
            using var input = File.OpenRead(sealedPath);
            using var output = new FileStream(plainPath, FileMode.Create, FileAccess.Write, FileShare.None);
            Open(input, output, password);
        }
        catch
        {
            TryDelete(plainPath);
            throw;
        }
    }

    private static byte[] BuildHeader(int iterations, byte[] salt, byte[] prefix, byte[] check)
    {
        var header = new byte[HeaderBytes];
        Magic.CopyTo(header, 0);
        var offset = Magic.Length;
        header[offset++] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(offset, 4), iterations);
        offset += 4;
        salt.CopyTo(header, offset);
        offset += SaltBytes;
        prefix.CopyTo(header, offset);
        offset += PrefixBytes;
        check.CopyTo(header, offset);
        return header;
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] Check(byte[] key) => HMACSHA256.HashData(key, CheckLabel)[..CheckBytes];

    private static byte[] Nonce(byte[] prefix, long index)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(PrefixBytes), checked((uint)index));
        return nonce;
    }

    private static byte[] Aad(byte[] header, long index, bool final)
    {
        var aad = new byte[header.Length + 9];
        header.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(header.Length), index);
        aad[^1] = final ? (byte)1 : (byte)0;
        return aad;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Хвост неудачной записи — убрать его любезность, а не обязанность.
        }
    }
}
