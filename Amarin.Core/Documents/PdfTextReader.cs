using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Amarin.Core;

/// <summary>PDF — страницами текста в порядке чтения, а не в порядке, в каком буквы лежат в файле.</summary>
/// <remarks>
/// Достаются только страницы окна: разбор страницы — самое дорогое в чтении PDF, и книга на
/// триста страниц ради первых пяти разбиралась бы целиком. Страница без букв — скан: текста в ней
/// нет, и модель узнаёт об этом прямо, а не получает пустоту.
/// </remarks>
internal static class PdfTextReader
{
    public static DocumentContent Read(DocumentSource source, DocumentWindow window)
    {
        using var stream = source.OpenRead();
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(stream);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            throw new DocumentException(
                $"PDF could not be read: {ex.Message}. It may be password-protected or damaged.");
        }

        using (document)
        {
            var total = document.NumberOfPages;
            var budget = new UnitBudget(window);
            var shown = new List<DocumentUnit>();
            var blank = 0;
            for (var number = Math.Max(1, window.Offset); number <= total; number++)
            {
                string text;
                try
                {
                    text = ContentOrderTextExtractor.GetText(document.GetPage(number)).Trim();
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    text = $"[page could not be read: {ex.Message}]";
                }

                if (text.Length == 0)
                {
                    blank++;
                    text = "[no text on this page - it is an image or a scan]";
                }

                var room = budget.Left;
                if (!budget.TryTake(text.Length + 24))
                {
                    break;
                }

                shown.Add(new DocumentUnit(number, UnitBudget.Piece(text, number, room, window)));
            }

            var notes = new List<string>();
            if (shown.Count > 0 && blank == shown.Count)
            {
                notes.Add("These pages have no text layer: the PDF is a scan or pictures. Its text cannot be read here; say so instead of guessing the content.");
            }

            return new DocumentContent
            {
                Kind = DocumentKind.Pdf,
                Unit = "page",
                Sections = [new DocumentSection(null, shown, total, More: budget.More)],
                Summary = $"{total} pages",
                Notes = notes
            };
        }
    }

    /// <summary>
    /// Есть ли в PDF текстовый слой — по первым страницам. Нет — скан: его отдают провайдеру как
    /// есть, тот умеет распознавать картинки.
    /// </summary>
    public static bool HasText(DocumentSource source)
    {
        try
        {
            using var stream = source.OpenRead();
            using var document = PdfDocument.Open(stream);
            for (var number = 1; number <= Math.Min(3, document.NumberOfPages); number++)
            {
                if (document.GetPage(number).Letters.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            return false;
        }
    }
}
