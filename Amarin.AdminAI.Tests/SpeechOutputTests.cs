using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Чтение ответа вслух слышит слова, а не разметку.</summary>
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
}
