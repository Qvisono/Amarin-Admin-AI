namespace Amarin.Core;

/// <summary>
/// Конец фразы в записи с микрофона — для разговорного режима: человек сказал и замолчал.
/// </summary>
/// <remarks>
/// <para>
/// Порог тишины подстраивается под комнату: шум — самый тихий кусок за последние
/// <see cref="NoiseWindow"/>. Неподвижный порог в тихой комнате ловил бы дыхание как речь, а у
/// работающего вентилятора не дождался бы тишины никогда. Окно длиннее любой фразы без
/// передышки: в живой речи между словами всегда есть провалы, и шум не дорастает до голоса.
/// </para>
/// <para>
/// Фраза засчитывается после хотя бы <see cref="MinSpeech"/> речи — кашель или щелчок мыши не
/// отправляют пустой запрос, — а кончается паузой в <see cref="Pause"/>: короче — и запись
/// обрывалась бы на запятой.
/// </para>
/// </remarks>
internal sealed class SilenceDetector
{
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(1.2);

    public static readonly TimeSpan MinSpeech = TimeSpan.FromSeconds(0.4);

    public static readonly TimeSpan NoiseWindow = TimeSpan.FromSeconds(8);

    /// <summary>Ниже этого уровня не бывает речи, как бы тихо ни было в комнате.</summary>
    private const double MinThreshold = 0.06;

    private readonly Queue<(double Level, TimeSpan Length)> _recent = new();
    private TimeSpan _window;
    private TimeSpan _speech;
    private TimeSpan _quiet;

    /// <summary>Речь уже была: пауза после неё — конец фразы.</summary>
    public bool Heard => _speech >= MinSpeech;

    /// <summary>Учитывает кусок записи.</summary>
    /// <param name="level">Громкость куска (<see cref="WavFile.Level(ReadOnlySpan{byte})"/>).</param>
    /// <returns>Фраза закончилась: была речь, и после неё пауза.</returns>
    public bool Add(double level, TimeSpan length)
    {
        _recent.Enqueue((level, length));
        _window += length;
        while (_window - _recent.Peek().Length >= NoiseWindow)
        {
            _window -= _recent.Dequeue().Length;
        }

        var threshold = Math.Max(MinThreshold, _recent.Min(item => item.Level) * 2.5);
        if (level >= threshold)
        {
            _speech += length;
            _quiet = TimeSpan.Zero;
            return false;
        }

        _quiet += length;
        return Heard && _quiet >= Pause;
    }
}
