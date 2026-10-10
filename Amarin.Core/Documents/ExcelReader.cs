using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Amarin.Core;

/// <summary>
/// Excel (.xlsx) — листами, строками и адресами ячеек: таблица с номерами строк и буквами
/// столбцов, у формул — и значение, и сама формула.
/// </summary>
/// <remarks>
/// Строки читаются потоком (<see cref="OpenXmlReader"/>), а не деревом листа: лист на сто тысяч
/// строк целиком в памяти занял бы сотни мегабайт, а показать из него надо одну страницу. Дерево
/// строится только для строк, попавших в окно.
/// </remarks>
internal static class ExcelReader
{
    /// <summary>Шире этого таблица в ответе не читается: дальние столбцы — диапазоном.</summary>
    public const int MaxColumns = 40;

    private const int MaxCellChars = 200;

    /// <summary>Меньше этого на лист не даётся: несколько строк таблицы — уже не обзор.</summary>
    private const int MinSheetShare = 1500;

    public static DocumentContent Read(DocumentSource source, DocumentWindow window)
    {
        using var stream = source.OpenRead();
        using var document = Open(stream);
        var workbook = document.WorkbookPart ?? throw new DocumentException("The Excel file has no workbook.");
        var sheets = workbook.Workbook?.Sheets?.Elements<S.Sheet>().ToList() ?? [];
        if (sheets.Count == 0)
        {
            throw new DocumentException("The Excel file has no sheets.");
        }

        var context = new ExcelContext(workbook);
        var selected = Select(sheets, window.Sheet);
        var notes = new List<string>();
        (int FromColumn, int FromRow, int ToColumn, int ToRow)? range = null;
        if (window.Range is { Length: > 0 } text)
        {
            if (!CellAddress.TryParseRange(text, out var parsed))
            {
                throw new DocumentException($"'{text}' is not a cell range. Use A1:F50, B:D or 5:9.");
            }

            range = parsed;
        }

        // Без выбранного листа бюджет делится поровну: каждый показанный лист хоть немного, но виден.
        // Листов больше, чем влезает по полторы тысячи знаков, — остальные только называются: иначе
        // книга на двадцать листов выходила за предел ответа, и его хвост обрезался посреди таблицы.
        var shownSheets = selected.Count > 1 ? selected.Take(Math.Max(1, window.Budget / MinSheetShare)).ToList() : selected;
        var share = shownSheets.Count > 1 ? window.Budget / shownSheets.Count : window.Budget;
        if (shownSheets.Count < selected.Count)
        {
            notes.Add("Not shown here: " + string.Join(", ", selected.Skip(shownSheets.Count).Select(sheet => $"\"{sheet.Name}\"")) +
                      ". Read each with read_file and sheet=\"name\".");
        }

        var sections = new List<DocumentSection>();
        foreach (var sheet in shownSheets)
        {
            if (sheet.Id?.Value is not { } relation || workbook.GetPartById(relation) is not WorksheetPart part)
            {
                continue;
            }

            var section = ReadSheet(part, sheet, context, window with { Budget = share }, range, out var wide);
            sections.Add(section);
            if (wide)
            {
                notes.Add($"Sheet \"{sheet.Name}\" is wider than {MaxColumns} columns; read the rest with range, e.g. range=\"{CellAddress.ColumnName(MaxColumns + 1)}1:{CellAddress.ColumnName(MaxColumns + 20)}50\".");
            }
        }

        var names = sheets.Select(sheet => $"\"{sheet.Name}\"" + (sheet.State?.Value == S.SheetStateValues.Hidden ? " (hidden)" : ""));
        return new DocumentContent
        {
            Kind = DocumentKind.Excel,
            Unit = "row",
            Sections = sections,
            Summary = $"{sheets.Count} sheet(s): {string.Join(", ", names)}",
            Notes = notes
        };
    }

    public static SpreadsheetDocument Open(string path, bool editable) =>
        Guard(() => SpreadsheetDocument.Open(path, editable));

    /// <summary>Открывает .xlsx из потока только для чтения — так читается вложение из памяти.</summary>
    public static SpreadsheetDocument Open(Stream stream) =>
        Guard(() => SpreadsheetDocument.Open(stream, false));

    private static SpreadsheetDocument Guard(Func<SpreadsheetDocument> open)
    {
        try
        {
            return open();
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException || (ex is IOException && ex is not FileNotFoundException))
        {
            throw new DocumentException(
                $"Excel could not be read: {ex.Message}. It may be password-protected, damaged or an old .xls renamed to .xlsx.");
        }
    }

    /// <summary>Лист по имени (без учёта регистра); не нашёлся — отказ со списком листов.</summary>
    internal static List<S.Sheet> Select(List<S.Sheet> sheets, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return sheets;
        }

        var match = sheets.FirstOrDefault(sheet => string.Equals(sheet.Name?.Value, name.Trim(), StringComparison.OrdinalIgnoreCase));
        return match is not null
            ? [match]
            : throw new DocumentException(
                $"There is no sheet \"{name}\". Sheets: {string.Join(", ", sheets.Select(sheet => $"\"{sheet.Name}\""))}.");
    }

    private static DocumentSection ReadSheet(
        WorksheetPart part,
        S.Sheet sheet,
        ExcelContext context,
        DocumentWindow window,
        (int FromColumn, int FromRow, int ToColumn, int ToRow)? range,
        out bool wide)
    {
        wide = false;
        var budget = new UnitBudget(window);
        var shown = new List<DocumentUnit>();
        var total = 0;
        var minColumn = int.MaxValue;
        var maxColumn = 0;
        var minRow = int.MaxValue;
        var maxRow = 0;
        var nextRow = 0;

        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(S.Row) || !reader.IsStartElement)
            {
                continue;
            }

            if (reader.LoadCurrentElement() is not S.Row row)
            {
                continue;
            }

            var number = row.RowIndex?.Value is { } index ? (int)index : nextRow + 1;
            nextRow = number;

            var cells = new List<SheetCell>();
            var column = 0;
            foreach (var cell in row.Elements<S.Cell>())
            {
                column = CellAddress.TryParse(cell.CellReference?.Value, out var at, out _) ? at : column + 1;
                var value = context.Display(cell);
                if (value.Length == 0)
                {
                    continue;
                }

                cells.Add(new SheetCell(column, value.Length <= MaxCellChars ? value : value[..MaxCellChars] + "…"));
            }

            if (cells.Count == 0)
            {
                continue;
            }

            total++;
            minRow = Math.Min(minRow, number);
            maxRow = Math.Max(maxRow, number);
            minColumn = Math.Min(minColumn, cells[0].Column);
            maxColumn = Math.Max(maxColumn, cells[^1].Column);

            var inWindow = range is { } r
                ? number >= r.FromRow && number <= r.ToRow
                : number >= window.Offset;
            if (!inWindow || budget.Full)
            {
                continue;
            }

            var from = range?.FromColumn ?? 1;
            var to = range?.ToColumn ?? from + MaxColumns - 1;
            var visible = cells.Where(cell => cell.Column >= from && cell.Column <= Math.Min(to, from + MaxColumns - 1)).ToList();
            wide |= cells.Any(cell => cell.Column > from + MaxColumns - 1 && cell.Column <= to);
            if (visible.Count == 0)
            {
                continue;
            }

            if (budget.TryTake(visible.Sum(cell => cell.Value.Length + 3) + 8))
            {
                shown.Add(new DocumentUnit(number, "", visible));
            }
        }

        var extent = maxRow == 0 ? null : CellAddress.Of(minColumn, minRow) + ":" + CellAddress.Of(maxColumn, maxRow);
        return new DocumentSection(sheet.Name?.Value ?? "?", shown, total, extent, budget.More);
    }

    /// <summary>Общие строки и форматы дат книги — читаются один раз на всю книгу.</summary>
    internal sealed class ExcelContext
    {
        /// <summary>Встроенные форматы дат и времени Excel (номера форматов).</summary>
        private static readonly HashSet<uint> BuiltInDateFormats = [14, 15, 16, 17, 18, 19, 20, 21, 22, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 45, 46, 47, 50, 51, 52, 53, 54, 55, 56, 57, 58];

        private readonly List<string> _shared = [];
        private readonly HashSet<uint> _dateStyles = [];

        public ExcelContext(WorkbookPart workbook)
        {
            if (workbook.SharedStringTablePart?.SharedStringTable is { } table)
            {
                foreach (var item in table.Elements<S.SharedStringItem>())
                {
                    _shared.Add(item.InnerText);
                }
            }

            var stylesheet = workbook.WorkbookStylesPart?.Stylesheet;
            var custom = stylesheet?.NumberingFormats?.Elements<S.NumberingFormat>()
                .Where(format => LooksLikeDate(format.FormatCode?.Value))
                .Select(format => format.NumberFormatId?.Value)
                .OfType<uint>()
                .ToHashSet() ?? [];
            var index = 0u;
            foreach (var format in stylesheet?.CellFormats?.Elements<S.CellFormat>() ?? [])
            {
                var id = format.NumberFormatId?.Value ?? 0;
                if (BuiltInDateFormats.Contains(id) || custom.Contains(id))
                {
                    _dateStyles.Add(index);
                }

                index++;
            }
        }

        /// <summary>Что видно в ячейке; у формулы — значение и формула: «42 (=SUM(B2:B9))».</summary>
        public string Display(S.Cell cell)
        {
            var value = Value(cell);
            if (cell.CellFormula is { Text: { Length: > 0 } formula })
            {
                return value.Length == 0 ? "=" + formula : $"{value} (={formula})";
            }

            return value;
        }

        internal string Value(S.Cell cell)
        {
            var raw = cell.CellValue?.Text ?? "";
            var type = cell.DataType?.Value;
            if (type == S.CellValues.SharedString)
            {
                return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var at) && at >= 0 && at < _shared.Count
                    ? _shared[at]
                    : "";
            }

            if (type == S.CellValues.InlineString)
            {
                return cell.InlineString?.InnerText ?? "";
            }

            if (type == S.CellValues.Boolean)
            {
                return raw == "1" ? "TRUE" : raw == "0" ? "FALSE" : raw;
            }

            if ((type is null || type == S.CellValues.Number) && raw.Length > 0 &&
                cell.StyleIndex?.Value is { } style && _dateStyles.Contains(style) &&
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) &&
                serial is > -657435 and < 2958466)
            {
                var date = DateTime.FromOADate(serial);
                return date.TimeOfDay == TimeSpan.Zero
                    ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }

            return raw;
        }

        /// <summary>Формат с днями, месяцами или годами вне кавычек и скобок — формат даты.</summary>
        private static bool LooksLikeDate(string? code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return false;
            }

            var quoted = false;
            var bracket = false;
            foreach (var c in code)
            {
                switch (c)
                {
                    case '"':
                        quoted = !quoted;
                        break;
                    case '[':
                        bracket = true;
                        break;
                    case ']':
                        bracket = false;
                        break;
                    case 'd' or 'D' or 'y' or 'Y' or 'm' or 'M' or 'h' or 'H' when !quoted && !bracket:
                        return true;
                }
            }

            return false;
        }
    }
}
