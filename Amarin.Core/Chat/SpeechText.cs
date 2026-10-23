using System.Text;
using System.Text.RegularExpressions;

namespace Amarin.Core;

/// <summary>Текст ответа для чтения вслух: то, что человек услышит, без разметки и того, что не читается.</summary>
/// <remarks>
/// Синтезатор читает строку как есть: «решётка решётка», адрес ссылки по буквам и блок кода строка за
/// строкой делают ответ неслушаемым. Поэтому из Markdown остаются слова: заголовки, абзацы, пункты
/// списков и цитаты, у ссылки — подпись. Код, таблицы, формулы, картинки и диаграммы пропускаются:
/// их смотрят глазами, а не слушают.
/// </remarks>
internal static partial class SpeechText
{
    public static string From(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "";
        }

        var text = new StringBuilder();
        Append(text, MarkdownBlocks.Parse(DisplayMath().Replace(markdown, " ")));
        return Spaces().Replace(InlineMath().Replace(text.ToString(), " "), " ").Trim();
    }

    private static void Append(StringBuilder text, IEnumerable<DocBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case DocHeading heading:
                    Sentence(text, MarkdownBlocks.PlainText(heading.Spans));
                    break;
                case DocText paragraph:
                    Sentence(text, MarkdownBlocks.PlainText(paragraph.Spans));
                    break;
                case DocList list:
                    foreach (var item in list.Items)
                    {
                        Append(text, item);
                    }

                    break;
                case DocQuote quote:
                    Append(text, quote.Blocks);
                    break;
            }
        }
    }

    /// <summary>Абзац заканчивается точкой, если своей нет: иначе синтезатор склеивает пункты без паузы.</summary>
    private static void Sentence(StringBuilder text, string line)
    {
        var clean = line.Replace('\n', ' ').Trim();
        if (clean.Length == 0)
        {
            return;
        }

        text.Append(clean);
        if (!".!?…:;".Contains(clean[^1], StringComparison.Ordinal))
        {
            text.Append('.');
        }

        text.Append(' ');
    }

    [GeneratedRegex(@"\$\$[\s\S]*?\$\$")]
    private static partial Regex DisplayMath();

    [GeneratedRegex(@"\$[^$\n]+\$")]
    private static partial Regex InlineMath();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
