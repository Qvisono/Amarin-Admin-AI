using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Amarin.Core;
using Amarin.Tools;
using Markdig;
using Markdig.Extensions.Mathematics;
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
    // Набор расширений собран вручную: UseAdvancedExtensions() тянет Diagrams, а он проглотит
    // ```mermaid целиком. Mathematics включён ради формул, но $var из PowerShell тоже попадает
    // под его правило — поэтому содержимое одинарных $ проходит через MathDetection, и всё,
    // что не похоже на математику, печатается как было, вместе с самими долларами.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseGridTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseListExtras()
        .UseAutoLinks()
        .UseMathematics()
        .Build();

    private const double BlockGap = 8;
    private const string BodyBrush = "Text.Secondary";

    /// <summary>
    /// Моноширинное семейство для инлайнового кода. Одно на программу: составное имя
    /// разбирается при создании, а таких Run-ов в ответе бывают десятки, и каждая перерисовка
    /// живого ответа создавала их заново.
    /// </summary>
    private static readonly FontFamily MonoFamily = new("Consolas, Cascadia Mono, Courier New");

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
    internal static bool HasBlockConstructs(string text) => Parse(text).HasBlockConstructs;

    /// <summary>
    /// Текст без inline-разметки — то, что реально увидит глаз. Нужен для замера ширины
    /// пузыря: «**жирный**» иначе намерит на четыре символа шире отрисованного.
    /// </summary>
    internal static string FlattenInline(string text) => Parse(text).FlatInline;

    /// <summary>Разбирает разметку один раз, чтобы задать ей потом несколько вопросов.</summary>
    /// <remarks>
    /// Пузырь пользователя спрашивал три вещи — есть ли блочная разметка, как текст выглядит без
    /// инлайновой и как его нарисовать, — и каждый вопрос гонял Markdig по всему тексту заново.
    /// </remarks>
    internal static ParsedMarkdown Parse(string? text) => ParsedMarkdown.Of(text);

    /// <summary>Разобранная разметка одного сообщения.</summary>
    internal sealed class ParsedMarkdown
    {
        private string? _flat;

        private ParsedMarkdown(string source, IReadOnlyList<MdBlock> blocks)
        {
            Source = source;
            Blocks = blocks;
            HasBlockConstructs = blocks.Any(block => block is not ParagraphBlock);
        }

        /// <summary>Текст, каким его дала модель, — без приведения формул к долларам.</summary>
        public string Source { get; }

        internal IReadOnlyList<MdBlock> Blocks { get; }

        public bool HasBlockConstructs { get; }

        public string FlatInline => _flat ??= Flatten();

        internal static ParsedMarkdown Of(string? text)
        {
            var source = text ?? "";
            return string.IsNullOrWhiteSpace(source)
                ? new ParsedMarkdown(source, [])
                : new ParsedMarkdown(source, [.. Markdown.Parse(Normalize(source), Pipeline)]);
        }

        private string Flatten()
        {
            if (string.IsNullOrWhiteSpace(Source))
            {
                return Source;
            }

            var lines = new List<string>(Blocks.Count);
            foreach (var block in Blocks)
            {
                if (block is not ParagraphBlock { Inline: not null } paragraph)
                {
                    return Source;
                }

                lines.Add(InlineText(paragraph.Inline));
            }

            return lines.Count == 0 ? Source : string.Join("\n", lines);
        }
    }

    /// <param name="box">Куда положить готовый документ.</param>
    /// <param name="host">
    /// Владелец ресурсов — окно. Стили вроде MsgActionButton лежат в Window.Resources,
    /// а сам <paramref name="box"/> в момент вызова ещё может быть вне дерева.
    /// </param>
    /// <param name="files">
    /// Файлы, которые инструменты этого ответа положили на диск. Путь, названный в тексте,
    /// заменяется карточкой файла прямо на своём месте.
    /// </param>
    /// <param name="streaming">
    /// Ответ ещё дописывается, и этот же документ пересоберут через десятые доли секунды.
    /// Отключает кэши, которые ключуются полным текстом блока: у растущего текста попаданий
    /// не бывает, зато его промежуточные состояния вытесняют оттуда всё полезное.
    /// </param>
    /// <returns>
    /// Файлы, для которых карточка встала в текст. Остальные показываются полосой под ответом —
    /// иначе файл, о котором модель написала, показывался бы дважды.
    /// </returns>
    public static IReadOnlyList<SavedFile> Write(
        RichTextBox box,
        FrameworkElement host,
        string text,
        double fontSize,
        double lineHeight,
        bool fillAvailableWidth = true,
        IReadOnlyList<SavedFile>? files = null,
        bool streaming = false) =>
        Write(box, host, Parse(text), fontSize, lineHeight, fillAvailableWidth, files, streaming);

    /// <inheritdoc cref="Write(RichTextBox, FrameworkElement, string, double, double, bool, IReadOnlyList{SavedFile}, bool)"/>
    public static IReadOnlyList<SavedFile> Write(
        RichTextBox box,
        FrameworkElement host,
        ParsedMarkdown parsed,
        double fontSize,
        double lineHeight,
        bool fillAvailableWidth = true,
        IReadOnlyList<SavedFile>? files = null,
        bool streaming = false)
    {
        ArgumentNullException.ThrowIfNull(parsed);

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

        var context = new RenderContext(host, fontSize, lineHeight, files ?? [], [], streaming);
        if (parsed.Blocks.Count == 0)
        {
            // Пусто или один пробельный текст: разметки нет, но пустой документ выглядел бы багом.
            document.Blocks.Add(BuildPlainParagraph(parsed.Source, context, last: true));
        }
        else
        {
            AddBlocks(document.Blocks, parsed.Blocks, context, first: true);
        }

        box.Document = document;
        if (fillAvailableWidth && box.ActualWidth > 1)
        {
            document.PageWidth = box.ActualWidth;
        }

        return context.Placed;
    }

    // Заодно приводит формулы к долларам: модели пишут их и как \(…\) с \[…\], а разметка
    // понимает только $. Пересчёт идёт до разбора, чтобы Markdig увидел уже готовые формулы.
    private static string Normalize(string text) =>
        MathDelimiterNormalizer.ToDollars(text.Replace("\r\n", "\n").Replace('\r', '\n'));

    /// <summary>Всё, что нужно знать при построении блоков: кегль, интерлиньяж и хозяин ресурсов.</summary>
    /// <param name="Files">Файлы, которые можно узнать по пути и заменить карточкой.</param>
    /// <param name="Placed">Те из них, чья карточка уже встала в текст. Заполняется по ходу.</param>
    /// <param name="Streaming">
    /// Ответ ещё дописывается. Всё, что кэшируется по полному тексту, при этом кэшировать нельзя:
    /// у растущего блока ключ меняется на каждой перерисовке.
    /// </param>
    private sealed record RenderContext(
        FrameworkElement Host,
        double FontSize,
        double LineHeight,
        IReadOnlyList<SavedFile> Files,
        List<SavedFile> Placed,
        bool Streaming);

    // ───────────────────────── Блоки ─────────────────────────

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

            // MathBlock наследуется от FencedCodeBlock, так что этот случай обязан идти первым:
            // иначе выключная формула уедет в блок кода.
            case MathBlock math:
                target.Add(BuildMathBlock(math.Lines.ToString(), context, last));
                break;

            // ```math и ```latex — ещё один способ, которым модели оформляют выключную формулу.
            case FencedCodeBlock fenced when IsMathFence(fenced.Info):
                target.Add(BuildMathBlock(fenced.Lines.ToString(), context, last));
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

            // «$$…$$» в одну строку разметка отдаёт как инлайн, а не как блок. Абзац, в котором
            // кроме такой формулы ничего нет, всё равно должен встать отдельной строкой.
            case ParagraphBlock { Inline: { } display } when IsLoneDisplayMath(display, out var latex):
                target.Add(BuildMathBlock(latex, context, last));
                break;

            case ParagraphBlock { Inline: { } inline } when HasPicture(inline) || HasSavedFile(inline, context):
                // Картинка в абзаце — это иллюстрация, а не текст. Абзац разрезается на части:
                // текст до, сама картинка, текст после. Иначе всё, что не осталось наедине с
                // картинкой, молча вырождалось в синюю ссылку.
                // Названный путь к скачанному файлу режет абзац по той же причине: карточке
                // место там, где о файле говорят, а не отдельной полосой в конце ответа.
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
        RelaxLineHeight(paragraph);
        return paragraph;
    }

    /// <summary>Выключная формула: своя строка, по центру колонки.</summary>
    private static WpfBlock BuildMathBlock(string latex, RenderContext context, bool last)
    {
        var formula = MathRenderer.Build(context.Host, latex, context.FontSize * 1.15, display: true, "Text.Primary");
        formula.HorizontalAlignment = HorizontalAlignment.Center;
        formula.Margin = new Thickness(0, 4, 0, 4);

        // Формула нарисована, а не набрана: без исходника цитата потеряла бы её целиком.
        QuoteSelection.SetSource(formula, new QuoteSource("$$", latex.Trim(), "$$", Block: true));

        return new BlockUIContainer(formula)
        {
            Margin = new Thickness(0, 0, 0, last ? 0 : BlockGap),
            LineHeight = double.NaN,
            LineStackingStrategy = LineStackingStrategy.MaxHeight
        };
    }

    private static bool IsMathFence(string? info) =>
        info is not null &&
        (info.Equals("math", StringComparison.OrdinalIgnoreCase) ||
         info.Equals("latex", StringComparison.OrdinalIgnoreCase) ||
         info.Equals("tex", StringComparison.OrdinalIgnoreCase));

    /// <summary>Абзац целиком состоит из одной формулы <c>$$…$$</c>.</summary>
    private static bool IsLoneDisplayMath(ContainerInline inline, out string latex)
    {
        latex = "";
        MathInline? found = null;

        foreach (var child in inline)
        {
            switch (child)
            {
                case MathInline { DelimiterCount: >= 2 } math when found is null:
                    found = math;
                    break;

                case LiteralInline literal when literal.Content.ToString().Trim().Length == 0:
                case LineBreakInline:
                    break;

                default:
                    return false;
            }
        }

        if (found is null)
        {
            return false;
        }

        latex = found.Content.ToString();
        return true;
    }

    /// <summary>
    /// Формула внутри строки. Не математику (цену, переменную оболочки) возвращаем как текст —
    /// вместе с долларами, которые её обрамляли.
    /// </summary>
    private static WpfInline BuildMathInline(MathInline math, RenderContext context)
    {
        var content = math.Content.ToString();
        var delimiters = new string(math.Delimiter, math.DelimiterCount);

        if (math.DelimiterCount < 2 && !MathDetection.LooksLikeMath(content))
        {
            return new Run(delimiters + content + delimiters);
        }

        var visual = MathRenderer.BuildVisual(context.Host, content, context.FontSize, display: false);

        // InlineUIContainer ставит на базовую линию нижний край элемента, а у формулы под ней
        // ещё есть свес. Базовую линию формулы строке сообщает BaselineOffset — его читает
        // InlineObjectRun.Format. Прежде коробку опускали отрицательным нижним отступом, но
        // тогда строка не знала о свесе: знаменатели дробей уходили под следующую строку,
        // и условие «(x ≠ −1)» с новой строки печаталось поверх них.
        visual.Element.Margin = new Thickness(1, 0, 1, 0);
        TextBlock.SetBaselineOffset(visual.Element, visual.Ascent);
        QuoteSelection.SetSource(visual.Element, new QuoteSource(delimiters, content, delimiters));

        return new InlineUIContainer(visual.Element)
        {
            BaselineAlignment = BaselineAlignment.Baseline
        };
    }

    private static WpfBlock BuildCode(string code, string? language, RenderContext context)
    {
        var body = code.TrimEnd('\n');

        // Блок кода из одного пути — это не код, а указание на файл: модели пишут так, когда
        // хотят, чтобы путь было видно и удобно скопировать.
        if (ChatMessageViews.MatchSavedFile(body, context.Files) is { } file)
        {
            return BuildSavedFileCard(file, context);
        }

        // chatMenu: правый клик по коду открывает меню ленты («Копировать», «Ответить»), а не
        // системное. Исходник — чтобы выделение, захватившее блок целиком, донесло код до
        // цитаты: сам блок лежит в документе элементом интерфейса, а не текстом.
        var view = CodeBlockView.Create(context.Host, body, language, cache: !context.Streaming, chatMenu: true);
        QuoteSelection.SetSource(view, new QuoteSource("```" + language?.Trim(), body, "```", Block: true));
        return new BlockUIContainer(view)
        {
            Margin = new Thickness(0)
        };
    }

    /// <summary>
    /// Карточка файла на месте названного пути. Тот же вид, что и у карточек под ответом, —
    /// разница только в отбивке: здесь она стоит строкой, а не в ряду соседок.
    /// </summary>
    private static WpfBlock BuildSavedFileCard(SavedFile file, RenderContext context)
    {
        if (!context.Placed.Contains(file))
        {
            context.Placed.Add(file);
        }

        var card = ChatMessageViews.CreateSavedFileCard(context.Host, file);
        card.Margin = new Thickness(0, 2, 0, 2);
        QuoteSelection.SetSource(card, new QuoteSource("", file.Path, "", Block: true));
        return new BlockUIContainer(card) { Margin = new Thickness(0, 0, 0, 8) };
    }

    /// <summary>Файл, названный этим куском кода в тексте, — или <c>null</c>.</summary>
    private static SavedFile? SavedFileOf(CodeInline code, RenderContext context) =>
        ChatMessageViews.MatchSavedFile(code.Content, context.Files);

    /// <summary>Назван ли в абзаце путь к файлу, который этот ответ положил на диск.</summary>
    private static bool HasSavedFile(ContainerInline inline, RenderContext context)
    {
        if (context.Files.Count == 0)
        {
            return false;
        }

        foreach (var child in inline)
        {
            if (child is CodeInline code && SavedFileOf(code, context) is not null)
            {
                return true;
            }
        }

        return false;
    }

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
            else if (child is CodeInline code && SavedFileOf(code, context) is { } file)
            {
                FlushText();
                produced.Add(BuildSavedFileCard(file, context));
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
        RelaxLineHeight(paragraph);
        return paragraph;
    }

    /// <summary>
    /// Абзац с формулой перестаёт держать строку постоянной высоты. Весь остальной текст
    /// набирается с жёстким интерлиньяжем — он ровнее, — но дробь выше строки, и на жёстком
    /// интерлиньяже она наезжает на соседние строки.
    /// </summary>
    private static void RelaxLineHeight(WpfParagraph paragraph)
    {
        if (!HasEmbeddedElement(paragraph.Inlines))
        {
            return;
        }

        paragraph.LineHeight = double.NaN;
        paragraph.LineStackingStrategy = LineStackingStrategy.MaxHeight;
    }

    // Формула бывает и внутри выделения или ссылки (**$x^2$**): смотреть только верхний
    // уровень абзаца значило оставить такой строке жёсткий интерлиньяж.
    private static bool HasEmbeddedElement(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case InlineUIContainer:
                    return true;

                case Span span when HasEmbeddedElement(span.Inlines):
                    return true;
            }
        }

        return false;
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

        RelaxLineHeight(paragraph);
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

    // ───────────────────────── Inline ─────────────────────────

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

            case MathInline math:
                target.Add(BuildMathInline(math, context));
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
            FontFamily = MonoFamily,
            FontSize = context.FontSize - 1
        };
        run.SetResourceReference(TextElement.ForegroundProperty, "Code.Inline");
        run.SetResourceReference(TextElement.BackgroundProperty, "Bg.Raised");

        // Выделенный целиком кусок кода уходит в цитату в обратных кавычках и без шпаций.
        QuoteSelection.SetSource(run, new QuoteSource("`", content, "`"));
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

    // ───────────────────────── Предпросмотр в списке чатов ─────────────────────────

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
            case MathBlock math:
                var formula = math.Lines.ToString().Trim();
                if (formula.Length > 0)
                {
                    lines.Add(formula);
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
                case MathInline math:
                    sb.Append(math.Content.ToString());
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
