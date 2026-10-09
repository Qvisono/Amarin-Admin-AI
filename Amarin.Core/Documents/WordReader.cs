using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Amarin.Core;

/// <summary>
/// Word (.docx) — абзацами и таблицами в Markdown: заголовки, списки, жирный и курсив, ссылки,
/// таблицы строками с разделителями.
/// </summary>
/// <remarks>
/// Markdown, а не голый текст: модель понимает по нему, где заголовок, где пункт списка и где
/// таблица, — а голый текст, который провайдер прежде доставал из файла сам, сливал всё это в
/// одну простыню. Единица — элемент верхнего уровня тела документа (<see cref="Units"/>); правка
/// (<c>WordEditor</c>) считает их тем же методом.
/// </remarks>
internal static class WordReader
{
    public static DocumentContent Read(DocumentSource source, DocumentWindow window)
    {
        using var stream = source.OpenRead();
        using var document = Open(stream);
        var body = document.MainDocumentPart?.Document?.Body
                   ?? throw new DocumentException("The Word file has no document body.");
        var context = new WordContext(document);

        var units = new List<DocumentUnit>();
        var paragraphs = 0;
        var tables = 0;
        var number = 0;
        foreach (var element in Units(body))
        {
            number++;
            switch (element)
            {
                case W.Table table:
                    tables++;
                    units.Add(new DocumentUnit(number, TableMarkdown(table, context)));
                    break;
                case W.Paragraph paragraph:
                    paragraphs++;
                    units.Add(new DocumentUnit(number, ParagraphMarkdown(paragraph, context)));
                    break;
                default:
                    units.Add(new DocumentUnit(number, string.Join("\n",
                        element.Descendants<W.Paragraph>().Select(p => ParagraphMarkdown(p, context)).Where(t => t.Length > 0))));
                    break;
            }
        }

        var pages = document.ExtendedFilePropertiesPart?.Properties?.Pages?.Text;
        var summary = $"{paragraphs} paragraphs, {tables} tables" + (int.TryParse(pages, out var count) && count > 0 ? $", ~{count} pages" : "");

        // Пустой абзац номер получает, но не показывается и бюджета не ест: в документах их
        // бывает треть, и модели они ничего не говорят.
        return new DocumentContent
        {
            Kind = DocumentKind.Word,
            Unit = "paragraph",
            Sections = [UnitBudget.Slice(units, window, unit => unit.Text.Length + 8, unit => unit.Text.Length == 0)],
            Summary = summary
        };
    }

    /// <summary>Открывает .docx; битый или зашифрованный файл — понятным отказом, а не исключением пакета.</summary>
    public static WordprocessingDocument Open(string path, bool editable) =>
        Guard(() => WordprocessingDocument.Open(path, editable));

    /// <summary>Открывает .docx из потока только для чтения — так читается вложение из памяти.</summary>
    public static WordprocessingDocument Open(Stream stream) =>
        Guard(() => WordprocessingDocument.Open(stream, false));

    private static WordprocessingDocument Guard(Func<WordprocessingDocument> open)
    {
        try
        {
            return open();
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException || (ex is IOException && ex is not FileNotFoundException))
        {
            throw new DocumentException(
                $"Word could not be read: {ex.Message}. It may be password-protected, damaged or an old .doc renamed to .docx.");
        }
    }

    /// <summary>
    /// Единицы документа — абзацы, таблицы и блоки элементов управления верхнего уровня тела, по
    /// порядку. Закладки, разрывы разделов и прочая служебная разметка номеров не получают.
    /// </summary>
    public static IEnumerable<OpenXmlElement> Units(W.Body body) =>
        body.ChildElements.Where(element => element is W.Paragraph or W.Table or W.SdtBlock);

    /// <summary>Текст абзаца без разметки — для поиска при правке и для подписей.</summary>
    public static string PlainText(W.Paragraph paragraph)
    {
        var text = new StringBuilder();
        foreach (var element in paragraph.Descendants())
        {
            AppendPlain(text, element);
        }

        return text.ToString();
    }

    private static void AppendPlain(StringBuilder text, OpenXmlElement element)
    {
        switch (element)
        {
            case W.Text t when t.Parent is not W.DeletedRun:
                text.Append(t.Text);
                break;
            case W.TabChar:
                text.Append('\t');
                break;
            case W.Break lineBreak:
                // Разрыв страницы или колонки — вёрстка, а не перевод строки в тексте.
                if (lineBreak.Type is null ||
                    (lineBreak.Type.Value != W.BreakValues.Page && lineBreak.Type.Value != W.BreakValues.Column))
                {
                    text.Append('\n');
                }

                break;
            case W.CarriageReturn:
                text.Append('\n');
                break;
            case W.NoBreakHyphen:
                text.Append('-');
                break;
        }
    }

    internal static string ParagraphMarkdown(W.Paragraph paragraph, WordContext context)
    {
        var body = InlineMarkdown(paragraph, context).Trim();
        if (body.Length == 0)
        {
            return paragraph.Descendants<W.Drawing>().Any() ? "[image]" : "";
        }

        var properties = paragraph.ParagraphProperties;
        var style = properties?.ParagraphStyleId?.Val?.Value;
        var level = context.HeadingLevel(style, properties?.OutlineLevel?.Val?.Value);
        if (level > 0)
        {
            return new string('#', Math.Min(level, 6)) + " " + body;
        }

        if (context.ListKind(paragraph) is { } list)
        {
            var indent = new string(' ', Math.Min(list.Level, 6) * 2);
            return indent + (list.Ordered ? "1. " : "- ") + body;
        }

        return body;
    }

    /// <summary>Текст абзаца в Markdown: соседние куски с одинаковым начертанием сливаются.</summary>
    private static string InlineMarkdown(W.Paragraph paragraph, WordContext context)
    {
        var segments = new List<(string Text, bool Bold, bool Italic, string? Link)>();
        foreach (var run in paragraph.Descendants<W.Run>())
        {
            if (run.Ancestors<W.DeletedRun>().Any())
            {
                continue;
            }

            var text = new StringBuilder();
            foreach (var child in run.ChildElements)
            {
                AppendPlain(text, child);
            }

            if (run.Descendants<W.Drawing>().Any())
            {
                text.Append(" [image] ");
            }

            if (text.Length == 0)
            {
                continue;
            }

            var properties = run.RunProperties;
            var bold = IsOn(properties?.Bold);
            var italic = IsOn(properties?.Italic);
            var link = run.Ancestors<W.Hyperlink>().FirstOrDefault() is { Id.Value: { } id } ? context.Link(id) : null;
            if (segments.Count > 0 && segments[^1] is var last &&
                last.Bold == bold && last.Italic == italic && last.Link == link)
            {
                segments[^1] = (last.Text + text, bold, italic, link);
            }
            else
            {
                segments.Add((text.ToString(), bold, italic, link));
            }
        }

        var markdown = new StringBuilder();
        foreach (var (text, bold, italic, link) in segments)
        {
            var core = text;
            if (string.IsNullOrWhiteSpace(core))
            {
                markdown.Append(core);
                continue;
            }

            // Пробелы по краям остаются снаружи разметки: «** жирное**» Markdown не узнал бы.
            var lead = core[..(core.Length - core.TrimStart().Length)];
            var trail = core[core.TrimEnd().Length..];
            core = core.Trim();
            if (bold && italic)
            {
                core = "***" + core + "***";
            }
            else if (bold)
            {
                core = "**" + core + "**";
            }
            else if (italic)
            {
                core = "*" + core + "*";
            }

            if (link is not null)
            {
                core = "[" + core + "](" + link + ")";
            }

            markdown.Append(lead).Append(core).Append(trail);
        }

        return markdown.ToString();
    }

    private static bool IsOn(W.OnOffType? value) => value is not null && (value.Val is null || value.Val.Value);

    internal static string TableMarkdown(W.Table table, WordContext context)
    {
        var rows = table.Elements<W.TableRow>()
            .Select(row => row.Elements<W.TableCell>()
                .Select(cell => string.Join(" ", cell.Elements<W.Paragraph>()
                        .Select(p => InlineMarkdown(p, context).Trim())
                        .Where(t => t.Length > 0))
                    .Replace("|", "\\|", StringComparison.Ordinal)
                    .Replace("\n", " ", StringComparison.Ordinal))
                .ToList())
            .Where(cells => cells.Count > 0)
            .ToList();
        if (rows.Count == 0)
        {
            return "[empty table]";
        }

        var width = rows.Max(cells => cells.Count);
        var text = new StringBuilder();
        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i];
            text.Append("| ").Append(string.Join(" | ", cells.Concat(Enumerable.Repeat("", width - cells.Count)))).Append(" |\n");
            if (i == 0)
            {
                text.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", width))).Append('\n');
            }
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Стили, нумерация и ссылки документа: читаются один раз на весь документ.</summary>
    internal sealed class WordContext
    {
        private readonly Dictionary<string, int> _headingByStyle = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);
        private readonly Dictionary<(int NumId, int Level), bool> _ordered = [];
        private readonly MainDocumentPart? _main;

        public WordContext(WordprocessingDocument document)
        {
            _main = document.MainDocumentPart;
            if (_main?.StyleDefinitionsPart?.Styles is { } styles)
            {
                foreach (var style in styles.Elements<W.Style>())
                {
                    if (style.StyleId?.Value is not { } id)
                    {
                        continue;
                    }

                    var name = style.StyleName?.Val?.Value ?? "";
                    var level = HeadingFromName(name);
                    if (level == 0 && style.StyleParagraphProperties?.OutlineLevel?.Val?.Value is { } outline && outline < 9)
                    {
                        level = outline + 1;
                    }

                    if (level > 0)
                    {
                        _headingByStyle[id] = level;
                    }
                }
            }

            if (_main is not null)
            {
                foreach (var relation in _main.HyperlinkRelationships)
                {
                    _links[relation.Id] = relation.Uri.ToString();
                }
            }
        }

        public string? Link(string id) => _links.GetValueOrDefault(id);

        public int HeadingLevel(string? styleId, int? outlineLevel)
        {
            if (styleId is not null && _headingByStyle.TryGetValue(styleId, out var level))
            {
                return level;
            }

            return outlineLevel is { } outline and < 9 ? outline + 1 : 0;
        }

        public (bool Ordered, int Level)? ListKind(W.Paragraph paragraph)
        {
            var numbering = paragraph.ParagraphProperties?.NumberingProperties;
            if (numbering?.NumberingId?.Val?.Value is not { } numId || numId == 0)
            {
                return null;
            }

            var level = numbering.NumberingLevelReference?.Val?.Value ?? 0;
            if (!_ordered.TryGetValue((numId, level), out var ordered))
            {
                ordered = IsOrdered(numId, level);
                _ordered[(numId, level)] = ordered;
            }

            return (ordered, level);
        }

        private bool IsOrdered(int numId, int level)
        {
            var numbering = _main?.NumberingDefinitionsPart?.Numbering;
            var instance = numbering?.Elements<W.NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numId);
            var abstractId = instance?.AbstractNumId?.Val?.Value;
            var abstractNum = numbering?.Elements<W.AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == abstractId);
            var format = abstractNum?.Elements<W.Level>().FirstOrDefault(l => l.LevelIndex?.Value == level)?.NumberingFormat?.Val;
            return format is not null && format.Value != W.NumberFormatValues.Bullet && format.Value != W.NumberFormatValues.None;
        }

        private static int HeadingFromName(string name)
        {
            if (name.Equals("Title", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            const string prefix = "heading ";
            return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                   int.TryParse(name[prefix.Length..], out var level) && level is >= 1 and <= 9
                ? level
                : 0;
        }
    }
}
