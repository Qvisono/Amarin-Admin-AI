using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Amarin.Core;

/// <summary>
/// Слайды из блоков Markdown: заголовок H1/H2, <c>---</c> или <c>\pagebreak</c> начинают слайд,
/// остальное ложится в его тело. Один и тот же для новой презентации и для правки чужой.
/// </summary>
/// <remarks>
/// <para>
/// Заголовок и текст — заполнители раскладки, а не надписи: человек, открывший файл, меняет тему,
/// сбрасывает слайд и видит заголовки в структуре — презентация выглядит сделанной руками. У чужой
/// презентации берутся её же раскладки, поэтому новые слайды надевают её оформление.
/// </para>
/// <para>
/// Размер шрифта выбирается по объёму: PowerPoint ужимает переполненный текст только при правке,
/// а открытый файл показал бы текст, вылезший за слайд. Последний абзац «Notes: …» — заметки
/// докладчика: так их показывает чтение (<see cref="SlidesReader"/>), и модель пишет их тем же видом.
/// </para>
/// </remarks>
internal sealed class SlideBuilder(PresentationPart presentation, Func<string, byte[]?> images, SlidesBuildReport report)
{
    private const string NotesPrefix = "Notes:";

    private static readonly int[] BodySizes = [28, 24, 20, 18, 16, 14, 12, 10];

    private static readonly int[] TitleSizes = [40, 36, 32, 28, 24, 20];

    private const long ListIndent = 342_900;

    private readonly long _width = presentation.Presentation?.SlideSize?.Cx?.Value ?? SlidesTemplate.Width;

    private readonly long _height = presentation.Presentation?.SlideSize?.Cy?.Value ?? SlidesTemplate.Height;

    /// <summary>Строит слайды; в список слайдов презентации их ставит вызывающий.</summary>
    public List<SlidePart> Build(IReadOnlyList<DocBlock> blocks) =>
        [.. Split(blocks).Select(Slide)];

    /// <summary>Делит блоки на слайды.</summary>
    internal static List<SlideDraft> Split(IReadOnlyList<DocBlock> blocks)
    {
        var slides = new List<SlideDraft>();
        SlideDraft? current = null;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case DocHeading { Level: <= 2 } heading:
                    current = new SlideDraft(heading.Spans, heading.Level);
                    slides.Add(current);
                    break;
                case DocRule or DocPageBreak:
                    current = null;
                    break;
                default:
                    if (current is null)
                    {
                        current = new SlideDraft(null, 0);
                        slides.Add(current);
                    }

                    current.Body.Add(block);
                    break;
            }
        }

        foreach (var slide in slides)
        {
            if (slide.Body.Count > 0 && slide.Body[^1] is DocText last &&
                MarkdownBlocks.PlainText(last.Spans).TrimStart() is var text &&
                text.StartsWith(NotesPrefix, StringComparison.OrdinalIgnoreCase))
            {
                slide.Notes = text[NotesPrefix.Length..].Trim();
                slide.Body.RemoveAt(slide.Body.Count - 1);
            }
        }

        return slides;
    }

    private SlidePart Slide(SlideDraft draft)
    {
        var visuals = draft.Body.Where(block => block is DocTable or DocImage).ToList();
        var texts = Paragraphs(draft.Body.Where(block => block is not (DocTable or DocImage)), 0);

        // Титульный: заголовок первого уровня и разве что строка подзаголовка — как у слайда,
        // который человек начал бы с раскладки «Титульный».
        var titleLayout = draft.Level == 1 && visuals.Count == 0 && texts.Count <= 1 ? Layout(P.SlideLayoutValues.Title) : null;
        var layout = titleLayout ?? ContentLayout();
        var part = presentation.AddNewPart<SlidePart>();
        part.AddPart(layout);

        var tree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()));
        uint id = 2;

        if (draft.Title is { } title)
        {
            var area = Area(layout, IsTitle)
                ?? new Rect(_width / 20, _height / 19, _width * 9 / 10, _height / 5);
            var size = Fit([new Para(title, 0, Bullet.None)], area, TitleSizes);
            tree.Append(Placeholder(id++, "Title", titleLayout is not null ? P.PlaceholderValues.CenteredTitle : P.PlaceholderValues.Title, null, null,
                [Paragraph(part, new Para(title, 0, Bullet.None), size, plain: true)]));
        }

        if (titleLayout is not null)
        {
            if (texts.Count > 0)
            {
                var area = Area(layout, IsBody) ?? new Rect(_width / 8, _height / 2, _width * 3 / 4, _height / 4);
                var size = Fit(texts, area, BodySizes);
                tree.Append(Placeholder(id++, "Subtitle", P.PlaceholderValues.SubTitle, 1U, null, [.. texts.Select(text => Paragraph(part, text, size, plain: true))]));
            }
        }
        else
        {
            var body = Area(layout, IsBody) ?? new Rect(_width / 20, _height * 4 / 15, _width * 9 / 10, _height * 19 / 30);
            var visualTop = body.Y;
            if (texts.Count > 0)
            {
                // С таблицей или картинкой текст занимает верх, сколько ему нужно, но не больше половины.
                var textArea = visuals.Count == 0 ? body : body with { Height = Math.Min(body.Height / 2, Needed(texts, body.Width, 18)) };
                var size = Fit(texts, textArea, BodySizes);
                var paragraphs = texts.Select(text => Paragraph(part, text, size, plain: false)).ToList();
                Rect? explicitArea = visuals.Count == 0 ? null : textArea;
                tree.Append(HasPlaceholder(layout, 1U)
                    ? Placeholder(id++, "Content", null, 1U, explicitArea, paragraphs)
                    : TextBox(id++, textArea, paragraphs));
                visualTop = textArea.Y + textArea.Height;
            }

            if (visuals.Count > 0)
            {
                var slice = (body.Y + body.Height - visualTop) / visuals.Count;
                foreach (var visual in visuals)
                {
                    var area = new Rect(body.X, visualTop, body.Width, slice);
                    visualTop += slice;
                    switch (visual)
                    {
                        case DocTable table:
                            foreach (var element in Tables(table, area, ref id))
                            {
                                tree.Append(element);
                            }

                            break;
                        case DocImage image when Picture(part, image, area, id) is { } picture:
                            id++;
                            tree.Append(picture);
                            break;
                    }
                }
            }
        }

        part.Slide = new P.Slide(new P.CommonSlideData(tree), new P.ColorMapOverride(new A.MasterColorMapping()));
        if (draft.Notes is { Length: > 0 } notes)
        {
            AddNotes(part, notes);
        }

        part.Slide.Save();
        return part;
    }

    /// <summary>Абзацы тела слайда из текстовых блоков.</summary>
    private static List<Para> Paragraphs(IEnumerable<DocBlock> blocks, int level)
    {
        var paragraphs = new List<Para>();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case DocText text:
                    paragraphs.Add(new Para(text.Spans, level, Bullet.None));
                    break;
                case DocHeading heading:
                    paragraphs.Add(new Para([.. heading.Spans.Select(span => span with { Bold = true })], level, Bullet.None));
                    break;
                case DocList list:
                    foreach (var item in list.Items)
                    {
                        var first = true;
                        foreach (var inner in item)
                        {
                            if (first && inner is DocText text)
                            {
                                paragraphs.Add(new Para(text.Spans, level, list.Ordered ? Bullet.Number : Bullet.Dot, list.Start));
                                first = false;
                            }
                            else
                            {
                                paragraphs.AddRange(Paragraphs([inner], inner is DocList ? Math.Min(level + 1, 4) : level));
                            }
                        }
                    }

                    break;
                case DocCode code:
                    paragraphs.AddRange(code.Text.Split('\n').Select(line => new Para([new DocSpan(line.Length == 0 ? " " : line, Code: true)], level, Bullet.None)));
                    break;
                case DocQuote quote:
                    paragraphs.AddRange(Paragraphs(quote.Blocks, level).Select(para => para with { Spans = [.. para.Spans.Select(span => span with { Italic = true })] }));
                    break;
            }
        }

        return paragraphs;
    }

    /// <summary>Наибольший размер из списка, при котором текст влезает в область; не влез ни один — наименьший.</summary>
    private static int Fit(IReadOnlyList<Para> paragraphs, Rect area, int[] sizes)
    {
        foreach (var size in sizes)
        {
            if (Needed(paragraphs, area.Width, size) <= area.Height)
            {
                return size;
            }
        }

        return sizes[^1];
    }

    /// <summary>Высота текста в EMU при размере шрифта в пунктах — прикидка по средней ширине буквы.</summary>
    private static long Needed(IReadOnlyList<Para> paragraphs, long width, int size)
    {
        var points = 0.0;
        foreach (var paragraph in paragraphs)
        {
            var text = MarkdownBlocks.PlainText(paragraph.Spans);
            var widthPoints = (width - (paragraph.Level + (paragraph.Bullet == Bullet.None ? 0 : 1)) * ListIndent) / 12700.0 - 14;
            var letter = size * (paragraph.Spans.Any(span => span.Code) ? 0.6 : 0.52);
            var perLine = Math.Max(1, (int)(widthPoints / letter));
            var lines = text.Split('\n').Sum(line => Math.Max(1, (line.Length + perLine - 1) / perLine));
            points += lines * size * 1.2 + 6;
        }

        return (long)(points * 12700) + 91_440;
    }

    private static A.Paragraph Paragraph(SlidePart part, Para para, int size, bool plain)
    {
        var properties = new A.ParagraphProperties();
        if (!plain)
        {
            properties.Level = para.Level;
            switch (para.Bullet)
            {
                case Bullet.None:
                    properties.LeftMargin = (int)(para.Level * ListIndent);
                    properties.Indent = 0;
                    properties.Append(new A.NoBullet());
                    break;
                case Bullet.Dot:
                    properties.LeftMargin = (int)((para.Level + 1) * ListIndent);
                    properties.Indent = (int)(-ListIndent / 2 - 57_150);
                    properties.Append(new A.CharacterBullet { Char = "•" });
                    break;
                case Bullet.Number:
                    properties.LeftMargin = (int)((para.Level + 1) * ListIndent);
                    properties.Indent = (int)-ListIndent;
                    properties.Append(new A.AutoNumberedBullet { Type = A.TextAutoNumberSchemeValues.ArabicPeriod, StartAt = Math.Max(1, para.Start) });
                    break;
            }
        }

        var paragraph = new A.Paragraph(properties);
        foreach (var span in para.Spans)
        {
            var lines = span.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    paragraph.Append(new A.Break(Run(part, span, size)));
                }

                if (lines[i].Length > 0)
                {
                    paragraph.Append(new A.Run(Run(part, span, size), new A.Text(lines[i])));
                }
            }
        }

        paragraph.Append(new A.EndParagraphRunProperties { FontSize = size * 100 });
        return paragraph;
    }

    /// <summary>Начертание куска; дети — в порядке схемы: заливка, шрифт, ссылка.</summary>
    private static A.RunProperties Run(SlidePart? part, DocSpan span, int size, bool inverse = false)
    {
        var properties = new A.RunProperties { FontSize = size * 100, Dirty = false };
        if (span.Bold)
        {
            properties.Bold = true;
        }

        if (span.Italic)
        {
            properties.Italic = true;
        }

        if (span.Strike)
        {
            properties.Strike = A.TextStrikeValues.SingleStrike;
        }

        if (inverse)
        {
            properties.Append(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Background1 }));
        }

        if (span.Code)
        {
            properties.Append(new A.LatinFont { Typeface = "Consolas" });
        }

        if (part is not null && span.Link is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            var relation = part.AddHyperlinkRelationship(uri, isExternal: true);
            properties.Append(new A.HyperlinkOnClick { Id = relation.Id });
        }

        return properties;
    }

    private static P.Shape Placeholder(uint id, string name, P.PlaceholderValues? type, uint? index, Rect? area, IEnumerable<A.Paragraph> paragraphs)
    {
        var placeholder = new P.PlaceholderShape();
        if (type is { } kind)
        {
            placeholder.Type = kind;
        }

        if (index is { } number)
        {
            placeholder.Index = number;
        }

        var shapeProperties = new P.ShapeProperties();
        if (area is { } rect)
        {
            shapeProperties.Append(Transform(rect));
        }

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name + " " + id },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties(placeholder)),
            shapeProperties,
            Body(new A.BodyProperties(new A.NormalAutoFit()), paragraphs));
    }

    private static P.TextBody Body(A.BodyProperties properties, IEnumerable<A.Paragraph> paragraphs)
    {
        var body = new P.TextBody(properties, new A.ListStyle());
        body.Append(paragraphs);
        return body;
    }

    /// <summary>Надпись — когда в раскладке чужой презентации нет заполнителя для текста.</summary>
    private static P.Shape TextBox(uint id, Rect area, IEnumerable<A.Paragraph> paragraphs) =>
        new(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "TextBox " + id },
                new P.NonVisualShapeDrawingProperties { TextBox = true },
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(Transform(area), new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }, new A.NoFill()),
            Body(new A.BodyProperties(new A.NormalAutoFit()) { Wrap = A.TextWrappingValues.Square }, paragraphs));

    private static A.Transform2D Transform(Rect area) =>
        new(new A.Offset { X = area.X, Y = area.Y }, new A.Extents { Cx = area.Width, Cy = area.Height });

    /// <summary>Таблица во всю ширину; шире слайда — блоками столбцов, как в Word и PDF.</summary>
    private static IEnumerable<P.GraphicFrame> Tables(DocTable table, Rect area, ref uint id)
    {
        var widthPoints = area.Width / 12700.0;
        var columns = table.Rows.Count == 0 ? 0 : table.Rows.Max(row => row.Count);
        var widths = Enumerable.Repeat(40.0, columns).ToArray();
        foreach (var row in table.Rows)
        {
            for (var c = 0; c < row.Count; c++)
            {
                widths[c] = Math.Min(widthPoints, Math.Max(widths[c], MarkdownBlocks.PlainText(row[c]).Length * 7.0 + 14));
            }
        }

        var blocks = MarkdownBlocks.ColumnBlocks(table, widths, widthPoints);
        var frames = new List<P.GraphicFrame>();
        var slice = area.Height / Math.Max(1, blocks.Count);
        var top = area.Y;
        foreach (var block in blocks)
        {
            frames.Add(Table(block, new Rect(area.X, top, area.Width, slice), id++));
            top += slice;
        }

        return frames;
    }

    private static P.GraphicFrame Table(DocTable table, Rect area, uint id)
    {
        var columns = Math.Max(1, table.Rows.Max(row => row.Count));
        var rows = Math.Max(1, table.Rows.Count);
        var rowHeight = Math.Min(area.Height / rows, 457_200L);
        var size = (int)Math.Clamp(rowHeight / 12700.0 * 0.45, 8, 18);
        var columnWidth = area.Width / columns;

        var grid = new A.TableGrid();
        for (var c = 0; c < columns; c++)
        {
            grid.Append(new A.GridColumn { Width = columnWidth });
        }

        var result = new A.Table(new A.TableProperties { FirstRow = true, BandRow = true }, grid);
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var header = table.HasHeader && r == 0;
            var row = new A.TableRow { Height = rowHeight };
            for (var c = 0; c < columns; c++)
            {
                var spans = c < table.Rows[r].Count ? table.Rows[r][c] : [];
                var paragraph = new A.Paragraph();
                foreach (var span in spans)
                {
                    paragraph.Append(new A.Run(Run(null, span with { Bold = span.Bold || header, Link = null }, size, inverse: header), new A.Text(span.Text.Replace('\n', ' '))));
                }

                paragraph.Append(new A.EndParagraphRunProperties { FontSize = size * 100 });
                row.Append(new A.TableCell(
                    new A.TextBody(new A.BodyProperties(), new A.ListStyle(), paragraph),
                    CellProperties(header)));
            }

            result.Append(row);
        }

        return new P.GraphicFrame(
            new P.NonVisualGraphicFrameProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "Table " + id },
                new P.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.Transform(new A.Offset { X = area.X, Y = area.Y }, new A.Extents { Cx = area.Width, Cy = rowHeight * rows }),
            new A.Graphic(new A.GraphicData(result) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/table" }));
    }

    /// <summary>Тонкие серые границы со всех сторон; шапка залита цветом темы. Дети — в порядке схемы.</summary>
    private static A.TableCellProperties CellProperties(bool header)
    {
        static T Line<T>() where T : A.LinePropertiesType, new()
        {
            var line = new T { Width = 9525, CompoundLineType = A.CompoundLineValues.Single };
            line.Append(new A.SolidFill(new A.RgbColorModelHex { Val = "BFBFBF" }));
            return line;
        }

        var properties = new A.TableCellProperties(
            Line<A.LeftBorderLineProperties>(),
            Line<A.RightBorderLineProperties>(),
            Line<A.TopBorderLineProperties>(),
            Line<A.BottomBorderLineProperties>());
        properties.Append(header
            ? new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 })
            : new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Background1 }));
        return properties;
    }

    /// <summary>Картинка вписана в область без искажения и стоит по центру.</summary>
    private P.Picture? Picture(SlidePart part, DocImage image, Rect area, uint id)
    {
        var bytes = images(image.Source);
        var format = bytes is null ? null : ImageFormats.Sniff(bytes);
        if (bytes is null || format is null)
        {
            report.Skipped.Add(bytes is null ? $"image not found: {image.Source}" : $"image {image.Source} is not PNG, JPEG, GIF or BMP");
            return null;
        }

        var (pixelWidth, pixelHeight) = ImageFormats.Size(bytes);
        var imagePart = part.AddImagePart(format.Value.PartType);
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            imagePart.FeedData(stream);
        }

        var natural = (Width: Math.Max(1L, pixelWidth * 9525L), Height: Math.Max(1L, pixelHeight * 9525L));
        var scale = Math.Min(1.0, Math.Min((double)area.Width / natural.Width, (double)area.Height / natural.Height));
        var cx = (long)(natural.Width * scale);
        var cy = (long)(natural.Height * scale);
        var name = string.IsNullOrWhiteSpace(image.Caption) ? "Picture " + id : image.Caption;
        return new P.Picture(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name, Description = image.Caption },
                new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(new A.Blip { Embed = part.GetIdOfPart(imagePart) }, new A.Stretch(new A.FillRectangle())),
            new P.ShapeProperties(
                Transform(new Rect(area.X + (area.Width - cx) / 2, area.Y + (area.Height - cy) / 2, cx, cy)),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
    }

    private void AddNotes(SlidePart part, string notes)
    {
        var master = SlidesTemplate.EnsureNotesMaster(presentation);
        var notesPart = part.AddNewPart<NotesSlidePart>();
        notesPart.AddPart(master);
        notesPart.AddPart(part);
        notesPart.NotesSlide = new P.NotesSlide(
            new P.CommonSlideData(new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(new A.TransformGroup()),
                new P.Shape(
                    new P.NonVisualShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 2U, Name = "Slide Image 1" },
                        new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true, NoRotation = true, NoChangeAspect = true }),
                        new P.ApplicationNonVisualDrawingProperties(new P.PlaceholderShape { Type = P.PlaceholderValues.SlideImage })),
                    new P.ShapeProperties()),
                new P.Shape(
                    new P.NonVisualShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 3U, Name = "Notes 2" },
                        new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                        new P.ApplicationNonVisualDrawingProperties(new P.PlaceholderShape { Type = P.PlaceholderValues.Body, Index = 1U })),
                    new P.ShapeProperties(),
                    Body(new A.BodyProperties(), notes.Split('\n').Select(line => new A.Paragraph(new A.Run(new A.RunProperties { Dirty = false }, new A.Text(line)))))))),
            new P.ColorMapOverride(new A.MasterColorMapping()));
        notesPart.NotesSlide.Save();
    }

    private SlideLayoutPart? Layout(P.SlideLayoutValues type) =>
        presentation.SlideMasterParts.SelectMany(master => master.SlideLayoutParts)
            .FirstOrDefault(layout => layout.SlideLayout?.Type?.Value == type);

    /// <summary>Раскладка для слайда с текстом: «Заголовок и объект», иначе любая с заполнителем тела, иначе первая.</summary>
    private SlideLayoutPart ContentLayout()
    {
        var layouts = presentation.SlideMasterParts.SelectMany(master => master.SlideLayoutParts).ToList();
        return Layout(P.SlideLayoutValues.Object)
            ?? Layout(P.SlideLayoutValues.Text)
            ?? layouts.FirstOrDefault(layout => HasPlaceholder(layout, 1U))
            ?? layouts.FirstOrDefault()
            ?? throw new DocumentException("The PowerPoint file has no slide layouts to build a slide on.");
    }

    private static bool HasPlaceholder(SlideLayoutPart layout, uint index) =>
        Placeholders(layout.SlideLayout?.CommonSlideData).Any(item => item.Placeholder.Index?.Value == index);

    /// <summary>Где на слайде стоит заполнитель раскладки: у раскладки, иначе у её мастера.</summary>
    private static Rect? Area(SlideLayoutPart layout, Func<P.PlaceholderShape, bool> match)
    {
        return Find(layout.SlideLayout?.CommonSlideData) ?? Find(layout.SlideMasterPart?.SlideMaster?.CommonSlideData);

        Rect? Find(P.CommonSlideData? data)
        {
            foreach (var (shape, placeholder) in Placeholders(data))
            {
                if (match(placeholder) && shape.ShapeProperties?.Transform2D is { Offset: { } offset, Extents: { } extents })
                {
                    return new Rect(offset.X?.Value ?? 0, offset.Y?.Value ?? 0, extents.Cx?.Value ?? 0, extents.Cy?.Value ?? 0);
                }
            }

            return null;
        }
    }

    private static bool IsTitle(P.PlaceholderShape placeholder) =>
        placeholder.Type?.Value == P.PlaceholderValues.Title || placeholder.Type?.Value == P.PlaceholderValues.CenteredTitle;

    private static bool IsBody(P.PlaceholderShape placeholder) =>
        placeholder.Index?.Value == 1U || placeholder.Type?.Value == P.PlaceholderValues.Body || placeholder.Type?.Value == P.PlaceholderValues.SubTitle;

    private static IEnumerable<(P.Shape Shape, P.PlaceholderShape Placeholder)> Placeholders(P.CommonSlideData? data)
    {
        foreach (var shape in data?.ShapeTree?.Elements<P.Shape>() ?? [])
        {
            if (shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape is { } placeholder)
            {
                yield return (shape, placeholder);
            }
        }
    }

    internal sealed class SlideDraft(IReadOnlyList<DocSpan>? title, int level)
    {
        public IReadOnlyList<DocSpan>? Title { get; } = title;

        public int Level { get; } = level;

        public List<DocBlock> Body { get; } = [];

        public string? Notes { get; set; }
    }

    private enum Bullet
    {
        None,
        Dot,
        Number
    }

    private sealed record Para(IReadOnlyList<DocSpan> Spans, int Level, Bullet Bullet, int Start = 1);

    private readonly record struct Rect(long X, long Y, long Width, long Height);
}

/// <summary>Что при сборке слайдов пришлось пропустить — модель скажет об этом человеку.</summary>
internal sealed class SlidesBuildReport
{
    public List<string> Skipped { get; } = [];
}
