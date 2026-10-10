using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Amarin.Core;

/// <summary>Word (.docx) из Markdown: заголовки стилями Word, настоящие списки и таблицы, ссылки и картинки.</summary>
/// <remarks>
/// Стилями, а не прямым оформлением: человек, открывший документ, меняет вид всех заголовков разом
/// и видит их в навигации Word, — документ выглядит сделанным руками, а не выгруженным.
/// </remarks>
internal static class WordWriter
{
    /// <summary>Создаёт документ. Файл пишется целиком во временный рядом и только потом встаёт на место.</summary>
    public static WordBuildReport Create(string path, string markdown, Func<string, byte[]?> images, bool overwrite = false)
    {
        var report = new WordBuildReport();
        DocumentFiles.WriteAtomically(path, stream =>
        {
            using var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);
            var main = document.AddMainDocumentPart();
            var body = new W.Body();
            main.Document = new W.Document(body);
            var styles = WordStyles.Ensure(main);
            var builder = new WordBuilder(main, images, report, styles);
            foreach (var element in builder.Build(MarkdownBlocks.Parse(markdown)))
            {
                body.Append(element);
            }

            body.Append(PageSetup());
            main.Document.Save();
        }, overwrite);
        return report;
    }

    /// <summary>A4, поля по 2 см — как у документа, созданного в Word с настройками по умолчанию для России.</summary>
    private static W.SectionProperties PageSetup() =>
        new(
            new W.PageSize { Width = 11906U, Height = 16838U },
            new W.PageMargin { Top = 1134, Bottom = 1134, Left = 1134U, Right = 1134U, Header = 709U, Footer = 709U, Gutter = 0U });
}

/// <summary>Что при сборке пришлось пропустить — модель скажет об этом человеку.</summary>
internal sealed class WordBuildReport
{
    public List<string> Skipped { get; } = [];
}

/// <summary>Блоки Markdown — в элементы Word: абзацы, списки, таблицы, картинки.</summary>
/// <remarks>Один и тот же для нового документа и для вставки в чужой (<c>WordEditor</c>).</remarks>
internal sealed class WordBuilder(MainDocumentPart main, Func<string, byte[]?> images, WordBuildReport report, WordStyleMap styles)
{
    /// <summary>Ширина текста страницы A4 с полями по 2 см, в EMU: шире картинка не встанет.</summary>
    private const long ContentWidthEmu = 6_120_000;

    // Номера рисунков в документе уникальны: Word отказывается открывать файл с повтором.
    private uint _pictureId = (main.Document?.Descendants<DW.DocProperties>().Select(p => p.Id?.Value ?? 0U).DefaultIfEmpty(0U).Max() ?? 0U) + 1;

    public List<OpenXmlElement> Build(IEnumerable<DocBlock> blocks)
    {
        var elements = new List<OpenXmlElement>();
        foreach (var block in blocks)
        {
            Add(elements, block, quote: false);
        }

        return elements;
    }

    private void Add(List<OpenXmlElement> elements, DocBlock block, bool quote)
    {
        switch (block)
        {
            case DocHeading heading:
                elements.Add(Paragraph(heading.Spans, styles["Heading" + Math.Min(heading.Level, 3).ToString(CultureInfo.InvariantCulture)]));
                break;
            case DocText text:
                elements.Add(Paragraph(text.Spans, quote ? styles[WordStyles.Quote] : null));
                break;
            case DocList list:
                AddList(elements, list, level: 0);
                break;
            case DocTable table:
                var blocks = MarkdownBlocks.ColumnBlocks(table, ColumnWidths(table), ContentWidthPoints);
                for (var b = 0; b < blocks.Count; b++)
                {
                    // Две таблицы подряд Word склеивает в одну: между блоками нужен абзац.
                    if (b > 0)
                    {
                        elements.Add(new W.Paragraph());
                    }

                    elements.Add(Table(blocks[b]));
                }

                break;
            case DocCode code:
                foreach (var line in code.Text.Split('\n'))
                {
                    elements.Add(new W.Paragraph(
                        new W.ParagraphProperties(new W.ParagraphStyleId { Val = styles[WordStyles.Code] }),
                        TextRun(line.Length == 0 ? " " : line, null)));
                }

                break;
            case DocQuote nested:
                foreach (var inner in nested.Blocks)
                {
                    Add(elements, inner, quote: true);
                }

                break;
            case DocRule:
                elements.Add(new W.Paragraph(new W.ParagraphProperties(
                    new W.ParagraphBorders(new W.BottomBorder { Val = W.BorderValues.Single, Size = 6U, Space = 1U, Color = "BFBFBF" }))));
                break;
            case DocPageBreak:
                elements.Add(new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page })));
                break;
            case DocImage image:
                if (Picture(image) is { } picture)
                {
                    elements.Add(picture);
                }

                break;
        }
    }

    private void AddList(List<OpenXmlElement> elements, DocList list, int level)
    {
        // Свой экземпляр нумерации на каждый список: второй нумерованный список в документе
        // начинается с единицы, а не продолжает первый.
        var numId = WordNumbering.NewList(main, list.Ordered, list.Start);
        foreach (var item in list.Items)
        {
            var first = true;
            foreach (var block in item)
            {
                switch (block)
                {
                    case DocText text when first:
                        elements.Add(ListParagraph(text.Spans, numId, level));
                        first = false;
                        break;
                    case DocList nested:
                        AddList(elements, nested, Math.Min(level + 1, 8));
                        break;
                    case DocText text:
                        var continuation = Paragraph(text.Spans, null);
                        continuation.ParagraphProperties ??= new W.ParagraphProperties();
                        continuation.ParagraphProperties.Indentation = new W.Indentation { Left = ((level + 1) * 360).ToString(CultureInfo.InvariantCulture) };
                        elements.Add(continuation);
                        break;
                    default:
                        Add(elements, block, quote: false);
                        break;
                }
            }

            if (first)
            {
                elements.Add(ListParagraph([], numId, level));
            }
        }
    }

    private W.Paragraph ListParagraph(IReadOnlyList<DocSpan> spans, int numId, int level)
    {
        var paragraph = Paragraph(spans, styles[WordStyles.ListParagraph]);
        (paragraph.ParagraphProperties ??= new W.ParagraphProperties()).NumberingProperties = new W.NumberingProperties(
            new W.NumberingLevelReference { Val = level },
            new W.NumberingId { Val = numId });
        return paragraph;
    }

    public W.Paragraph Paragraph(IReadOnlyList<DocSpan> spans, string? style)
    {
        var paragraph = new W.Paragraph();
        if (style is not null)
        {
            paragraph.ParagraphProperties = new W.ParagraphProperties(new W.ParagraphStyleId { Val = style });
        }

        foreach (var span in spans)
        {
            if (span.Link is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri))
            {
                var relation = main.AddHyperlinkRelationship(uri, isExternal: true);
                paragraph.Append(new W.Hyperlink(TextRun(span.Text, span with { Link = null }, hyperlink: true)) { Id = relation.Id });
            }
            else
            {
                paragraph.Append(TextRun(span.Text, span));
            }
        }

        return paragraph;
    }

    /// <summary>Кусок текста с начертанием; переводы строк внутри — разрывами строки, а не новыми абзацами.</summary>
    /// <remarks>
    /// Свойства — в порядке схемы (стиль, шрифт, жирный, курсив, зачёркнутый, заливка): шрифт после
    /// жирного Word считает повреждением и предлагает «восстановить» документ.
    /// </remarks>
    private W.Run TextRun(string text, DocSpan? style, bool hyperlink = false)
    {
        var run = new W.Run();
        var properties = new W.RunProperties();
        if (hyperlink)
        {
            properties.Append(new W.RunStyle { Val = styles[WordStyles.Hyperlink] });
        }

        if (style is { Code: true })
        {
            properties.Append(new W.RunFonts { Ascii = "Consolas", HighAnsi = "Consolas", ComplexScript = "Consolas" });
        }

        if (style is { Bold: true })
        {
            properties.Append(new W.Bold());
        }

        if (style is { Italic: true })
        {
            properties.Append(new W.Italic());
        }

        if (style is { Strike: true })
        {
            properties.Append(new W.Strike());
        }

        if (style is { Code: true })
        {
            properties.Append(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = "F2F2F2" });
        }

        if (properties.HasChildren)
        {
            run.Append(properties);
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                run.Append(new W.Break());
            }

            run.Append(new W.Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
        }

        return run;
    }

    /// <summary>Ширина текста страницы в пунктах: 9638 твипов между полями A4.</summary>
    private const double ContentWidthPoints = 9638 / 20.0;

    /// <summary>Уже этого столбец в Word не читается: буква на строку.</summary>
    private const double MinColumnPoints = 30;

    /// <summary>Прикидка ширины столбцов по самому длинному тексту — чтобы решить, помещается ли таблица.</summary>
    private static double[] ColumnWidths(DocTable table)
    {
        var columns = table.Rows.Count == 0 ? 0 : table.Rows.Max(row => row.Count);
        var widths = Enumerable.Repeat(MinColumnPoints, columns).ToArray();
        foreach (var row in table.Rows)
        {
            for (var c = 0; c < row.Count; c++)
            {
                widths[c] = Math.Min(ContentWidthPoints, Math.Max(widths[c], (MarkdownBlocks.PlainText(row[c]).Length * 6.0) + 11));
            }
        }

        return widths;
    }

    private W.Table Table(DocTable table)
    {
        var columns = table.Rows.Max(row => row.Count);
        var grid = new W.TableGrid();
        var width = 9638 / Math.Max(1, columns);
        for (var i = 0; i < columns; i++)
        {
            grid.Append(new W.GridColumn { Width = width.ToString(CultureInfo.InvariantCulture) });
        }

        var result = new W.Table(
            new W.TableProperties(
                new W.TableStyle { Val = styles[WordStyles.TableGrid] },
                new W.TableWidth { Type = W.TableWidthUnitValues.Pct, Width = "5000" },
                new W.TableLook { Val = "04A0", FirstRow = true, NoVerticalBand = true }),
            grid);

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var header = table.HasHeader && r == 0;
            var row = new W.TableRow();
            if (header)
            {
                row.Append(new W.TableRowProperties(new W.TableHeader()));
            }

            var cells = table.Rows[r];
            for (var c = 0; c < columns; c++)
            {
                var spans = c < cells.Count ? cells[c] : [];
                var paragraph = Paragraph(header ? [.. spans.Select(span => span with { Bold = true })] : spans, null);
                var properties = new W.TableCellProperties(new W.TableCellWidth { Type = W.TableWidthUnitValues.Dxa, Width = width.ToString(CultureInfo.InvariantCulture) });
                if (header)
                {
                    properties.Append(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = "F2F2F2" });
                }

                row.Append(new W.TableCell(properties, paragraph));
            }

            result.Append(row);
        }

        return result;
    }

    /// <summary>Картинка по ширине страницы, если шире; без потери пропорций.</summary>
    private W.Paragraph? Picture(DocImage image)
    {
        var bytes = images(image.Source);
        var format = bytes is null ? null : ImageFormats.Sniff(bytes);
        if (bytes is null || format is null)
        {
            report.Skipped.Add(bytes is null
                ? $"image not found: {image.Source}"
                : $"image {image.Source} is not PNG, JPEG, GIF or BMP");
            return null;
        }

        var (pixelWidth, pixelHeight) = ImageFormats.Size(bytes);
        var part = main.AddImagePart(format.Value.PartType);
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            part.FeedData(stream);
        }

        var cx = Math.Min(ContentWidthEmu, pixelWidth * 9525L);
        var cy = pixelWidth == 0 ? cx : cx * pixelHeight / pixelWidth;
        var id = _pictureId++;
        var name = string.IsNullOrWhiteSpace(image.Caption) ? "Picture " + id : image.Caption;
        var relation = main.GetIdOfPart(part);

        var inline = new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
            new DW.DocProperties { Id = id, Name = name, Description = image.Caption },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(
                        new PIC.NonVisualDrawingProperties { Id = 0U, Name = name },
                        new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = relation }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = cx, Cy = cy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        {
            DistanceFromTop = 0U,
            DistanceFromBottom = 0U,
            DistanceFromLeft = 0U,
            DistanceFromRight = 0U
        };

        return new W.Paragraph(
            new W.ParagraphProperties(new W.Justification { Val = W.JustificationValues.Center }),
            new W.Run(new W.Drawing(inline)));
    }
}
