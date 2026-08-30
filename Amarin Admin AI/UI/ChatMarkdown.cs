using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Amarin.UI;

internal static class ChatMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .Build();

    public static IReadOnlyList<string> PreviewLines(string text)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return lines;
        }

        var document = Markdown.Parse(text.Replace("\r\n", "\n"), Pipeline);
        foreach (var block in document)
        {
            AppendPreview(lines, block);
        }

        if (lines.Count == 0 && !string.IsNullOrWhiteSpace(text))
        {
            lines.Add(text.Trim());
        }

        return lines;
    }

    public static void Write(
        RichTextBox box,
        string text,
        bool markdown,
        Color foreground,
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

        if (string.IsNullOrEmpty(text))
        {
            document.Blocks.Add(CreateParagraph("", foreground, fontSize, lineHeight, last: true));
        }
        else if (!markdown)
        {
            var parts = text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                document.Blocks.Add(CreateParagraph(
                    parts[i], foreground, fontSize, lineHeight, last: i == parts.Length - 1));
            }
        }
        else
        {
            var parsed = Markdown.Parse(text.Replace("\r\n", "\n"), Pipeline);
            var blocks = parsed.ToList();
            if (blocks.Count == 0)
            {
                document.Blocks.Add(CreateParagraph(text, foreground, fontSize, lineHeight, last: true));
            }
            else
            {
                for (var i = 0; i < blocks.Count; i++)
                {
                    AddBlocks(document, blocks[i], foreground, fontSize, lineHeight, i == blocks.Count - 1);
                }
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

    private static void AppendPreview(List<string> lines, Markdig.Syntax.Block block)
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

    private static void AddBlocks(
        FlowDocument document,
        Markdig.Syntax.Block block,
        Color foreground,
        double fontSize,
        double lineHeight,
        bool last)
    {
        switch (block)
        {
            case ListBlock list:
                var items = list.OfType<ListItemBlock>().ToList();
                for (var i = 0; i < items.Count; i++)
                {
                    var itemText = ListItemText(items[i]);
                    document.Blocks.Add(CreateParagraph(
                        "• " + itemText,
                        foreground,
                        fontSize,
                        lineHeight,
                        last && i == items.Count - 1));
                }

                break;
            case ParagraphBlock paragraph:
                document.Blocks.Add(CreateParagraph(
                    InlineText(paragraph.Inline), foreground, fontSize, lineHeight, last));
                break;
            case HeadingBlock heading:
                document.Blocks.Add(CreateParagraph(
                    InlineText(heading.Inline), foreground, fontSize, lineHeight, last, bold: true));
                break;
            case CodeBlock code:
                document.Blocks.Add(CreateParagraph(
                    code.Lines.ToString().TrimEnd(), foreground, fontSize, lineHeight, last));
                break;
        }
    }

    private static Paragraph CreateParagraph(
        string text,
        Color foreground,
        double fontSize,
        double lineHeight,
        bool last,
        bool bold = false)
    {
        var paragraph = new Paragraph
        {
            Margin = last ? new Thickness(0) : new Thickness(0, 0, 0, 8),
            LineHeight = lineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = TextAlignment.Left
        };
        paragraph.Inlines.Add(new Run(text)
        {
            Foreground = new SolidColorBrush(foreground),
            FontSize = fontSize,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
        });
        return paragraph;
    }

    private static string ListItemText(ListItemBlock item)
    {
        var parts = new List<string>();
        foreach (var child in item)
        {
            if (child is ParagraphBlock paragraph)
            {
                parts.Add(InlineText(paragraph.Inline));
            }
            else if (child is LeafBlock leaf && leaf.Inline is not null)
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
                case LineBreakInline:
                    sb.Append(' ');
                    break;
                case EmphasisInline emphasis:
                    sb.Append(InlineText(emphasis));
                    break;
                case LinkInline link:
                    sb.Append(InlineText(link));
                    break;
                case ContainerInline container:
                    sb.Append(InlineText(container));
                    break;
            }
        }

        return sb.ToString().Trim();
    }
}
