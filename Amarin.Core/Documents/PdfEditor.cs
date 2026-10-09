using System.Globalization;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace Amarin.Core;

/// <summary>Правка PDF: оставить выбранные страницы, дописать другие PDF в конец.</summary>
/// <remarks>
/// Текст внутри страниц PDF не правится: формат хранит буквы с координатами, а не абзацы, и
/// «замена слова» поехала бы вёрсткой. Для правки текста документ собирают заново
/// (<c>create_document</c>) по прочитанному.
/// </remarks>
internal static class PdfEditor
{
    /// <summary>Оставляет страницы по списку вида <c>1-3,7,10-</c>, в этом порядке.</summary>
    public static byte[] KeepPages(byte[] source, string pages)
    {
        var count = PageCount(source);
        var selected = ParsePages(pages, count);
        return PdfMerger.Merge([source], [selected]);
    }

    /// <summary>Дописывает в конец страницы других PDF.</summary>
    public static byte[] Append(byte[] source, IReadOnlyList<byte[]> others)
    {
        var all = new List<byte[]> { source };
        all.AddRange(others);
        return PdfMerger.Merge(all, [.. all.Select(bytes => (IReadOnlyList<int>)[.. Enumerable.Range(1, PageCount(bytes))])]);
    }

    public static int PageCount(byte[] bytes)
    {
        try
        {
            using var document = PdfDocument.Open(bytes);
            return document.NumberOfPages;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            throw new DocumentException($"PDF could not be read: {ex.Message}. It may be password-protected or damaged.");
        }
    }

    /// <summary>
    /// Разбирает «1-3,7,10-»: диапазоны включительно, «10-» — до конца, «-3» — с начала. Номера
    /// за пределами документа — отказ, а не молчаливый пропуск: модель должна знать, что ошиблась.
    /// </summary>
    internal static List<int> ParsePages(string pages, int count)
    {
        var selected = new List<int>();
        foreach (var part in (pages ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-', StringComparison.Ordinal);
            int from;
            int to;
            if (dash < 0)
            {
                from = to = Number(part);
            }
            else
            {
                from = dash == 0 ? 1 : Number(part[..dash]);
                to = dash == part.Length - 1 ? count : Number(part[(dash + 1)..]);
            }

            if (from < 1 || to > count || from > to)
            {
                throw new DocumentException($"Pages \"{part}\" are outside the document: it has {count} pages.");
            }

            selected.AddRange(Enumerable.Range(from, to - from + 1));
        }

        return selected.Count > 0
            ? selected
            : throw new DocumentException("No pages were given. Use a list like 1-3,7 or 10-.");

        static int Number(string text) =>
            int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : throw new DocumentException($"\"{text}\" is not a page number.");
    }
}
