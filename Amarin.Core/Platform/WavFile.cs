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

    /// <summary>Длительность данных.</summary>
    public static TimeSpan Duration(int pcmBytes) => TimeSpan.FromSeconds(pcmBytes / (double)BytesPerSecond);
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
