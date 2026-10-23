using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Чтение ответа вслух слышит слова, а не разметку; разговорный режим кончает фразу паузой после
/// речи, а не первой тишиной и не шумом комнаты.
/// </summary>
public sealed class SpeechOutputTests
{
    [Fact]
    public void Markdown_turns_into_words_without_code_tables_formulas_or_charts()
    {
        var text = SpeechText.From("""
            ## Итоги

            Продажи **выросли** на [треть](https://example.com/report).

            - первый пункт
            - второй пункт!

            ```powershell
            Get-Process | Stop-Process
            ```

            | A | B |
            |---|---|
            | 1 | 2 |

            Формула $x^2 + 1$ и блок:

            $$E = mc^2$$

            ```chart
            {"type":"bar","labels":["a"],"series":[{"values":[1]}]}
            ```
            """);

        Assert.Equal("Итоги. Продажи выросли на треть. первый пункт. второй пункт! Формула и блок:", text);
    }

    [Fact]
    public void Empty_text_reads_as_nothing() =>
        Assert.Equal("", SpeechText.From("   "));

    private static readonly TimeSpan Chunk = TimeSpan.FromMilliseconds(100);

    private static int ChunksUntilEnd(SilenceDetector detector, IEnumerable<double> levels)
    {
        var count = 0;
        foreach (var level in levels)
        {
            count++;
            if (detector.Add(level, Chunk))
            {
                return count;
            }
        }

        return -1;
    }

    [Fact]
    public void A_phrase_ends_after_a_pause_that_follows_speech()
    {
        var detector = new SilenceDetector();
        var levels = Enumerable.Repeat(0.01, 10)
            .Concat(Enumerable.Repeat(0.3, 8))
            .Concat(Enumerable.Repeat(0.01, 20));

        // 10 тихих, 8 речи, затем 12 кусков паузы по 100 мс — 1,2 с.
        Assert.Equal(10 + 8 + 12, ChunksUntilEnd(detector, levels));
    }

    [Fact]
    public void Silence_without_speech_or_a_click_does_not_end_anything()
    {
        Assert.Equal(-1, ChunksUntilEnd(new SilenceDetector(), Enumerable.Repeat(0.01, 100)));

        var click = Enumerable.Repeat(0.01, 5).Append(0.5).Concat(Enumerable.Repeat(0.01, 50));
        Assert.Equal(-1, ChunksUntilEnd(new SilenceDetector(), click));
    }

    [Fact]
    public void A_short_breath_inside_the_phrase_does_not_cut_it()
    {
        var levels = Enumerable.Repeat(0.3, 6)
            .Concat(Enumerable.Repeat(0.01, 6))
            .Concat(Enumerable.Repeat(0.3, 6))
            .Concat(Enumerable.Repeat(0.01, 20));

        Assert.Equal(6 + 6 + 6 + 12, ChunksUntilEnd(new SilenceDetector(), levels));
    }

    [Fact]
    public void A_noisy_room_raises_the_threshold_but_a_long_phrase_does_not()
    {
        // Вентилятор чуть выше наименьшего порога: шум, а не речь. Речь — полминуты с провалами
        // между словами, как у живого голоса: порог не дорастает до неё и не обрывает фразу.
        var fan = Enumerable.Repeat(0.07, 30);
        var speech = Enumerable.Range(0, 300).Select(i => i % 5 == 2 ? 0.08 : 0.4);
        var pause = Enumerable.Repeat(0.07, 20);

        var detector = new SilenceDetector();
        Assert.Equal(30 + 300 + 12, ChunksUntilEnd(detector, fan.Concat(speech).Concat(pause)));
    }
}
