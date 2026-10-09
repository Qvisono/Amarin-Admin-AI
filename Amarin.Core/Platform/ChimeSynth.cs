using System.Buffers.Binary;

namespace Amarin.Core;

/// <summary>Какая мелодия.</summary>
public enum ChimeTune
{
    /// <summary>Напоминание: три ноты вверх — приветливо, но заметно.</summary>
    Reminder,

    /// <summary>Задача выполнена: две ноты, завершённая каденция.</summary>
    Done,

    /// <summary>Задача не удалась: две ноты вниз, мягко, без тревоги.</summary>
    Failed
}

/// <summary>
/// Мелодии отложенных задач — синтез в коде, без файлов: тихий колокольчик из нескольких призвуков.
/// </summary>
/// <remarks>
/// <para>
/// Мягкость — от трёх вещей. Ноты — мажорное трезвучие в средней октаве, без резких интервалов и
/// высоких частот, от которых звенит в ушах. Тембр — основной тон с тихими второй и третьей
/// гармониками и чуть расстроенным вторым голосом: тепло, как у музыкальной шкатулки, а не писк.
/// Огибающая — атака 12 мс (без щелчка в начале), плавное затухание и обнуление к концу (без
/// щелчка в конце). Пик — около −14 дБ полной шкалы: слышно, но не громче системного звука.
/// </para>
/// <para>
/// Байты собираются при первом звуке и кэшируются у проигрывателя: синтез ~0,1 с на мелодию, и
/// запуску программы он не нужен.
/// </para>
/// </remarks>
internal static class ChimeSynth
{
    public const int SampleRate = 44_100;

    /// <summary>Пиковая громкость: −14 дБ полной шкалы.</summary>
    public const double Peak = 0.2;

    private const double Attack = 0.012;
    private const double Fade = 0.08;

    /// <summary>Мелодия готовым WAV — 16 бит, моно, 44,1 кГц.</summary>
    public static byte[] Render(ChimeTune tune)
    {
        var (notes, length) = Score(tune);
        var samples = new double[(int)(length * SampleRate)];
        foreach (var (frequency, start, decay) in notes)
        {
            Strike(samples, frequency, start, decay);
        }

        Normalize(samples);
        FadeOut(samples);

        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)Math.Round(samples[i] * short.MaxValue));
        }

        return WavFile.Build(pcm, SampleRate);
    }

    /// <summary>Ноты мелодии: частота, начало и время затухания, — и длина целиком.</summary>
    private static ((double Frequency, double Start, double Decay)[] Notes, double Length) Score(ChimeTune tune) => tune switch
    {
        // До, ми, соль пятой октавы.
        ChimeTune.Reminder => ([(523.25, 0.0, 0.42), (659.25, 0.17, 0.42), (783.99, 0.34, 0.6)], 1.9),

        // Соль четвёртой — до пятой: кварта вверх, «готово».
        ChimeTune.Done => ([(392.00, 0.0, 0.38), (523.25, 0.19, 0.55)], 1.5),

        // Ми — до: большая терция вниз, мягко.
        _ => ([(659.25, 0.0, 0.38), (523.25, 0.21, 0.5)], 1.5)
    };

    /// <summary>Удар колокольчика: основной тон, тихие гармоники и второй голос, расстроенный на пару центов.</summary>
    private static void Strike(double[] samples, double frequency, double start, double decay)
    {
        var from = (int)(start * SampleRate);
        var detuned = frequency * Math.Pow(2, 2.0 / 1200);
        for (var i = from; i < samples.Length; i++)
        {
            var t = (double)(i - from) / SampleRate;
            var attack = t < Attack ? 0.5 - 0.5 * Math.Cos(Math.PI * t / Attack) : 1.0;
            var envelope = attack * Math.Exp(-t / decay);
            if (envelope < 1e-5 && t > Attack)
            {
                break;
            }

            var tone = Math.Sin(2 * Math.PI * frequency * t)
                       + 0.22 * Math.Sin(2 * Math.PI * 2 * frequency * t) * Math.Exp(-t / (decay * 0.5))
                       + 0.07 * Math.Sin(2 * Math.PI * 3 * frequency * t) * Math.Exp(-t / (decay * 0.3))
                       + 0.3 * Math.Sin(2 * Math.PI * detuned * t);
            samples[i] += envelope * tone;
        }
    }

    private static void Normalize(double[] samples)
    {
        var max = samples.Max(Math.Abs);
        if (max <= 0)
        {
            return;
        }

        var scale = Peak / max;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] *= scale;
        }
    }

    /// <summary>Последние <see cref="Fade"/> секунды — к нулю: обрыв звука на ненулевом отсчёте щёлкает.</summary>
    private static void FadeOut(double[] samples)
    {
        var length = (int)(Fade * SampleRate);
        for (var i = 0; i < length && i < samples.Length; i++)
        {
            samples[^(i + 1)] *= (double)i / length;
        }
    }
}
