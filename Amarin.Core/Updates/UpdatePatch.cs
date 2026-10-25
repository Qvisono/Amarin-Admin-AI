using System.Security.Cryptography;

namespace Amarin.Core;

/// <summary>
/// Заплатка обновления: новый exe как перечень кусков установленного и новых байтов.
/// </summary>
/// <remarks>
/// <para>
/// Свой формат, а не MSDelta из Windows: <c>CreateDeltaW</c> отказывает файлам больше 32 МБ
/// (ERROR_INVALID_DATA сразу), а exe весит около ста. Устройство exe тут на руку: это бандл, где
/// каждый файл сжат отдельно, и у соседних выпусков совпадают байт в байт все файлы, кроме наших
/// сборок и составного образа ReadyToRun (1.35.0 → 1.36.0: 5 файлов из 419, 49 МБ из 100). Такие
/// куски и находит сопоставление по скользящему хешу, как у rsync, а сжимать остальное незачем —
/// оно уже сжато.
/// </para>
/// <para>
/// Формат: <c>AMRNPT01</c>, длина исходного файла и длина результата (int64), затем операции:
/// 1 — копировать (смещение и длина в исходном), 2 — вставить (длина и байты), 0 — конец.
/// Сумма результата в заплатке не хранится: правду о файле знает сумма exe в релизе, и сверяет
/// её <see cref="UpdateInstaller"/>.
/// </para>
/// </remarks>
public static class UpdatePatch
{
    /// <summary>Расширение вложения релиза; имя — <c>…-v&lt;новая&gt;-from-v&lt;установленная&gt;-win-x64.patch</c>.</summary>
    public const string Extension = ".patch";

    /// <summary>Размер куска исходного файла, по которому ищутся совпадения.</summary>
    /// <remarks>
    /// Меньше — больше мелких совпадений внутри изменившихся файлов, которых в сжатом бандле почти
    /// нет; больше — длиннее хвосты новых байтов у краёв совпавших файлов. Края добираются
    /// побайтно, поэтому выигрыш от мелкого куска здесь ничтожен.
    /// </remarks>
    internal const int BlockSize = 4096;

    private const byte EndOp = 0;
    private const byte CopyOp = 1;
    private const byte InsertOp = 2;

    /// <summary>Множитель скользящего хеша (по модулю 2^32).</summary>
    private const uint Prime = 16777619;

    private static readonly byte[] Magic = "AMRNPT01"u8.ToArray();

    /// <summary>Заплатку, превращающую <paramref name="source"/> в <paramref name="target"/>, — в поток.</summary>
    public static void Create(ReadOnlySpan<byte> source, ReadOnlySpan<byte> target, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write((long)source.Length);
        writer.Write((long)target.Length);

        // Каждый кусок исходного — по ровной сетке; результат проверяется на каждом смещении.
        // Так находятся и файлы бандла, сдвинутые относительно сетки.
        var blocks = new Dictionary<uint, int>();
        for (var offset = 0; offset + BlockSize <= source.Length; offset += BlockSize)
        {
            blocks.TryAdd(Hash(source.Slice(offset, BlockSize)), offset);
        }

        // Старший множитель окна: его вклад вычитается, когда байт уходит из окна.
        var outgoing = 1u;
        for (var i = 1; i < BlockSize; i++)
        {
            outgoing *= Prime;
        }

        var pending = 0;
        var position = 0;
        var hash = target.Length >= BlockSize ? Hash(target[..BlockSize]) : 0;
        while (position + BlockSize <= target.Length)
        {
            if (blocks.TryGetValue(hash, out var found) &&
                source.Slice(found, BlockSize).SequenceEqual(target.Slice(position, BlockSize)))
            {
                // Совпадение растёт в обе стороны побайтно: файл бандла редко начинается на сетке.
                var back = 0;
                while (position - back > pending && found - back > 0 &&
                       source[found - back - 1] == target[position - back - 1])
                {
                    back++;
                }

                var start = position - back;
                var from = found - back;
                var length = BlockSize + back;
                while (start + length < target.Length && from + length < source.Length &&
                       source[from + length] == target[start + length])
                {
                    length++;
                }

                WriteInsert(writer, target[pending..start]);
                writer.Write(CopyOp);
                writer.Write((long)from);
                writer.Write((long)length);

                position = start + length;
                pending = position;
                if (position + BlockSize <= target.Length)
                {
                    hash = Hash(target.Slice(position, BlockSize));
                }

                continue;
            }

            if (position + BlockSize < target.Length)
            {
                hash = (hash - (target[position] * outgoing)) * Prime + target[position + BlockSize];
            }

            position++;
        }

        WriteInsert(writer, target[pending..]);
        writer.Write(EndOp);
    }

    /// <summary>
    /// Собирает результат по заплатке и возвращает его SHA-256 (шестнадцатеричной строкой в
    /// нижнем регистре).
    /// </summary>
    /// <exception cref="InvalidDataException">Заплатка битая или сделана к другому файлу.</exception>
    public static string Apply(Stream source, Stream patch, Stream target, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(target);

        using var reader = new BinaryReader(patch, System.Text.Encoding.UTF8, leaveOpen: true);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        try
        {
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
            {
                throw new InvalidDataException("Not an update patch.");
            }

            var sourceLength = reader.ReadInt64();
            var targetLength = reader.ReadInt64();
            if (sourceLength != source.Length)
            {
                throw new InvalidDataException("The patch was made for another file.");
            }

            if (targetLength < 0 || targetLength > maxBytes)
            {
                throw new InvalidDataException("The patch result is too large.");
            }

            var written = 0L;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var op = reader.ReadByte();
                if (op == EndOp)
                {
                    break;
                }

                if (op is not (CopyOp or InsertOp))
                {
                    throw new InvalidDataException("Unknown patch operation.");
                }

                var offset = op == CopyOp ? reader.ReadInt64() : 0;
                var length = reader.ReadInt64();
                if (length <= 0 || length > targetLength - written ||
                    (op == CopyOp && (offset < 0 || offset > sourceLength - length)))
                {
                    throw new InvalidDataException("A patch operation is out of range.");
                }

                var from = op == CopyOp ? source : patch;
                if (op == CopyOp)
                {
                    source.Position = offset;
                }

                for (var left = length; left > 0;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = from.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (read <= 0)
                    {
                        throw new InvalidDataException("The patch ends too early.");
                    }

                    target.Write(buffer, 0, read);
                    hasher.AppendData(buffer, 0, read);
                    left -= read;
                }

                written += length;
            }

            if (written != targetLength)
            {
                throw new InvalidDataException("The patch result is incomplete.");
            }
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The patch ends too early.", ex);
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// Заплатка из <paramref name="basePath"/> в <paramref name="targetPath"/> файлом
    /// <paramref name="outputPath"/> — для выпуска (<c>--make-update-patch</c> в release.yml).
    /// </summary>
    /// <remarks>
    /// Готовая заплатка тут же собирается обратно и сверяется с целью по SHA-256: выпуск не должен
    /// выложить заплатку, которая у людей не сойдётся. Делает её и проверяет тот же код, что
    /// потом ставит обновление, — второй реализации формата нет нигде.
    /// </remarks>
    /// <returns>Null — готово; иначе причина отказа.</returns>
    public static string? MakeFile(string basePath, string targetPath, string outputPath)
    {
        var partial = outputPath + ".part";
        try
        {
            var source = ReadShared(basePath);
            var target = ReadShared(targetPath);
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Create(source, target, output);
            }

            string built;
            using (var sourceStream = new MemoryStream(source, writable: false))
            using (var patchStream = File.OpenRead(partial))
            {
                built = Apply(sourceStream, patchStream, Stream.Null, long.MaxValue, CancellationToken.None);
            }

            var expected = Convert.ToHexString(SHA256.HashData(target)).ToLowerInvariant();
            if (built != expected)
            {
                return "The patch does not rebuild the target.";
            }

            File.Move(partial, outputPath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ex.Message;
        }
        finally
        {
            UpdateInstaller.TryDelete(partial);
        }
    }

    /// <summary>
    /// Читает файл, не мешая тем, кто его держит: целью выпуска служит сам работающий exe.
    /// </summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void WriteInsert(BinaryWriter writer, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        writer.Write(InsertOp);
        writer.Write((long)bytes.Length);
        writer.Write(bytes);
    }

    /// <summary>Тот же хеш, что скользящий в <see cref="Create"/>, посчитанный заново.</summary>
    private static uint Hash(ReadOnlySpan<byte> block)
    {
        var hash = 0u;
        foreach (var value in block)
        {
            hash = hash * Prime + value;
        }

        return hash;
    }
}
