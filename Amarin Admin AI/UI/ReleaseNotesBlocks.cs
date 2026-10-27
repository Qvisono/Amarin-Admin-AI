using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Block = Markdig.Syntax.Block;
using Inline = System.Windows.Documents.Inline;

namespace Amarin.UI;

/// <summary>
/// Заметки к выпуску видом концепта «Занавес» (1.37.0): абзацы, пункты с короткой чертой вместо
/// точки, полужирное и код жёлтым на подложке, строка в полтора кегля.
/// </summary>
/// <remarks>
/// Не <see cref="ChatMarkdown"/>: тот рисует ответ модели — точки списков, отступы и шрифты ленты,
/// — а у окна «Что нового» свой рисунок. В заметках бывают только абзацы, списки, подзаголовки и
/// код, поэтому разбор Markdig обходится здесь целиком; цвета — ключами из ресурсов листа.
/// </remarks>
internal static class ReleaseNotesBlocks
{
    private const double FontSize = 12.5;
    private const double LineHeight = FontSize * 1.5;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    /// <summary>Блоки заметок цветом <paramref name="textKey"/> (ключ кисти в ресурсах листа).</summary>
    public static IEnumerable<FrameworkElement> Build(string markdown, string textKey)
    {
        var document = Markdown.Parse(markdown ?? "", Pipeline);
        foreach (var block in document)
        {
            foreach (var element in Block(block, textKey))
            {
                yield return element;
            }
        }
    }

    private static IEnumerable<FrameworkElement> Block(Block block, string textKey)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var title = Text(heading.Inline, "WhatsNew.Text1");
                title.FontWeight = FontWeights.SemiBold;
                title.Margin = new Thickness(0, 8, 0, 5);
                yield return title;
                break;

            case ListBlock list:
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    yield return Item(item, textKey);
                }

                break;

            case FencedCodeBlock or CodeBlock:
                var code = new TextBlock
                {
                    Text = ((LeafBlock)block).Lines.ToString(),
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 6)
                };
                code.SetResourceReference(TextBlock.FontFamilyProperty, "WhatsNew.Mono");
                code.SetResourceReference(TextBlock.ForegroundProperty, "WhatsNew.Code");
                yield return code;
                break;

            case ParagraphBlock paragraph:
                var text = Text(paragraph.Inline, textKey);
                text.Margin = new Thickness(0, 0, 0, 6);
                yield return text;
                break;

            case ContainerBlock container:
                foreach (var child in container)
                {
                    foreach (var element in Block(child, textKey))
                    {
                        yield return element;
                    }
                }

                break;
        }
    }

    /// <summary>Пункт (.notes li): черта 5×1,5 на трёх точках от края и на 0,74 кегля от верха строки.</summary>
    private static FrameworkElement Item(ListItemBlock item, string textKey)
    {
        var body = new StackPanel { Margin = new Thickness(15, 0, 0, 0) };
        foreach (var child in item)
        {
            foreach (var element in Block(child, textKey))
            {
                body.Children.Add(element);
            }
        }

        if (body.Children.Count > 0 && body.Children[^1] is FrameworkElement last)
        {
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        }

        var marker = new Rectangle
        {
            Width = 5,
            Height = 1.5,
            RadiusX = 1,
            RadiusY = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(3, FontSize * .74, 0, 0)
        };
        marker.SetResourceReference(Shape.FillProperty, "WhatsNew.Marker");

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.Children.Add(marker);
        grid.Children.Add(body);
        return grid;
    }

    private static TextBlock Text(ContainerInline? inlines, string textKey)
    {
        var text = new TextBlock
        {
            FontSize = FontSize,
            LineHeight = LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.Wrap
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, textKey);
        if (inlines is not null)
        {
            Add(text.Inlines, inlines);
        }

        return text;
    }

    private static void Add(InlineCollection target, ContainerInline source)
    {
        foreach (var inline in source)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;

                case CodeInline code:
                    target.Add(Code(code.Content));
                    break;

                case LineBreakInline lineBreak:
                    target.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                    break;

                case EmphasisInline emphasis:
                    var span = emphasis.DelimiterCount >= 2 ? (Span)new Bold() : new Italic();
                    Add(span.Inlines, emphasis);
                    target.Add(span);
                    break;

                case ContainerInline container:
                    // Ссылка и прочие обёртки — их текстом: окно заметок ничего не открывает.
                    Add(target, container);
                    break;
            }
        }
    }

    /// <summary>Код в строке (.notes code): моноширинный, жёлтый, на бледной подложке со скруглением.</summary>
    private static Inline Code(string content)
    {
        var text = new TextBlock { Text = content, FontSize = 11.5 };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "WhatsNew.Mono");
        text.SetResourceReference(TextBlock.ForegroundProperty, "WhatsNew.Code");
        var border = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 0), Child = text };
        border.SetResourceReference(Border.BackgroundProperty, "WhatsNew.CodeBack");
        return new InlineUIContainer(border) { BaselineAlignment = BaselineAlignment.Center };
    }
}
