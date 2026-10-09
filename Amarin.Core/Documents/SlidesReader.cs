using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Amarin.Core;

/// <summary>PowerPoint (.pptx) — слайдами: заголовок, текст фигур по порядку, таблицы и заметки докладчика.</summary>
internal static class SlidesReader
{
    public static DocumentContent Read(DocumentSource source, DocumentWindow window)
    {
        using var stream = source.OpenRead();
        PresentationDocument document;
        try
        {
            document = PresentationDocument.Open(stream, false);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException || (ex is IOException && ex is not FileNotFoundException))
        {
            throw new DocumentException(
                $"PowerPoint could not be read: {ex.Message}. It may be password-protected, damaged or an old .ppt renamed to .pptx.");
        }

        using (document)
        {
            var presentation = document.PresentationPart ?? throw new DocumentException("The PowerPoint file has no slides.");
            var ids = presentation.Presentation?.SlideIdList?.Elements<P.SlideId>().ToList() ?? [];
            var units = new List<DocumentUnit>(ids.Count);
            var number = 0;
            foreach (var id in ids)
            {
                number++;
                if (id.RelationshipId?.Value is not { } relation || presentation.GetPartById(relation) is not SlidePart slide)
                {
                    continue;
                }

                units.Add(new DocumentUnit(number, SlideText(slide)));
            }

            return new DocumentContent
            {
                Kind = DocumentKind.Slides,
                Unit = "slide",
                Sections = [UnitBudget.Slice(units, window, unit => unit.Text.Length + 20)],
                Summary = $"{ids.Count} slides"
            };
        }
    }

    private static string SlideText(SlidePart slide)
    {
        var text = new StringBuilder();
        var tree = slide.Slide?.CommonSlideData?.ShapeTree;
        if (tree is not null)
        {
            foreach (var shape in tree.Descendants<P.Shape>())
            {
                var body = Paragraphs(shape.TextBody);
                if (body.Length == 0)
                {
                    continue;
                }

                var placeholder = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value;
                var title = placeholder is { } type && (type == P.PlaceholderValues.Title || type == P.PlaceholderValues.CenteredTitle);
                text.Append(title ? "## " + body.Replace("\n", " ", StringComparison.Ordinal) : body).Append('\n');
            }

            foreach (var table in tree.Descendants<A.Table>())
            {
                foreach (var row in table.Elements<A.TableRow>())
                {
                    text.Append("| ")
                        .Append(string.Join(" | ", row.Elements<A.TableCell>().Select(cell => cell.InnerText.Replace("|", "\\|", StringComparison.Ordinal))))
                        .Append(" |\n");
                }
            }

            if (tree.Descendants<P.Picture>().Any())
            {
                text.Append("[image]\n");
            }
        }

        var notes = Paragraphs(slide.NotesSlidePart?.NotesSlide?.CommonSlideData?.ShapeTree?
            .Descendants<P.Shape>()
            .Where(shape => shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value == P.PlaceholderValues.Body)
            .Select(shape => shape.TextBody)
            .FirstOrDefault());
        if (notes.Length > 0)
        {
            text.Append("Notes: ").Append(notes.Replace("\n", " ", StringComparison.Ordinal)).Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    private static string Paragraphs(OpenXmlElement? body) =>
        body is null
            ? ""
            : string.Join("\n", body.Elements<A.Paragraph>()
                .Select(paragraph => string.Concat(paragraph.Descendants<A.Text>().Select(t => t.Text)).Trim())
                .Where(line => line.Length > 0));
}
