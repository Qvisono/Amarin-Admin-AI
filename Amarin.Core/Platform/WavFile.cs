using System.Buffers.Binary;

namespace Amarin.Core;

/// <summary>
/// Голосовой ввод (D14): WAV из сырых отсчётов и громкость для индикатора. Без WPF и без звука —
/// проверяется тестами.
/// </summary>
internal static class WavFile
{
    /// <summary>Частота записи: распознавателям её хватает, а файл вдвое меньше, чем при 32 кГц.</summary>
    public const int SampleRate = 16_000;

    /// <summary>Потолок записи: пять минут — это ~9,6 МБ, больше провайдеры могут и не принять.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(5);

    public const int BytesPerSecond = SampleRate * 2;

    /// <summary>Заголовок RIFF/WAVE и данные: 16 бит, моно, <see cref="SampleRate"/>.</summary>
    public static byte[] Build(ReadOnlySpan<byte> pcm) => Build(pcm, SampleRate);

    /// <summary>Заголовок RIFF/WAVE и данные: 16 бит, моно, с этой частотой — для мелодий, которым 16 кГц мало.</summary>
    public static byte[] Build(ReadOnlySpan<byte> pcm, int sampleRate)
    {
        var wav = new byte[44 + pcm.Length];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + pcm.Length);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], pcm.Length);
        pcm.CopyTo(span[44..]);
        return wav;
    }

    /// <summary>
    /// Громкость куска записи от 0 до 1 — среднеквадратичная, с поправкой, чтобы обычная речь
    /// поднимала полосу заметно, а не на пару пикселей.
    /// </summary>
    public static double Level(ReadOnlySpan<byte> pcm)
    {
        var samples = pcm.Length / 2;
        if (samples == 0)
        {
            return 0;
        }

        double sum = 0;
        for (var i = 0; i < samples; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]) / 32768.0;
            sum += sample * sample;
        }

        return Math.Clamp(Math.Sqrt(sum / samples) * 4, 0, 1);
    }

    /// <summary>
    /// Громкость каждой из <paramref name="parts"/> равных долей куска, 0…1 — для точек у кнопки
    /// микрофона: соседние доли звучат по-разному, и точки двигаются вразнобой, как речь.
    /// </summary>
    public static double[] Levels(ReadOnlySpan<byte> pcm, int parts)
    {
        var levels = new double[Math.Max(0, parts)];
        var samples = pcm.Length / 2;
        for (var part = 0; part < levels.Length && samples > 0; part++)
        {
            var from = samples * part / levels.Length;
            var to = samples * (part + 1) / levels.Length;
            levels[part] = Level(pcm[(from * 2)..(to * 2)]);
        }

        return levels;
    }

    /// <summary>Длительность данных.</summary>
    public static TimeSpan Duration(int pcmBytes) => TimeSpan.FromSeconds(pcmBytes / (double)BytesPerSecond);

    /// <summary>
    /// Длинная запись (16 бит, моно) частями не длиннее <paramref name="length"/> — каждая своим WAV
    /// и с моментом начала.
    /// </summary>
    /// <remarks>
    /// Читается потоком, а не целиком: час записи — это больше ста мегабайт, а распознаванию
    /// нужна одна часть за раз. Заголовок разбирается по блокам RIFF, а не по смещению 44:
    /// перекодировщик Windows кладёт перед данными и свои блоки.
    /// </remarks>
    /// <exception cref="InvalidDataException">Не WAV, или не 16 бит моно.</exception>
    public static IEnumerable<(TimeSpan Start, byte[] Wav)> Pieces(Stream wav, TimeSpan length)
    {
        var (sampleRate, dataBytes) = Header(wav);
        var bytesPerSecond = sampleRate * 2;
        var piece = (int)Math.Max(bytesPerSecond, (long)(length.TotalSeconds * bytesPerSecond) & ~1L);
        var buffer = new byte[piece];
        var offset = 0L;
        while (dataBytes < 0 || offset < dataBytes)
        {
            var wanted = dataBytes < 0 ? piece : (int)Math.Min(piece, dataBytes - offset);
            var read = 0;
            while (read < wanted && wav.Read(buffer, read, wanted - read) is var got and > 0)
            {
                read += got;
            }

            read &= ~1;
            if (read == 0)
            {
                yield break;
            }

            yield return (TimeSpan.FromSeconds(offset / (double)bytesPerSecond), Build(buffer.AsSpan(0, read), sampleRate));
            offset += read;
        }
    }

    /// <summary>Частота и длина данных; поток после вызова стоит на начале данных. Длина −1 — до конца файла.</summary>
    private static (int SampleRate, long DataBytes) Header(Stream wav)
    {
        Span<byte> head = stackalloc byte[12];
        wav.ReadExactly(head);
        if (!head[..4].SequenceEqual("RIFF"u8) || !head[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("not a WAV file");
        }

        int? sampleRate = null;
        Span<byte> chunk = stackalloc byte[8];
        while (true)
        {
            wav.ReadExactly(chunk);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (chunk[..4].SequenceEqual("data"u8))
            {
                return (sampleRate ?? throw new InvalidDataException("no format block"), size is 0 or uint.MaxValue ? -1 : size);
            }

            var body = new byte[size + (size & 1)];
            wav.ReadExactly(body);
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2));
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(14));
                if (channels != 1 || bits != 16)
                {
                    throw new InvalidDataException("not 16-bit mono");
                }

                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4));
            }
        }
    }
}

/// <summary>Чем распознавать голос (D14). Пишется в settings.json именем.</summary>
public enum VoiceEngine
{
    /// <summary>На ПК, если есть распознаватель языка, иначе в облаке.</summary>
    Auto,

    /// <summary>Только на ПК: запись никуда не уходит.</summary>
    Local,

    /// <summary>Только в облаке, моделью из настроек.</summary>
    Cloud
}
