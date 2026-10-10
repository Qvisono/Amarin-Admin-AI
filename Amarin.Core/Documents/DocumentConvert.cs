using System.Text;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Amarin.Core;

/// <summary>
/// Документ в Markdown — чтобы сделать из него документ другого формата (<c>create_document</c> с
/// <c>source</c>): Word, Excel с посчитанными значениями, PDF, PowerPoint, CSV или текст.
/// </summary>
/// <remarks>
/// Ради «сделай PDF из этой таблицы»: модель не перепечатывает документ в ответе (на большой таблице
/// это невозможно и чревато ошибками), а программа переносит его сама. До 1.33.0 этого не было, и
/// модель советовала человеку открыть файл в Excel и сохранить PDF вручную.
/// </remarks>
internal static class DocumentConvert
{
    /// <summary>Окно чтения без предела: переводится документ целиком, а не его начало.</summary>
    private static readonly DocumentWindow Whole = new(Budget: int.MaxValue / 2);

    public static string ToMarkdown(DocumentSource source, string? sheet)
    {
        switch (DocumentKinds.Of(source))
        {
            case DocumentKind.Excel:
                return ExcelMarkdown(source, sheet);
            case DocumentKind.Word:
                return Joined(WordReader.Read(source, Whole), "\n\n");
            case DocumentKind.Pdf:
                return Joined(PdfTextReader.Read(source, Whole), "\n\n\\pagebreak\n\n");
            case DocumentKind.Slides:
                var slides = SlidesReader.Read(source, Whole);
                return string.Join("\n\n", slides.Sections.SelectMany(section => section.Units)
                    .Select(unit => $"## {unit.Number}\n\n{unit.Text.Trim()}"));
            case DocumentKind.Text:
                var text = Encoding.UTF8.GetString(source.ReadAllBytes()).TrimStart('﻿');
                var extension = Path.GetExtension(source.Path).ToLowerInvariant();
                return extension is ".csv" or ".tsv" ? Table(ExcelWriter.FromDelimited(text).Rows.Select(row => row.Select(cell => cell.Text).ToList()).ToList()) : text;
            default:
                throw new DocumentException($"{Path.GetExtension(source.Path)} cannot be turned into another document here. Word, Excel, PDF, PowerPoint, CSV and text files can.");
        }
    }

    private static string Joined(DocumentContent content, string separator) =>
        string.Join(separator, content.Sections.SelectMany(section => section.Units).Select(unit => unit.Text.Trim()).Where(text => text.Length > 0));

    /// <summary>
    /// Листы книги таблицами Markdown — со значениями, а не формулами. Итог, которого в файле нет,
    /// считается здесь (<see cref="ExcelCalc"/>); посчитать нельзя — отказ с тем, что делать: таблица
    /// с пропусками на месте чисел хуже, чем никакой.
    /// </summary>
    private static string ExcelMarkdown(DocumentSource source, string? sheet)
    {
        using var stream = source.OpenRead();
        using var document = ExcelReader.Open(stream);
        var workbook = document.WorkbookPart ?? throw new DocumentException("The Excel file has no workbook.");
        var listed = workbook.Workbook?.Sheets?.Elements<S.Sheet>().ToList() ?? [];
        var chosen = ExcelReader.Select(listed, sheet)
            .Where(item => sheet is not null || item.State?.Value != S.SheetStateValues.Hidden)
            .Select(item => item.Name?.Value ?? "")
            .ToList();
        var grid = ExcelGrid.Load(workbook);
        var calc = grid.Calc();
        var shown = new ExcelReader.ExcelContext(workbook);
        var output = new StringBuilder();
        foreach (var name in chosen)
        {
            var rows = new SortedDictionary<int, SortedDictionary<int, string>>();
            var unknown = 0;
            string? example = null;
            foreach (var ((column, row), entry) in grid.Cells(name))
            {
                var text = Shown(entry, name, column, row, calc, shown);
                if (text is null)
                {
                    unknown++;
                    example ??= entry.Cell.CellFormula?.Text;
                    continue;
                }

                if (text.Length > 0)
                {
                    if (!rows.TryGetValue(row, out var line))
                    {
                        rows[row] = line = [];
                    }

                    line[column] = text;
                }
            }

            if (unknown > 0)
            {
                throw new DocumentException(
                    $"{unknown} cell(s) of sheet \"{name}\" hold formulas whose results were never saved in the file and that this program cannot calculate (=" +
                    $"{example}). Rebuild that table with plain formulas and fill (edit_document), or have the agent recalculate the file in Excel, then convert it again.");
            }

            if (rows.Count == 0)
            {
                continue;
            }

            var first = rows.Values.Min(line => line.Keys.Min());
            var last = rows.Values.Max(line => line.Keys.Max());
            var table = rows.Values
                .Select(line => (IReadOnlyList<string>)[.. Enumerable.Range(first, last - first + 1).Select(column => line.GetValueOrDefault(column, ""))])
                .ToList();
            if (chosen.Count > 1)
            {
                output.Append("## ").Append(name).Append("\n\n");
            }

            output.Append(Table(table)).Append("\n\n");
        }

        return output.Length > 0 ? output.ToString().TrimEnd() : throw new DocumentException("The workbook has no values to convert.");
    }

    /// <summary>Что видно в ячейке: сохранённое значение, а у формулы без итога — посчитанное. Null — посчитать нельзя.</summary>
    private static string? Shown(ExcelGrid.Entry entry, string sheet, int column, int row, ExcelCalc calc, ExcelReader.ExcelContext shown)
    {
        var formula = entry.Source.Formula is not null || entry.Source.Opaque;
        if (!formula || entry.Cell.CellValue is { Text.Length: > 0 })
        {
            return shown.Value(entry.Cell);
        }

        return calc.TryValue(sheet, column, row) is { } value ? ExcelCalc.ToText(value) : null;
    }

    /// <summary>Таблица Markdown: первая строка — шапка; «|» и переносы внутри ячеек не ломают разметку.</summary>
    private static string Table(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0)
        {
            return "";
        }

        var width = rows.Max(row => row.Count);
        var text = new StringBuilder();
        for (var r = 0; r < rows.Count; r++)
        {
            text.Append('|');
            for (var c = 0; c < width; c++)
            {
                var cell = c < rows[r].Count ? rows[r][c] : "";
                text.Append(' ').Append(cell.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ')).Append(" |");
            }

            text.Append('\n');
            if (r == 0)
            {
                text.Append('|').Append(string.Concat(Enumerable.Repeat("---|", width))).Append('\n');
            }
        }

        return text.ToString();
    }
}
