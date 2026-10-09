using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Amarin.Core;

/// <summary>
/// Правка открытого .docx: замена текста с сохранением оформления, вставка, замена и удаление
/// абзацев по номерам из чтения.
/// </summary>
/// <remarks>
/// <para>
/// Номера абзацев — те, что модель видела в <c>read_file</c>, и все правки одного вызова
/// ссылаются на них, а не на то, что получится после соседней правки: номера сопоставляются
/// элементам один раз, при открытии. Иначе «вставь после 5 и удали 9» удаляло бы не тот абзац.
/// </para>
/// <para>
/// Замена ищет текст внутри абзаца, а не внутри одного куска: Word режет предложение на куски по
/// любому поводу (проверка орфографии, история правок), и фраза «итог за квартал» запросто лежит
/// в трёх кусках. Вставленный текст наследует оформление того куска, где начиналось совпадение.
/// </para>
/// </remarks>
internal sealed partial class WordEditor : IDisposable
{
    private readonly WordprocessingDocument _document;
    private readonly MainDocumentPart _main;
    private readonly W.Body _body;
    private readonly List<OpenXmlElement> _units;
    private readonly WordBuildReport _report = new();
    private WordBuilder? _builder;

    private WordEditor(WordprocessingDocument document)
    {
        _document = document;
        _main = document.MainDocumentPart ?? throw new DocumentException("The Word file has no document body.");
        _body = _main.Document?.Body ?? throw new DocumentException("The Word file has no document body.");
        _units = [.. WordReader.Units(_body)];
    }

    /// <summary>Что при вставке пришлось пропустить (картинки, которых нет).</summary>
    public IReadOnlyList<string> Skipped => _report.Skipped;

    /// <param name="images">Откуда брать картинки вставляемого Markdown.</param>
    public static WordEditor Open(string path, Func<string, byte[]?> images)
    {
        var editor = new WordEditor(WordReader.Open(path, editable: true));
        editor.Images = images;
        return editor;
    }

    private Func<string, byte[]?> Images { get; set; } = _ => null;

    private WordBuilder Builder
    {
        get
        {
            if (_builder is null)
            {
                var styles = WordStyles.Ensure(_main);
                _builder = new WordBuilder(_main, Images, _report, styles);
            }

            return _builder;
        }
    }

    /// <summary>Заменяет текст в абзацах документа (и в ячейках таблиц).</summary>
    /// <returns>Номера абзацев, где была замена; сколько совпадений нашлось всего.</returns>
    public (int Count, IReadOnlyList<int> Units) Replace(string find, string replacement, bool all)
    {
        if (string.IsNullOrEmpty(find))
        {
            throw new DocumentException("The text to find is empty.");
        }

        // Чтение показывает абзацы Markdown-ом (номер, «#», «**»), а в документе этих знаков нет.
        // Фрагмент, скопированный из чтения как есть, ищется второй раз без них; разметка из замены
        // снимается всегда — вид берётся у заменяемого текста, а не из звёздочек.
        replacement = WithoutReadMarkup(replacement);
        var matches = Matches(find);
        if (matches.Count == 0 && WithoutReadMarkup(find) is { Length: > 0 } plain && plain != find)
        {
            find = plain;
            matches = Matches(find);
        }

        if (matches.Count == 0)
        {
            throw new DocumentException(
                "The text was not found in any paragraph. Copy it from read_file without the [number] prefix and Markdown marks; a match cannot span two paragraphs.");
        }

        if (matches.Count > 1 && !all)
        {
            throw new DocumentException(
                $"The text occurs {matches.Count} times. Give a longer fragment that occurs once, or set all=true to replace every occurrence.");
        }

        // С конца абзаца к началу: замена не сдвигает ещё не обработанные совпадения.
        foreach (var group in matches.GroupBy(match => match.Paragraph))
        {
            foreach (var (paragraph, index) in group.OrderByDescending(match => match.Index))
            {
                ReplaceAt(paragraph, index, find.Length, replacement);
            }
        }

        var touched = matches.Select(match => UnitOf(match.Paragraph)).Where(number => number > 0).Distinct().Order().ToList();
        return (matches.Count, touched);
    }

    private List<(W.Paragraph Paragraph, int Index)> Matches(string find)
    {
        var matches = new List<(W.Paragraph Paragraph, int Index)>();
        foreach (var paragraph in _body.Descendants<W.Paragraph>())
        {
            var text = Texts(paragraph).Text;
            for (var at = text.IndexOf(find, StringComparison.Ordinal); at >= 0; at = text.IndexOf(find, at + find.Length, StringComparison.Ordinal))
            {
                matches.Add((paragraph, at));
            }
        }

        return matches;
    }

    /// <summary>
    /// Текст без разметки, которой чтение показывает абзац: номер <c>[12] </c>, заголовок, маркер
    /// списка, цитата, жирный и курсив, код и ссылки.
    /// </summary>
    internal static string WithoutReadMarkup(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(line => BlockMarkup().Replace(line, ""));
        var plain = string.Join("\n", lines);
        plain = LinkMarkup().Replace(plain, "${text}");
        plain = StrongMarkup().Replace(plain, "${text}");
        plain = EmphasisMarkup().Replace(plain, "${text}");
        return CodeMarkup().Replace(plain, "${text}");
    }

    [GeneratedRegex(@"^\s*(\[\d+\]\s+)?(#{1,6}\s+|>\s+|[-*+]\s+|\d+[.)]\s+)?")]
    private static partial Regex BlockMarkup();

    [GeneratedRegex(@"\[(?<text>[^\]\n]*)\]\([^)\n]*\)")]
    private static partial Regex LinkMarkup();

    [GeneratedRegex(@"(\*\*|__)(?<text>[^\n]+?)\1")]
    private static partial Regex StrongMarkup();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])([*_])(?<text>[^\s*_][^\n]*?)\1(?![\p{L}\p{N}])")]
    private static partial Regex EmphasisMarkup();

    [GeneratedRegex(@"`(?<text>[^`\n]+)`")]
    private static partial Regex CodeMarkup();

    public void Insert(int unit, bool before, string markdown)
    {
        var elements = Builder.Build(MarkdownBlocks.Parse(markdown));
        if (elements.Count == 0)
        {
            throw new DocumentException("Nothing to insert: the Markdown is empty.");
        }

        if (unit == 0 && before)
        {
            InsertAtStart(elements);
            return;
        }

        var anchor = Unit(unit);
        if (before)
        {
            foreach (var element in elements)
            {
                anchor.InsertBeforeSelf(element);
            }
        }
        else
        {
            var after = anchor;
            foreach (var element in elements)
            {
                after.InsertAfterSelf(element);
                after = element;
            }
        }
    }

    public void Set(int unit, string markdown)
    {
        var anchor = Unit(unit);
        var elements = Builder.Build(MarkdownBlocks.Parse(markdown));
        foreach (var element in elements)
        {
            anchor.InsertBeforeSelf(element);
        }

        anchor.Remove();
    }

    public void Delete(int from, int to)
    {
        if (to < from)
        {
            (from, to) = (to, from);
        }

        var doomed = Enumerable.Range(from, to - from + 1).Select(Unit).ToList();
        foreach (var element in doomed)
        {
            element.Remove();
        }

        // Тело без единого абзаца Word считает повреждённым.
        if (!_body.Elements<W.Paragraph>().Any() && !_body.Elements<W.Table>().Any())
        {
            _body.InsertAt(new W.Paragraph(), 0);
        }
    }

    public void Append(string markdown)
    {
        var elements = Builder.Build(MarkdownBlocks.Parse(markdown));
        var section = _body.Elements<W.SectionProperties>().LastOrDefault();
        foreach (var element in elements)
        {
            if (section is null)
            {
                _body.Append(element);
            }
            else
            {
                section.InsertBeforeSelf(element);
            }
        }
    }

    public void Save() => _main.Document?.Save();

    public void Dispose() => _document.Dispose();

    /// <summary>Единица по номеру из чтения; удалённая этим же вызовом — отказ, а не тихая правка не того.</summary>
    private OpenXmlElement Unit(int number)
    {
        if (number < 1 || number > _units.Count)
        {
            throw new DocumentException($"There is no paragraph {number}: the document has {_units.Count}. Read it again to get current numbers.");
        }

        var element = _units[number - 1];
        return element.Parent is null
            ? throw new DocumentException($"Paragraph {number} was already removed by an earlier operation in this call.")
            : element;
    }

    private int UnitOf(W.Paragraph paragraph)
    {
        OpenXmlElement? node = paragraph;
        while (node is not null && node.Parent is not W.Body)
        {
            node = node.Parent;
        }

        return node is null ? 0 : _units.IndexOf(node) + 1;
    }

    private void InsertAtStart(List<OpenXmlElement> elements)
    {
        var first = _body.FirstChild;
        foreach (var element in elements)
        {
            if (first is null)
            {
                _body.Append(element);
            }
            else
            {
                first.InsertBeforeSelf(element);
            }
        }
    }

    /// <summary>Куски текста абзаца по порядку и их общий текст — для поиска поверх границ кусков.</summary>
    private static (string Text, List<W.Text> Parts) Texts(W.Paragraph paragraph)
    {
        var parts = paragraph.Descendants<W.Text>().Where(text => !text.Ancestors<W.DeletedRun>().Any()).ToList();
        var text = new StringBuilder();
        foreach (var part in parts)
        {
            text.Append(part.Text);
        }

        return (text.ToString(), parts);
    }

    /// <summary>Заменяет совпадение: новый текст — в куске, где оно начиналось, остальное вырезается.</summary>
    private static void ReplaceAt(W.Paragraph paragraph, int index, int length, string replacement)
    {
        var (_, parts) = Texts(paragraph);
        var offset = 0;
        var remaining = length;
        var placed = false;
        foreach (var part in parts)
        {
            var text = part.Text;
            var start = offset;
            offset += text.Length;
            if (offset <= index || remaining == 0)
            {
                continue;
            }

            var from = Math.Max(0, index - start);
            var take = Math.Min(text.Length - from, remaining);
            var head = text[..from];
            var tail = text[(from + take)..];
            part.Text = placed ? head + tail : head + replacement + tail;
            part.Space = SpaceProcessingModeValues.Preserve;
            placed = true;
            remaining -= take;
            index += take;
        }
    }
}
