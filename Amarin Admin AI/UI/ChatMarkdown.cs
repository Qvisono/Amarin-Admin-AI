using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfList = System.Windows.Documents.List;
using WpfListItem = System.Windows.Documents.ListItem;
using WpfParagraph = System.Windows.Documents.Paragraph;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace Amarin.UI;

internal static class ChatMarkdown
{
    // Набор расширений собран вручную: UseAdvancedExtensions() тянет Mathematics (сломает
    // $var в PowerShell) и Diagrams (проглотит ```mermaid целиком).
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseGridTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseListExtras()
        .UseAutoLinks()
        .Build();

    private const double BlockGap = 8;
    private const string BodyBrush = "Text.Secondary";
    private const string MonoFamily = "Consolas, Cascadia Mono, Courier New";

    public static IReadOnlyList<string> PreviewLines(string text)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return lines;
        }

        foreach (var block in Markdown.Parse(Normalize(text), Pipeline))
        {
            AppendPreview(lines, block);
        }

        if (lines.Count == 0 && !string.IsNullOrWhiteSpace(text))
        {
            lines.Add(text.Trim());
        }

        return lines;
    }

    /// <summary>
    /// Есть ли в тексте блочная разметка. Пузырь пользователя обжимается по ширине текста,
    /// что несовместимо с таблицами, списками и блоками кода — им нужна вся доступная ширина.
    /// </summary>
    internal static bool HasBlockConstructs(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var block in Markdown.Parse(Normalize(text), Pipeline))
        {
            if (block is not ParagraphBlock)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Текст без inline-разметки — то, что реально увидит глаз. Нужен для замера ширины
    /// пузыря: «**жирный**» иначе намерит на четыре символа шире отрисованного.
    /// </summary>
    internal static string FlattenInline(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text ?? "";
        }

        var lines = new List<string>();
        foreach (var block in Markdown.Parse(Normalize(text), Pipeline))
        {
            if (block is not ParagraphBlock { Inline: not null } paragraph)
            {
                return text;
            }

            lines.Add(InlineText(paragraph.Inline));
        }

        return lines.Count == 0 ? text : string.Join("\n", lines);
    }

    /// <param name="box">Куда положить готовый документ.</param>
    /// <param name="host">
    /// Владелец ресурсов — окно. Стили вроде MsgActionButton лежат в Window.Resources,
    /// а сам <paramref name="box"/> в момент вызова ещё может быть вне дерева.
    /// </param>
    public static void Write(
        RichTextBox box,
        FrameworkElement host,
        string text,
        double fontSize,
        double lineHeight,
        bool fillAvailableWidth = true)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
            FontSize = fontSize,
            LineHeight = lineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        if (box.FontFamily is not null)
        {
            document.FontFamily = box.FontFamily;
        }

        var context = new RenderContext(host, fontSize, lineHeight);
        if (string.IsNullOrEmpty(text))
        {
            document.Blocks.Add(BuildPlainParagraph("", context, last: true));
        }
        else
        {
            var blocks = Markdown.Parse(Normalize(text), Pipeline).ToList();
            if (blocks.Count == 0)
            {
                // Текст из одних пробелов: разметки нет, но пустой документ выглядел бы багом.
                document.Blocks.Add(BuildPlainParagraph(text, context, last: true));
            }
            else
            {
                AddBlocks(document.Blocks, blocks, context, first: true);
            }
        }

        box.Document = document;
        if (fillAvailableWidth && box.ActualWidth > 1)
        {
            document.PageWidth = box.ActualWidth;
        }
    }

    public static string ReadPlain(RichTextBox box)
    {
        var range = new TextRange(box.Document.ContentStart, box.Document.ContentEnd);
        return range.Text.TrimEnd('\r', '\n');
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Всё, что нужно знать при построении блоков: кегль, интерлиньяж и хозяин ресурсов.</summary>
    private sealed record RenderContext(FrameworkElement Host, double FontSize, double LineHeight);

    // ===== Блоки =====

    private static void AddBlocks(
        BlockCollection target,
        IReadOnlyList<MdBlock> blocks,
        RenderContext context,
        bool first)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            AddBlock(target, blocks[i], context, first && i == 0, i == blocks.Count - 1);
        }
    }

    private static void AddBlock(
        BlockCollection target,
        MdBlock block,
        RenderContext context,
        bool first,
        bool last)
    {
        switch (block)
        {
            case HeadingBlock heading:
                target.Add(BuildHeading(heading, context, first, last));
                break;

            case FencedCodeBlock fenced:
                target.Add(BuildCode(fenced.Lines.ToString(), fenced.Info, context));
                break;

            case CodeBlock code:
                target.Add(BuildCode(code.Lines.ToString(), null, context));
                break;

            case MdTable table:
                target.Add(BuildTable(table, context, last));
                break;

            case QuoteBlock quote:
                target.Add(BuildQuote(quote, context, last));
                break;

            case ListBlock list:
                target.Add(BuildList(list, context, last));
                break;

            case ThematicBreakBlock:
                target.Add(BuildRule());
                break;

            case ParagraphBlock { Inline: { } inline } when HasPicture(inline):
                // Картинка в абзаце — это иллюстрация, а не текст. Абзац разрезается на части:
                // текст до, сама картинка, текст после. Иначе всё, что не осталось наедине с
                // картинкой, молча вырождалось в синюю ссылку.
                AddSplitParagraph(target, inline, context, last);
                break;

            case ParagraphBlock paragraph:
                target.Add(BuildParagraph(paragraph.Inline, context, last));
                break;

            case LeafBlock { Inline: not null } leaf:
                target.Add(BuildParagraph(leaf.Inline, context, last));
                break;

            case ContainerBlock container:
                // Сноски и прочие контейнеры без своего вида — разворачиваем внутрь.
                AddBlocks(target, container.ToList(), context, first);
                break;

            case LeafBlock lines:
                target.Add(BuildPlainParagraph(lines.Lines.ToString().TrimEnd(), context, last));
                break;
        }
    }

    private static WpfBlock BuildHeading(HeadingBlock heading, RenderContext context, bool first, bool last)
    {
        var level = Math.Clamp(heading.Level, 1, 6);
        var size = level switch
        {
            1 => 20.0,
            2 => 17.5,
            3 => 15.5,
            4 => 14.0,
            5 => context.FontSize,
            _ => 13.0
        };

        var paragraph = new WpfParagraph
        {
            Margin = new Thickness(0, first ? 0 : 14, 0, last ? 0 : 6),
            LineHeight = Math.Max(context.LineHeight, size * 1.4),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            FontSize = size,
            FontWeight = level <= 3 ? FontWeights.Bold : FontWeights.SemiBold
        };
        paragraph.SetResourceReference(
            TextElement.ForegroundProperty,
            level switch { <= 3 => "Text.Primary", <= 5 => "Text.Bright", _ => "Text.Muted" });

        // Тонкая линия под h1/h2 — единственное, что делит длинный ответ на разделы.
        if (level <= 2)
        {
            paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
            paragraph.Padding = new Thickness(0, 0, 0, 5);
            paragraph.SetResourceReference(WpfBlock.BorderBrushProperty, "Border.Subtle");
        }

        AddInlines(paragraph.Inlines, heading.Inline, context);
        return paragraph;
    }

    private static WpfBlock BuildCode(string code, string? language, RenderContext context) =>
        new BlockUIContainer(CodeBlockView.Create(context.Host, code.TrimEnd('\n'), language))
        {
            Margin = new Thickness(0)
        };

    /// <summary>Есть ли в абзаце картинка на верхнем уровне — вложенные в ссылку не в счёт.</summary>
    private static bool HasPicture(ContainerInline inline)
    {
        foreach (var child in inline)
        {
            if (child is LinkInline { IsImage: true })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Разрезает абзац по картинкам: «Вот: ![alt](url) и подпись» превращается в три блока.
    /// Пустые куски выбрасываются, так что абзац из одной картинки остаётся одной картинкой.
    /// </summary>
    private static void AddSplitParagraph(
        BlockCollection target,
        ContainerInline inline,
        RenderContext context,
        bool last)
    {
        var produced = new List<WpfBlock>();
        var run = new List<MdInline>();

        void FlushText()
        {
            var trimmed = TrimBlank(run);
            if (trimmed.Count > 0)
            {
                produced.Add(BuildParagraphFrom(trimmed, context, last: false));
            }

            run.Clear();
        }

        foreach (var child in inline)
        {
            if (child is LinkInline { IsImage: true } picture)
            {
                FlushText();
                produced.Add(BuildImage(picture, context, last: false));
            }
            else
            {
                run.Add(child);
            }
        }

        FlushText();

        if (produced.Count == 0)
        {
            return;
        }

        if (last)
        {
            produced[^1].Margin = new Thickness(0);
        }

        foreach (var block in produced)
        {
            target.Add(block);
        }
    }

    /// <summary>Срезает переносы и пробельные куски с обоих концов куска текста.</summary>
    private static List<MdInline> TrimBlank(List<MdInline> inlines)
    {
        static bool Blank(MdInline inline) => inline switch
        {
            LineBreakInline => true,
            LiteralInline literal => literal.Content.ToString().Trim().Length == 0,
            _ => false
        };

        var start = 0;
        var end = inlines.Count;
        while (start < end && Blank(inlines[start]))
        {
            start++;
        }

        while (end > start && Blank(inlines[end - 1]))
        {
            end--;
        }

        return inlines.GetRange(start, end - start);
    }

    private static WpfBlock BuildImage(LinkInline image, RenderContext context, bool last)
    {
        var url = image.GetDynamicUrl?.Invoke() ?? image.Url ?? "";
        var alt = InlineText(image);
        return new BlockUIContainer(ImageBlockView.Create(context.Host, url, alt))
        {
            Margin = new Thickness(0, 0, 0, last ? 0 : 10)
        };
    }

    private static WpfBlock BuildQuote(QuoteBlock quote, RenderContext context, bool last)
    {
        var section = new Section
        {
            Margin = new Thickness(2, 2, 0, last ? 2 : 10),
            Padding = new Thickness(12, 0, 0, 0),
            BorderThickness = new Thickness(3, 0, 0, 0)
        };
        section.SetResourceReference(WpfBlock.BorderBrushProperty, "Border.Strong");
        section.SetResourceReference(TextElement.ForegroundProperty, "Text.Muted");
        AddBlocks(section.Blocks, quote.ToList(), context, first: true);
        return section;
    }

    private static WpfBlock BuildList(ListBlock list, RenderContext context, bool last)
    {
        var wpf = new WpfList
        {
            Margin = new Thickness(0, 0, 0, last ? 0 : BlockGap),
            Padding = new Thickness(20, 0, 0, 0),
            MarkerOffset = 5,
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            StartIndex = ParseStart(list.OrderedStart)
        };

        foreach (var child in list)
        {
            if (child is not ListItemBlock item)
            {
                continue;
            }

            var blocks = item.ToList();
            var listItem = new WpfListItem { Margin = new Thickness(0, 0, 0, 2) };
            AddBlocks(listItem.Blocks, blocks, context, first: true);

            // Пункт чек-листа рисует свой маркер сам, штатный кружок под ним лишний.
            if (IsTaskItem(blocks))
            {
                wpf.MarkerStyle = TextMarkerStyle.None;
                wpf.Padding = new Thickness(2, 0, 0, 0);
            }

            wpf.ListItems.Add(listItem);
        }

        return wpf;
    }

    private static WpfBlock BuildTable(MdTable table, RenderContext context, bool last)
    {
        var wpf = new WpfTable
        {
            Margin = new Thickness(0, 2, 0, last ? 0 : 12),
            CellSpacing = 0
        };

        var rows = table.OfType<MdTableRow>().ToList();
        var columns = rows.Select(row => row.Count).DefaultIfEmpty(1).Max();
        for (var i = 0; i < columns; i++)
        {
            wpf.Columns.Add(new TableColumn());
        }

        var group = new TableRowGroup();
        wpf.RowGroups.Add(group);

        foreach (var row in rows)
        {
            var wpfRow = new WpfTableRow();
            if (row.IsHeader)
            {
                wpfRow.FontWeight = FontWeights.SemiBold;
                wpfRow.SetResourceReference(TextElement.BackgroundProperty, "Bg.Card");
                wpfRow.SetResourceReference(TextElement.ForegroundProperty, "Text.Bright");
            }

            for (var i = 0; i < columns; i++)
            {
                var cell = new WpfTableCell
                {
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(9, 5, 9, 5),
                    TextAlignment = Alignment(table, i)
                };
                cell.SetResourceReference(WpfTableCell.BorderBrushProperty, "Border.Subtle");

                if (i < row.Count && row[i] is MdTableCell source)
                {
                    AddBlocks(cell.Blocks, source.ToList(), context, first: true);
                }

                wpfRow.Cells.Add(cell);
            }

            group.Rows.Add(wpfRow);
        }

        return wpf;
    }

    private static TextAlignment Alignment(MdTable table, int column)
    {
        if (column >= table.ColumnDefinitions.Count)
        {
            return TextAlignment.Left;
        }

        return table.ColumnDefinitions[column].Alignment switch
        {
            TableColumnAlign.Center => TextAlignment.Center,
            TableColumnAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Left
        };
    }

    private static WpfBlock BuildRule()
    {
        var rule = new WpfParagraph
        {
            Margin = new Thickness(0, 10, 0, 14),
            BorderThickness = new Thickness(0, 0, 0, 1),
            FontSize = 1,
            LineHeight = 1,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        rule.SetResourceReference(WpfBlock.BorderBrushProperty, "Border.Default");
        return rule;
    }

    private static WpfParagraph BuildParagraph(ContainerInline? inline, RenderContext context, bool last)
    {
        var paragraph = NewParagraph(context, last);
        AddInlines(paragraph.Inlines, inline, context);
        return paragraph;
    }

    private static WpfParagraph BuildParagraphFrom(
        IReadOnlyList<MdInline> inlines,
        RenderContext context,
        bool last)
    {
        var paragraph = NewParagraph(context, last);
        foreach (var child in inlines)
        {
            AddInline(paragraph.Inlines, child, context);
        }

        return paragraph;
    }

    private static WpfParagraph BuildPlainParagraph(string text, RenderContext context, bool last)
    {
        var paragraph = NewParagraph(context, last);
        paragraph.Inlines.Add(new Run(text));
        return paragraph;
    }

    private static WpfParagraph NewParagraph(RenderContext context, bool last)
    {
        var paragraph = new WpfParagraph
        {
            Margin = last ? new Thickness(0) : new Thickness(0, 0, 0, BlockGap),
            LineHeight = context.LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = TextAlignment.Left
        };
        paragraph.SetResourceReference(TextElement.ForegroundProperty, BodyBrush);
        return paragraph;
    }

    // ===== Inline =====

    private static void AddInlines(InlineCollection target, ContainerInline? inline, RenderContext context)
    {
        if (inline is null)
        {
            return;
        }

        foreach (var child in inline)
        {
            AddInline(target, child, context);
        }
    }

    private static void AddInline(InlineCollection target, MdInline inline, RenderContext context)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;

            case CodeInline code:
                target.Add(BuildInlineCode(code.Content, context));
                break;

            case EmphasisInline emphasis:
                target.Add(BuildEmphasis(emphasis, context));
                break;

            case LinkInline link:
                target.Add(BuildLink(link, context));
                break;

            case AutolinkInline auto:
                target.Add(BuildAnchor(auto.Url, auto.Url));
                break;

            case TaskList task:
                target.Add(BuildTaskMarker(task));
                break;

            // Мягкий перенос показываем переносом: модели пишут списки шагов, полагаясь
            // именно на это, а не на строгую склейку по CommonMark.
            case LineBreakInline:
                target.Add(new LineBreak());
                break;

            case HtmlEntityInline entity:
                target.Add(new Run(entity.Transcoded.ToString()));
                break;

            case HtmlInline html:
                target.Add(new Run(html.Tag));
                break;

            case ContainerInline container:
                foreach (var child in container)
                {
                    AddInline(target, child, context);
                }

                break;

            default:
                target.Add(new Run(inline.ToString() ?? ""));
                break;
        }
    }

    private static WpfInline BuildInlineCode(string content, RenderContext context)
    {
        // У Run нет padding, поэтому воздух вокруг фона даём тонкими шпациями.
        var run = new Run(" " + content + " ")
        {
            FontFamily = new FontFamily(MonoFamily),
            FontSize = context.FontSize - 1
        };
        run.SetResourceReference(TextElement.ForegroundProperty, "Code.Inline");
        run.SetResourceReference(TextElement.BackgroundProperty, "Bg.Raised");
        return run;
    }

    private static WpfInline BuildEmphasis(EmphasisInline emphasis, RenderContext context)
    {
        var span = new Span();
        switch (emphasis.DelimiterChar)
        {
            case '~' when emphasis.DelimiterCount >= 2:
                span.TextDecorations = TextDecorations.Strikethrough;
                span.SetResourceReference(TextElement.ForegroundProperty, "Text.Muted");
                break;

            case '~':
                span.BaselineAlignment = BaselineAlignment.Subscript;
                span.FontSize = context.FontSize - 3;
                break;

            case '^':
                span.BaselineAlignment = BaselineAlignment.Superscript;
                span.FontSize = context.FontSize - 3;
                break;

            case '=':
                span.SetResourceReference(TextElement.BackgroundProperty, "Status.WarningSurface");
                span.SetResourceReference(TextElement.ForegroundProperty, "Status.Warning");
                break;

            case '+':
                span.TextDecorations = TextDecorations.Underline;
                break;

            default:
                if (emphasis.DelimiterCount >= 2)
                {
                    span.FontWeight = FontWeights.SemiBold;
                    span.SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");
                }

                if (emphasis.DelimiterCount is 1 or 3)
                {
                    span.FontStyle = FontStyles.Italic;
                }

                break;
        }

        foreach (var child in emphasis)
        {
            AddInline(span.Inlines, child, context);
        }

        return span;
    }

    private static WpfInline BuildLink(LinkInline link, RenderContext context)
    {
        var url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? "";
        var label = InlineText(link);
        if (label.Length == 0)
        {
            label = string.IsNullOrEmpty(link.Title) ? url : link.Title!;
        }

        // Картинки из сети не тянем: показываем подпись как обычную ссылку на источник.
        if (link.IsImage)
        {
            return BuildAnchor(label, url);
        }

        var anchor = NewAnchor(url);
        foreach (var child in link)
        {
            AddInline(anchor.Inlines, child, context);
        }

        if (anchor.Inlines.Count == 0)
        {
            anchor.Inlines.Add(new Run(label));
        }

        return anchor;
    }

    private static WpfInline BuildAnchor(string label, string url)
    {
        var anchor = NewAnchor(url);
        anchor.Inlines.Add(new Run(label));
        return anchor;
    }

    private static Hyperlink NewAnchor(string url)
    {
        var anchor = new Hyperlink { ToolTip = url };
        anchor.SetResourceReference(TextElement.ForegroundProperty, "Link.Default");
        anchor.MouseEnter += (s, _) =>
            ((Hyperlink)s!).SetResourceReference(TextElement.ForegroundProperty, "Link.Hover");
        anchor.MouseLeave += (s, _) =>
            ((Hyperlink)s!).SetResourceReference(TextElement.ForegroundProperty, "Link.Default");

        if (!IsBrowsable(url, out var target))
        {
            return anchor;
        }

        anchor.NavigateUri = target;
        anchor.RequestNavigate += (_, e) =>
        {
            e.Handled = true;
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // Нет браузера по умолчанию или политика запрещает — тихо игнорируем.
            }
        };
        return anchor;
    }

    /// <summary>Отдаём оболочке только безопасные схемы: никаких file://, javascript: и прочего.</summary>
    private static bool IsBrowsable(string url, out Uri target)
    {
        target = null!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp
            && uri.Scheme != Uri.UriSchemeHttps
            && uri.Scheme != Uri.UriSchemeMailto)
        {
            return false;
        }

        target = uri;
        return true;
    }

    private static WpfInline BuildTaskMarker(TaskList task)
    {
        var run = new Run(task.Checked ? "☑ " : "☐ ");
        run.SetResourceReference(TextElement.ForegroundProperty, task.Checked ? "Status.Success" : "Text.Muted");
        return run;
    }

    private static bool IsTaskItem(IReadOnlyList<MdBlock> blocks) =>
        blocks.Count > 0
        && blocks[0] is ParagraphBlock { Inline: { } inline }
        && inline.FirstChild is TaskList;

    private static int ParseStart(string? orderedStart) =>
        int.TryParse(orderedStart, out var value) && value > 0 ? value : 1;

    // ===== Предпросмотр в списке чатов =====

    private static void AppendPreview(List<string> lines, MdBlock block)
    {
        switch (block)
        {
            case ParagraphBlock paragraph:
                var paragraphText = InlineText(paragraph.Inline);
                if (paragraphText.Length > 0)
                {
                    lines.Add(paragraphText);
                }

                break;
            case HeadingBlock heading:
                var headingText = InlineText(heading.Inline);
                if (headingText.Length > 0)
                {
                    lines.Add(headingText);
                }

                break;
            case ListBlock list:
                foreach (var item in list)
                {
                    if (item is not ListItemBlock listItem)
                    {
                        continue;
                    }

                    var itemText = ListItemText(listItem);
                    if (itemText.Length > 0)
                    {
                        lines.Add("• " + itemText);
                    }
                }

                break;
            case LeafBlock leaf when leaf.Inline is not null:
                var leafText = InlineText(leaf.Inline);
                if (leafText.Length > 0)
                {
                    lines.Add(leafText);
                }

                break;
        }
    }

    private static string ListItemText(ListItemBlock item)
    {
        var parts = new List<string>();
        foreach (var child in item)
        {
            if (child is LeafBlock { Inline: not null } leaf)
            {
                parts.Add(InlineText(leaf.Inline));
            }
        }

        return string.Join(" ", parts.Where(part => part.Length > 0));
    }

    private static string InlineText(ContainerInline? inline)
    {
        if (inline is null)
        {
            return "";
        }

        var sb = new System.Text.StringBuilder();
        Append(sb, inline);
        return sb.ToString().Trim();
    }

    private static void Append(System.Text.StringBuilder sb, ContainerInline inline)
    {
        foreach (var child in inline)
        {
            switch (child)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case AutolinkInline auto:
                    sb.Append(auto.Url);
                    break;
                case TaskList task:
                    sb.Append(task.Checked ? "[x] " : "[ ] ");
                    break;
                case LineBreakInline:
                    sb.Append(' ');
                    break;
                case ContainerInline container:
                    Append(sb, container);
                    break;
            }
        }
    }
}
