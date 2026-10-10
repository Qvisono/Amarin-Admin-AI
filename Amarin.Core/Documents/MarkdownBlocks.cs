using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Amarin.Core;

/// <summary>Блок документа, собранного из Markdown: из этих блоков пишутся и Word, и PDF.</summary>
internal abstract record DocBlock;

internal sealed record DocHeading(int Level, IReadOnlyList<DocSpan> Spans) : DocBlock;

internal sealed record DocText(IReadOnlyList<DocSpan> Spans) : DocBlock;

/// <param name="Start">С какого номера считать нумерованный список.</param>
internal sealed record DocList(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<DocBlock>> Items) : DocBlock;

/// <param name="Rows">Строки таблицы; первая — заголовок, если <paramref name="HasHeader"/>.</param>
internal sealed record DocTable(IReadOnlyList<IReadOnlyList<IReadOnlyList<DocSpan>>> Rows, bool HasHeader) : DocBlock;

internal sealed record DocCode(string Text, string? Language) : DocBlock;

internal sealed record DocQuote(IReadOnlyList<DocBlock> Blocks) : DocBlock;

internal sealed record DocRule : DocBlock;

internal sealed record DocPageBreak : DocBlock;

/// <param name="Source">Путь к картинке на диске или ссылка <c>amarin-image:</c> из чата.</param>
internal sealed record DocImage(string Source, string Caption) : DocBlock;

/// <summary>Кусок текста с одним начертанием.</summary>
internal sealed record DocSpan(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Code = false,
    bool Strike = false,
    string? Link = null);

/// <summary>
/// Markdown, который пишет модель, — в простые блоки документа.
/// </summary>
/// <remarks>
/// Модель пишет Markdown свободно и хорошо, а разметку Word или PDF — плохо и дорого. Поэтому
/// <c>create_document</c> принимает Markdown, и одна и та же разборка ведёт и Word, и PDF, и
/// вставку абзацев в чужой документ: заголовок, список и таблица выходят одинаковыми везде.
/// Разрыв страницы — строка <c>\pagebreak</c> или <c>&lt;!-- pagebreak --&gt;</c>.
/// </remarks>
internal static class MarkdownBlocks
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseGridTables()
        .UseEmphasisExtras()
        .UseListExtras()
        .UseTaskLists()
        .UseAutoLinks()
        .Build();

    public static IReadOnlyList<DocBlock> Parse(string markdown)
    {
        var normalized = (markdown ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);
        var document = Markdown.Parse(normalized, Pipeline);
        return Blocks(document);
    }

    private static List<DocBlock> Blocks(ContainerBlock container)
    {
        var blocks = new List<DocBlock>();
        foreach (var block in container)
        {
            Add(blocks, block);
        }

        return blocks;
    }

    private static void Add(List<DocBlock> blocks, Block block)
    {
        switch (block)
        {
            case Markdig.Syntax.HeadingBlock heading:
                blocks.Add(new DocHeading(Math.Clamp(heading.Level, 1, 6), Spans(heading.Inline)));
                break;
            case ParagraphBlock paragraph when IsPageBreak(paragraph):
                blocks.Add(new DocPageBreak());
                break;
            case ParagraphBlock paragraph:
                AddParagraph(blocks, paragraph);
                break;
            case ListBlock list:
                blocks.Add(new DocList(
                    list.IsOrdered,
                    int.TryParse(list.OrderedStart, out var start) ? start : 1,
                    [.. list.OfType<ListItemBlock>().Select(item => (IReadOnlyList<DocBlock>)Blocks(item))]));
                break;
            case Table table:
                blocks.Add(new DocTable(
                    [.. table.OfType<TableRow>().Select(row => (IReadOnlyList<IReadOnlyList<DocSpan>>)
                        [.. row.OfType<TableCell>().Select(CellSpans)])],
                    table.OfType<TableRow>().FirstOrDefault()?.IsHeader == true));
                break;
            case FencedCodeBlock fenced:
                blocks.Add(new DocCode(Lines(fenced), string.IsNullOrWhiteSpace(fenced.Info) ? null : fenced.Info));
                break;
            case CodeBlock code:
                blocks.Add(new DocCode(Lines(code), null));
                break;
            case QuoteBlock quote:
                blocks.Add(new DocQuote(Blocks(quote)));
                break;
            case ThematicBreakBlock:
                blocks.Add(new DocRule());
                break;
            case HtmlBlock html when Lines(html).Contains("pagebreak", StringComparison.OrdinalIgnoreCase):
                blocks.Add(new DocPageBreak());
                break;
            case ContainerBlock nested:
                blocks.AddRange(Blocks(nested));
                break;
        }
    }

    /// <summary>Абзац; картинки из него встают отдельными блоками сразу после текста.</summary>
    private static void AddParagraph(List<DocBlock> blocks, ParagraphBlock paragraph)
    {
        var spans = Spans(paragraph.Inline);
        if (spans.Any(span => span.Text.Trim().Length > 0))
        {
            blocks.Add(new DocText(spans));
        }

        foreach (var image in paragraph.Inline?.Descendants<LinkInline>().Where(link => link.IsImage) ?? [])
        {
            if (!string.IsNullOrWhiteSpace(image.Url))
            {
                blocks.Add(new DocImage(image.Url.Trim(), PlainText(image)));
            }
        }
    }

    private static bool IsPageBreak(ParagraphBlock paragraph)
    {
        var text = paragraph.Inline is null ? "" : PlainText(paragraph.Inline).Trim();
        return text.Equals("\\pagebreak", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("\\newpage", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<DocSpan> CellSpans(TableCell cell) =>
        [.. cell.OfType<ParagraphBlock>().SelectMany((paragraph, index) =>
            index == 0 ? Spans(paragraph.Inline) : [new DocSpan(" "), .. Spans(paragraph.Inline)])];

    private static string Lines(LeafBlock block)
    {
        var lines = block.Lines.Lines?.Take(block.Lines.Count).Select(line => line.ToString()) ?? [];
        return string.Join("\n", lines);
    }

    public static List<DocSpan> Spans(ContainerInline? inline)
    {
        var spans = new List<DocSpan>();
        if (inline is not null)
        {
            Collect(spans, inline, new DocSpan(""));
        }

        return Merge(spans);
    }

    private static void Collect(List<DocSpan> spans, ContainerInline container, DocSpan style)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    spans.Add(style with { Text = literal.Content.ToString() });
                    break;
                case CodeInline code:
                    spans.Add(style with { Text = code.Content, Code = true });
                    break;
                case LineBreakInline lineBreak:
                    spans.Add(style with { Text = lineBreak.IsHard ? "\n" : " " });
                    break;
                case AutolinkInline auto:
                    spans.Add(style with { Text = auto.Url, Link = auto.IsEmail ? "mailto:" + auto.Url : auto.Url });
                    break;
                case LinkInline { IsImage: true }:
                    // Картинка встаёт отдельным блоком после абзаца (AddParagraph).
                    break;
                case LinkInline link:
                    Collect(spans, link, style with { Link = link.Url });
                    break;
                case EmphasisInline emphasis:
                    var next = emphasis.DelimiterChar is '~'
                        ? style with { Strike = true }
                        : emphasis.DelimiterCount >= 2
                            ? style with { Bold = true }
                            : style with { Italic = true };
                    Collect(spans, emphasis, next);
                    break;
                case HtmlInline html when html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase):
                    spans.Add(style with { Text = "\n" });
                    break;
                case ContainerInline nested:
                    Collect(spans, nested, style);
                    break;
                case LeafInline leaf:
                    spans.Add(style with { Text = leaf.ToString() ?? "" });
                    break;
            }
        }
    }

    /// <summary>Соседние куски одного начертания — в один: Word и PDF не плодят лишних фрагментов.</summary>
    private static List<DocSpan> Merge(List<DocSpan> spans)
    {
        var merged = new List<DocSpan>(spans.Count);
        foreach (var span in spans)
        {
            if (span.Text.Length == 0)
            {
                continue;
            }

            if (merged.Count > 0 && merged[^1] is var last && last with { Text = "" } == span with { Text = "" })
            {
                merged[^1] = last with { Text = last.Text + span.Text };
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }

    public static string PlainText(ContainerInline inline)
    {
        var text = new StringBuilder();
        foreach (var span in Spans(inline))
        {
            text.Append(span.Text);
        }

        return text.ToString();
    }

    /// <summary>Текст кусков без начертания — для подписей и поиска.</summary>
    public static string PlainText(IEnumerable<DocSpan> spans) => string.Concat(spans.Select(span => span.Text));

    /// <summary>
    /// Таблицу шире страницы — блоками столбцов, которые помещаются: первый столбец (подписи строк)
    /// повторяется в каждом блоке. Помещается целиком — та же таблица.
    /// </summary>
    /// <remarks>
    /// У столбца есть наименьшая читаемая ширина, и до этого таблица в сто столбцов просто уходила за
    /// край страницы: в PDF всё правее обрезалось молча, в Word ячейки сжимались до буквы в строке.
    /// </remarks>
    /// <param name="widths">Ширина каждого столбца, уже не меньше наименьшей.</param>
    internal static IReadOnlyList<DocTable> ColumnBlocks(DocTable table, IReadOnlyList<double> widths, double available)
    {
        var columns = widths.Count;
        if (columns <= 2 || widths.Sum() <= available)
        {
            return [table];
        }

        var blocks = new List<DocTable>();
        var start = 1;
        while (start < columns)
        {
            var used = widths[0];
            var end = start;

            // Хотя бы один столбец в блоке, даже если вместе с подписями он шире страницы.
            while (end < columns && (end == start || used + widths[end] <= available))
            {
                used += widths[end];
                end++;
            }

            int[] keep = [0, .. Enumerable.Range(start, end - start)];
            blocks.Add(new DocTable(
                [.. table.Rows.Select(row => (IReadOnlyList<IReadOnlyList<DocSpan>>)[.. keep.Select(column => column < row.Count ? row[column] : [])])],
                table.HasHeader));
            start = end;
        }

        return blocks;
    }
}
