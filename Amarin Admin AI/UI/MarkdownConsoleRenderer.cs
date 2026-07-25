using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Amarin.UI;

/// <summary>
/// Renders Markdown to the console via Markdig (parse) + Spectre.Console (ANSI output).
/// Supports full-document <see cref="Render"/> and chunked streaming via
/// <see cref="AppendChunk"/> / <see cref="Complete"/>.
/// </summary>
internal sealed class MarkdownConsoleRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly Color[] HeadingColors =
    [
        Color.Cyan1,
        Color.DeepSkyBlue1,
        Color.SteelBlue1,
        Color.DodgerBlue1,
        Color.CornflowerBlue,
        Color.LightSkyBlue1
    ];

    private readonly IAnsiConsole _console;

    // Streaming state
    private readonly StringBuilder _streamBuffer = new();
    private bool _inFencedCode;
    private string _fenceMarker = "```";
    private readonly StringBuilder _codeFenceBuffer = new();
    private string? _codeFenceInfo;

    public MarkdownConsoleRenderer(IAnsiConsole? console = null)
    {
        _console = console ?? AnsiConsole.Console;
    }

    /// <summary>
    /// Parse and render a complete Markdown document.
    /// </summary>
    public void Render(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return;
        }

        try
        {
            var renderable = ToRenderable(markdown);
            _console.Write(renderable);
            _console.WriteLine();
        }
        catch
        {
            // Fallback: plain text if anything goes wrong (legacy terminals / unexpected markup)
            _console.Write(new Text(markdown));
            _console.WriteLine();
        }
    }

    /// <summary>
    /// Build a Spectre renderable tree without writing to the console.
    /// Useful for panels and composition.
    /// </summary>
    public IRenderable ToRenderable(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return Text.Empty;
        }

        var normalized = NormalizeNewlines(markdown);
        var document = Markdown.Parse(normalized, Pipeline);
        var parts = new List<IRenderable>();
        CollectBlocks(document, parts, listDepth: 0, quoteDepth: 0);

        if (parts.Count == 0)
        {
            return Text.Empty;
        }

        return parts.Count == 1 ? parts[0] : new Rows(parts);
    }

    /// <summary>
    /// Convert Markdown to Spectre markup string (inline-friendly subset).
    /// Escapes <c>[</c>/<c>]</c>. Suitable for short strings inside <see cref="Markup"/>.
    /// </summary>
    public static string ToSpectreMarkup(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var normalized = NormalizeNewlines(markdown).Trim();
        var document = Markdown.Parse(normalized, Pipeline);
        var sb = new StringBuilder();
        var first = true;

        foreach (var block in document)
        {
            if (!first)
            {
                sb.Append('\n');
            }

            first = false;
            AppendBlockMarkup(block, sb, listDepth: 0);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Streaming: append a chunk. Complete lines (and closed code fences) are rendered immediately.
    /// </summary>
    public void AppendChunk(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        _streamBuffer.Append(chunk.Replace("\r\n", "\n").Replace('\r', '\n'));
        DrainStreamBuffer(flush: false);
    }

    /// <summary>
    /// Streaming: flush any remaining buffered content (including an unclosed code fence).
    /// </summary>
    public void Complete()
    {
        DrainStreamBuffer(flush: true);
        ResetStreamState();
    }

    /// <summary>
    /// Reset streaming buffers without rendering.
    /// </summary>
    public void Reset()
    {
        ResetStreamState();
    }

    private void ResetStreamState()
    {
        _streamBuffer.Clear();
        _codeFenceBuffer.Clear();
        _inFencedCode = false;
        _fenceMarker = "```";
        _codeFenceInfo = null;
    }

    private void DrainStreamBuffer(bool flush)
    {
        while (true)
        {
            var text = _streamBuffer.ToString();
            var newlineIndex = text.IndexOf('\n');

            if (newlineIndex < 0)
            {
                if (flush && text.Length > 0)
                {
                    ProcessStreamLine(text, isCompleteLine: false);
                    _streamBuffer.Clear();
                }

                if (flush && _inFencedCode)
                {
                    FlushOpenCodeFenceAsCode();
                }

                return;
            }

            var line = text[..newlineIndex];
            _streamBuffer.Remove(0, newlineIndex + 1);
            ProcessStreamLine(line, isCompleteLine: true);
        }
    }

    private void ProcessStreamLine(string line, bool isCompleteLine)
    {
        if (_inFencedCode)
        {
            if (IsClosingFence(line, _fenceMarker))
            {
                RenderCodePanel(_codeFenceInfo, _codeFenceBuffer.ToString().TrimEnd('\n'));
                _codeFenceBuffer.Clear();
                _inFencedCode = false;
                _codeFenceInfo = null;
                return;
            }

            if (_codeFenceBuffer.Length > 0)
            {
                _codeFenceBuffer.Append('\n');
            }

            _codeFenceBuffer.Append(line);
            return;
        }

        if (TryOpenFence(line, out var marker, out var info))
        {
            _inFencedCode = true;
            _fenceMarker = marker;
            _codeFenceInfo = info;
            _codeFenceBuffer.Clear();
            return;
        }

        // Incomplete trailing line without newline: only emit on flush.
        if (!isCompleteLine && string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Render(line);
    }

    private void FlushOpenCodeFenceAsCode()
    {
        RenderCodePanel(_codeFenceInfo, _codeFenceBuffer.ToString().TrimEnd('\n'));
        _codeFenceBuffer.Clear();
        _inFencedCode = false;
        _codeFenceInfo = null;
    }

    private static bool TryOpenFence(string line, out string marker, out string? info)
    {
        marker = "```";
        info = null;
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            marker = "```";
            info = trimmed[3..].Trim();
            if (string.IsNullOrEmpty(info))
            {
                info = null;
            }

            return true;
        }

        if (trimmed.StartsWith("~~~", StringComparison.Ordinal))
        {
            marker = "~~~";
            info = trimmed[3..].Trim();
            if (string.IsNullOrEmpty(info))
            {
                info = null;
            }

            return true;
        }

        return false;
    }

    private static bool IsClosingFence(string line, string marker)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith(marker, StringComparison.Ordinal))
        {
            return false;
        }

        // Closing fence: only backticks/tildes and optional trailing spaces.
        for (var i = marker.Length; i < trimmed.Length; i++)
        {
            if (trimmed[i] is not ('`' or '~' or ' ' or '\t'))
            {
                return false;
            }
        }

        return true;
    }

    private void RenderCodePanel(string? language, string code)
    {
        try
        {
            _console.Write(BuildCodePanel(language, code));
            _console.WriteLine();
        }
        catch
        {
            _console.Write(new Text(code));
            _console.WriteLine();
        }
    }

    private void CollectBlocks(
        IEnumerable<Block> blocks,
        List<IRenderable> parts,
        int listDepth,
        int quoteDepth)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    parts.Add(BuildHeading(heading, quoteDepth));
                    break;

                case ParagraphBlock paragraph:
                    parts.Add(BuildParagraph(paragraph, quoteDepth));
                    break;

                case ListBlock list:
                    parts.Add(BuildList(list, listDepth, quoteDepth));
                    break;

                case QuoteBlock quote:
                    parts.Add(BuildQuote(quote, listDepth, quoteDepth));
                    break;

                case FencedCodeBlock fenced:
                    parts.Add(BuildCodePanel(fenced.Info, GetCodeBlockText(fenced)));
                    break;

                case CodeBlock code when code is not FencedCodeBlock:
                    parts.Add(BuildCodePanel(null, GetCodeBlockText(code)));
                    break;

                case Markdig.Extensions.Tables.Table table:
                    parts.Add(BuildTable(table));
                    break;

                case ThematicBreakBlock:
                    parts.Add(new Rule { Style = Style.Parse("grey") });
                    break;

                case BlankLineBlock:
                    parts.Add(Text.Empty);
                    break;

                case HtmlBlock:
                    // Raw HTML from models — show as plain escaped text.
                    parts.Add(new Markup(WithQuotePrefix(quoteDepth, Markup.Escape(GetLeafText(block)))));
                    break;

                case ContainerBlock container:
                    CollectBlocks(container, parts, listDepth, quoteDepth);
                    break;

                default:
                    var leaf = GetLeafText(block);
                    if (!string.IsNullOrWhiteSpace(leaf))
                    {
                        parts.Add(new Markup(WithQuotePrefix(quoteDepth, Markup.Escape(leaf))));
                    }

                    break;
            }
        }
    }

    private static IRenderable BuildHeading(HeadingBlock heading, int quoteDepth)
    {
        var level = Math.Clamp(heading.Level, 1, 6);
        var color = HeadingColors[level - 1];
        var text = BuildInlineMarkup(heading.Inline);
        var styled = new Markup(WithQuotePrefix(quoteDepth, $"[bold {ColorToMarkup(color)}]{text}[/]"));
        return new Rows(Text.Empty, styled, Text.Empty);
    }

    private static IRenderable BuildParagraph(ParagraphBlock paragraph, int quoteDepth)
    {
        var inline = BuildInlineMarkup(paragraph.Inline);
        if (quoteDepth > 0)
        {
            // Vertical bar + muted colour for quoted paragraphs.
            return new Markup(WithQuotePrefix(quoteDepth, $"[grey]{inline}[/]"));
        }

        return new Markup(inline);
    }

    private IRenderable BuildList(ListBlock list, int listDepth, int quoteDepth)
    {
        var rows = new List<IRenderable>();
        var index = GetOrderedStart(list);
        var indent = new string(' ', listDepth * 2);

        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem)
            {
                continue;
            }

            var marker = list.IsOrdered ? $"{index}." : "•";
            var itemParts = new List<IRenderable>();
            var firstContent = true;

            foreach (var child in listItem)
            {
                if (child is ParagraphBlock paragraph)
                {
                    var inline = BuildInlineMarkup(paragraph.Inline);
                    var markerPrefix = firstContent
                        ? $"{Markup.Escape(indent)}{Markup.Escape(marker)} "
                        : $"{Markup.Escape(indent)}{new string(' ', marker.Length + 1)}";
                    var body = quoteDepth > 0 ? $"[grey]{inline}[/]" : inline;
                    itemParts.Add(new Markup(WithQuotePrefix(quoteDepth, markerPrefix + body)));
                    firstContent = false;
                }
                else if (child is ListBlock nested)
                {
                    itemParts.Add(BuildList(nested, listDepth + 1, quoteDepth));
                    firstContent = false;
                }
                else if (child is QuoteBlock quote)
                {
                    itemParts.Add(BuildQuote(quote, listDepth + 1, quoteDepth));
                    firstContent = false;
                }
                else if (child is FencedCodeBlock fenced)
                {
                    itemParts.Add(BuildCodePanel(fenced.Info, GetCodeBlockText(fenced)));
                    firstContent = false;
                }
                else if (child is CodeBlock code)
                {
                    itemParts.Add(BuildCodePanel(null, GetCodeBlockText(code)));
                    firstContent = false;
                }
            }

            if (itemParts.Count == 1)
            {
                rows.Add(itemParts[0]);
            }
            else if (itemParts.Count > 1)
            {
                rows.Add(new Rows(itemParts));
            }

            if (list.IsOrdered)
            {
                index++;
            }
        }

        return rows.Count switch
        {
            0 => Text.Empty,
            1 => rows[0],
            _ => new Rows(rows)
        };
    }

    private IRenderable BuildQuote(QuoteBlock quote, int listDepth, int quoteDepth)
    {
        var inner = new List<IRenderable>();
        CollectBlocks(quote, inner, listDepth, quoteDepth + 1);

        if (inner.Count == 0)
        {
            return new Markup(WithQuotePrefix(quoteDepth + 1, string.Empty));
        }

        return inner.Count == 1 ? inner[0] : new Rows(inner);
    }

    private static string WithQuotePrefix(int quoteDepth, string content)
    {
        if (quoteDepth <= 0)
        {
            return content;
        }

        var bars = string.Concat(Enumerable.Repeat("[grey]│[/] ", quoteDepth));
        return bars + content;
    }

    private static Panel BuildCodePanel(string? language, string code)
    {
        // Code must never go through Markup — no bold/italic/link processing, no [ ] exceptions.
        var body = new Text(code ?? string.Empty);
        var header = string.IsNullOrWhiteSpace(language)
            ? "code"
            : language.Trim();

        return new Panel(body)
        {
            Header = new PanelHeader($" {header} "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            Padding = new Padding(1, 0)
        };
    }

    private IRenderable BuildTable(Markdig.Extensions.Tables.Table table)
    {
        var spectreTable = new Spectre.Console.Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey);

        var isHeader = true;
        foreach (var row in table)
        {
            if (row is not Markdig.Extensions.Tables.TableRow tableRow)
            {
                continue;
            }

            var cells = new List<string>();
            foreach (var cell in tableRow)
            {
                if (cell is Markdig.Extensions.Tables.TableCell tableCell)
                {
                    cells.Add(BuildContainerInlineMarkup(tableCell));
                }
                else
                {
                    cells.Add(string.Empty);
                }
            }

            if (isHeader)
            {
                foreach (var cell in cells)
                {
                    spectreTable.AddColumn(new TableColumn(new Markup($"[bold]{cell}[/]")));
                }

                isHeader = false;
            }
            else
            {
                // Ensure column count matches
                while (spectreTable.Columns.Count < cells.Count)
                {
                    spectreTable.AddColumn(new TableColumn(""));
                }

                spectreTable.AddRow(cells.Select(c => new Markup(c)).ToArray());
            }
        }

        return spectreTable;
    }

    private static string BuildContainerInlineMarkup(ContainerBlock container)
    {
        var sb = new StringBuilder();
        foreach (var block in container)
        {
            if (block is ParagraphBlock paragraph)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(BuildInlineMarkup(paragraph.Inline));
            }
            else
            {
                var text = GetLeafText(block);
                if (!string.IsNullOrEmpty(text))
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(' ');
                    }

                    sb.Append(Markup.Escape(text.Trim()));
                }
            }
        }

        return sb.ToString();
    }

    private static string BuildInlineMarkup(ContainerInline? inline)
    {
        if (inline is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        AppendInlines(inline, sb);
        return sb.ToString();
    }

    private static void AppendInlines(ContainerInline container, StringBuilder sb)
    {
        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            AppendInline(child, sb);
        }
    }

    private static void AppendInline(Inline inline, StringBuilder sb)
    {
        switch (inline)
        {
            case LiteralInline literal:
                sb.Append(Markup.Escape(literal.Content.ToString()));
                break;

            case EmphasisInline emphasis:
            {
                var open = emphasis.DelimiterCount >= 2 ? "[bold]" : "[italic]";
                sb.Append(open);
                AppendInlines(emphasis, sb);
                sb.Append("[/]");
                break;
            }

            case CodeInline code:
                // Yellow/cyan on grey background; content escaped.
                sb.Append("[black on grey][yellow]");
                sb.Append(Markup.Escape(code.Content));
                sb.Append("[/][/]");
                break;

            case LinkInline link:
            {
                var label = new StringBuilder();
                AppendInlines(link, label);
                var labelText = label.ToString();
                if (link.IsImage)
                {
                    sb.Append(labelText);
                    if (!string.IsNullOrEmpty(link.Url))
                    {
                        sb.Append(Markup.Escape($" ({link.Url})"));
                    }
                }
                else
                {
                    // text (url)
                    var url = link.Url ?? string.Empty;
                    if (string.IsNullOrEmpty(labelText))
                    {
                        sb.Append(Markup.Escape(url));
                    }
                    else if (string.IsNullOrEmpty(url))
                    {
                        sb.Append(labelText);
                    }
                    else
                    {
                        sb.Append(labelText);
                        sb.Append(Markup.Escape($" ({url})"));
                    }
                }

                break;
            }

            case LineBreakInline lineBreak:
                sb.Append(lineBreak.IsHard ? '\n' : ' ');
                break;

            case HtmlInline html:
                sb.Append(Markup.Escape(html.Tag));
                break;

            case AutolinkInline autolink:
                sb.Append(Markup.Escape(autolink.Url));
                break;

            case ContainerInline nested:
                AppendInlines(nested, sb);
                break;

            default:
                // Unknown inline — best-effort plain text if available via ToString
                var raw = inline.ToString();
                if (!string.IsNullOrEmpty(raw) && raw != inline.GetType().Name)
                {
                    sb.Append(Markup.Escape(raw));
                }

                break;
        }
    }

    private static void AppendBlockMarkup(Block block, StringBuilder sb, int listDepth)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var level = Math.Clamp(heading.Level, 1, 6);
                var color = HeadingColors[level - 1];
                sb.Append($"[bold {ColorToMarkup(color)}]");
                sb.Append(BuildInlineMarkup(heading.Inline));
                sb.Append("[/]");
                break;
            }

            case ParagraphBlock paragraph:
                sb.Append(BuildInlineMarkup(paragraph.Inline));
                break;

            case ListBlock list:
            {
                var index = GetOrderedStart(list);
                var first = true;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    if (!first)
                    {
                        sb.Append('\n');
                    }

                    first = false;
                    var marker = list.IsOrdered ? $"{index}." : "•";
                    sb.Append(Markup.Escape(new string(' ', listDepth * 2)));
                    sb.Append(Markup.Escape(marker));
                    sb.Append(' ');

                    var para = item.OfType<ParagraphBlock>().FirstOrDefault();
                    if (para is not null)
                    {
                        sb.Append(BuildInlineMarkup(para.Inline));
                    }

                    foreach (var nested in item.OfType<ListBlock>())
                    {
                        sb.Append('\n');
                        AppendBlockMarkup(nested, sb, listDepth + 1);
                    }

                    if (list.IsOrdered)
                    {
                        index++;
                    }
                }

                break;
            }

            case QuoteBlock quote:
            {
                var first = true;
                foreach (var child in quote)
                {
                    if (!first)
                    {
                        sb.Append('\n');
                    }

                    first = false;
                    sb.Append("[grey]│ [/]");
                    AppendBlockMarkup(child, sb, listDepth);
                }

                break;
            }

            case FencedCodeBlock fenced:
                sb.Append(Markup.Escape(GetCodeBlockText(fenced)));
                break;

            case CodeBlock code:
                sb.Append(Markup.Escape(GetCodeBlockText(code)));
                break;

            case ThematicBreakBlock:
                sb.Append("[grey]────────────────────────────────────────[/]");
                break;

            default:
                sb.Append(Markup.Escape(GetLeafText(block)));
                break;
        }
    }

    private static string GetCodeBlockText(LeafBlock block)
    {
        if (block.Lines.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < block.Lines.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            sb.Append(block.Lines.Lines[i].Slice.ToString());
        }

        return sb.ToString();
    }

    private static string GetLeafText(Block block)
    {
        if (block is LeafBlock leaf && leaf.Inline is not null)
        {
            return BuildPlainText(leaf.Inline);
        }

        if (block is LeafBlock leafBlock && leafBlock.Lines.Count > 0)
        {
            return GetCodeBlockText(leafBlock);
        }

        return string.Empty;
    }

    private static string BuildPlainText(ContainerInline container)
    {
        var sb = new StringBuilder();
        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            switch (child)
            {
                case LiteralInline lit:
                    sb.Append(lit.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case ContainerInline nested:
                    sb.Append(BuildPlainText(nested));
                    break;
                case LineBreakInline:
                    sb.Append('\n');
                    break;
            }
        }

        return sb.ToString();
    }

    private static string ColorToMarkup(Color color) => color.ToString();

    private static int GetOrderedStart(ListBlock list)
    {
        if (!list.IsOrdered)
        {
            return 1;
        }

        // Markdig exposes OrderedStart as string (e.g. "1") in current versions.
        return int.TryParse(list.OrderedStart, out var start) && start > 0 ? start : 1;
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');
}
