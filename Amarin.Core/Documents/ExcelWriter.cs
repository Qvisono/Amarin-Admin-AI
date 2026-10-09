using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Amarin.Core;

/// <summary>Лист новой книги: имя и строки значений; первая строка — шапка, если так сказано.</summary>
internal sealed record SheetInput(string Name, IReadOnlyList<IReadOnlyList<CellInput>> Rows, bool Header = true);

/// <summary>
/// Excel (.xlsx) из таблиц: типы значений, жирная закреплённая шапка с фильтром и ширина столбцов
/// по содержимому — книга, с которой можно сразу работать, а не приводить её в порядок.
/// </summary>
internal static partial class ExcelWriter
{
    public static void Create(string path, IReadOnlyList<SheetInput> sheets, bool overwrite = false)
    {
        if (sheets.Count == 0)
        {
            throw new DocumentException("Nothing to write: give at least one sheet with rows.");
        }

        DocumentFiles.WriteAtomically(path, stream =>
        {
            using var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook);
            var workbook = document.AddWorkbookPart();
            var list = new S.Sheets();
            workbook.Workbook = new S.Workbook(list);
            var styles = ExcelStyles.Ensure(workbook);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var id = 1U;
            var formulas = false;
            foreach (var sheet in sheets)
            {
                var name = UniqueName(SafeName(sheet.Name, id), names);
                var part = workbook.AddNewPart<WorksheetPart>();
                part.Worksheet = Sheet(sheet, styles, out var hasFormulas);
                formulas |= hasFormulas;
                list.Append(new S.Sheet { Id = workbook.GetIdOfPart(part), SheetId = id++, Name = name });
            }

            if (formulas)
            {
                ExcelCells.RecalculateOnOpen(workbook);
            }

            workbook.Workbook.Save();
        }, overwrite);
    }

    /// <summary>Лист целиком: столбцы по ширине содержимого, закреплённая шапка и автофильтр по ней.</summary>
    internal static S.Worksheet Sheet(SheetInput sheet, ExcelStyleIds styles, out bool formulas)
    {
        formulas = false;
        var data = new S.SheetData();
        var widths = new Dictionary<int, int>();
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var header = sheet.Header && r == 0;
            for (var c = 0; c < row.Count; c++)
            {
                var input = row[c];
                if (input.Kind == CellInputKind.Empty)
                {
                    continue;
                }

                formulas |= input.Kind == CellInputKind.Formula;
                ExcelCells.Set(data, c + 1, r + 1, input, styles, bold: header);
                var length = input.Kind == CellInputKind.Formula ? 10 : input.Text.Length;
                widths[c + 1] = Math.Max(widths.GetValueOrDefault(c + 1), length);
            }
        }

        var worksheet = new S.Worksheet();
        var columns = sheet.Rows.Count == 0 ? 0 : sheet.Rows.Max(row => row.Count);
        if (sheet.Header && sheet.Rows.Count > 1 && columns > 0)
        {
            worksheet.Append(new S.SheetViews(new S.SheetView(
                new S.Pane { VerticalSplit = 1D, TopLeftCell = "A2", ActivePane = S.PaneValues.BottomLeft, State = S.PaneStateValues.Frozen })
            { WorkbookViewId = 0U }));
        }

        if (widths.Count > 0)
        {
            var cols = new S.Columns();
            foreach (var (column, length) in widths.OrderBy(pair => pair.Key))
            {
                cols.Append(new S.Column
                {
                    Min = (uint)column,
                    Max = (uint)column,
                    Width = Math.Clamp(length + 2, 8, 60),
                    CustomWidth = true
                });
            }

            worksheet.Append(cols);
        }

        worksheet.Append(data);
        if (sheet.Header && sheet.Rows.Count > 1 && columns > 0)
        {
            worksheet.Append(new S.AutoFilter { Reference = "A1:" + CellAddress.Of(columns, sheet.Rows.Count) });
        }

        return worksheet;
    }

    /// <summary>Имя листа: не длиннее 31 знака и без <c>[]:*?/\</c> — Excel иначе файл не откроет.</summary>
    internal static string SafeName(string? name, uint index)
    {
        var clean = SheetNameForbidden().Replace(name ?? "", " ").Trim().Trim('\'');
        if (clean.Length == 0)
        {
            clean = "Sheet" + index.ToString(CultureInfo.InvariantCulture);
        }

        return clean.Length <= 31 ? clean : clean[..31];
    }

    private static string UniqueName(string name, HashSet<string> taken)
    {
        var candidate = name;
        for (var n = 2; !taken.Add(candidate); n++)
        {
            var suffix = " (" + n.ToString(CultureInfo.InvariantCulture) + ")";
            candidate = (name.Length + suffix.Length <= 31 ? name : name[..(31 - suffix.Length)]) + suffix;
        }

        return candidate;
    }

    [GeneratedRegex(@"[\[\]:*?/\\]")]
    private static partial Regex SheetNameForbidden();

    /// <summary>
    /// Таблицы Markdown — листами: каждая таблица своим листом, а заголовок прямо над таблицей —
    /// именем листа. Таблиц нет — строки текста через запятую, точку с запятой или табуляцию.
    /// </summary>
    public static List<SheetInput> FromMarkdown(string markdown)
    {
        var sheets = new List<SheetInput>();
        string? pendingName = null;
        foreach (var block in MarkdownBlocks.Parse(markdown))
        {
            switch (block)
            {
                case DocHeading heading:
                    pendingName = MarkdownBlocks.PlainText(heading.Spans);
                    break;
                case DocTable table:
                    sheets.Add(new SheetInput(
                        pendingName ?? "Sheet" + (sheets.Count + 1).ToString(CultureInfo.InvariantCulture),
                        [.. table.Rows.Select(row => (IReadOnlyList<CellInput>)[.. row.Select(cell => ExcelCells.Parse(MarkdownBlocks.PlainText(cell)))])],
                        table.HasHeader));
                    pendingName = null;
                    break;
            }
        }

        return sheets.Count > 0 ? sheets : [FromDelimited(markdown)];
    }

    private static readonly char[] Separators = ['\t', ';', ','];

    /// <summary>CSV или TSV: разделитель — тот, что чаще встречается в первой строке.</summary>
    public static SheetInput FromDelimited(string text)
    {
        var lines = TextFileReader.SplitLines(text ?? "").Where(line => line.Length > 0).ToList();
        if (lines.Count == 0)
        {
            throw new DocumentException("There is no table to write: give sheets, a Markdown table or CSV lines.");
        }

        var first = lines[0];
        var separator = Separators.OrderByDescending(c => first.Count(ch => ch == c)).First();
        return new SheetInput("Sheet1", [.. lines.Select(line => (IReadOnlyList<CellInput>)[.. SplitDelimited(line, separator).Select(ExcelCells.Parse)])]);
    }

    /// <summary>Поля строки с учётом кавычек: «"Иванов, И.",42» — два поля, а не три.</summary>
    private static List<string> SplitDelimited(string line, char separator)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == separator)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }
}
