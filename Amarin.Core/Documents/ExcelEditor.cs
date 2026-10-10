using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Amarin.Core;

/// <summary>Правка открытого .xlsx: ячейки по адресам, строки в конец листа, новый лист, переименование, очистка диапазона.</summary>
/// <remarks>
/// <para>
/// Строки посреди листа не вставляются и не удаляются намеренно: это сдвигает адреса во всех
/// формулах, диапазонах и условном форматировании книги, и полуготовый сдвиг молча ломал бы
/// чужие формулы. Такое — через пересборку листа или через агента.
/// </para>
/// <para>
/// Цепочка вычислений (<c>calcChain.xml</c>) при сохранении выбрасывается, а книга помечается
/// «пересчитать при открытии». Цепочка перечисляет ячейки с формулами; убранная или заменённая
/// формула, которая в ней осталась, — это окно Excel «найдено содержимое, которое не удалось
/// прочитать, восстановить?». Без цепочки Excel строит её заново сам.
/// </para>
/// </remarks>
internal sealed class ExcelEditor : IDisposable
{
    private readonly SpreadsheetDocument _document;
    private readonly WorkbookPart _workbook;
    private readonly S.Workbook _book;
    private readonly ExcelStyleIds _styles;

    private ExcelEditor(SpreadsheetDocument document)
    {
        _document = document;
        _workbook = document.WorkbookPart ?? throw new DocumentException("The Excel file has no workbook.");
        _book = _workbook.Workbook ?? throw new DocumentException("The Excel file has no workbook.");
        _styles = ExcelStyles.Ensure(_workbook);
    }

    public static ExcelEditor Open(string path) => new(ExcelReader.Open(path, editable: true));

    /// <summary>Есть ли в книге формулы: их итоги в файле посчитаны по прежним числам.</summary>
    public bool HasFormulas =>
        _workbook.WorksheetParts.Any(part => part.Worksheet?.Descendants<S.CellFormula>().Any() == true);

    /// <summary>Ставит значения в ячейки по адресам (<c>B7</c>).</summary>
    public int SetCells(string? sheet, IReadOnlyList<(string Address, CellInput Value)> cells)
    {
        var data = Data(sheet);
        foreach (var (address, value) in cells)
        {
            if (!CellAddress.TryParse(address, out var column, out var row))
            {
                throw new DocumentException($"'{address}' is not a cell address. Use A1-style addresses: the column letters, then the row number.");
            }

            ExcelCells.Set(data, column, row, value, _styles);
        }

        return cells.Count;
    }

    /// <summary>Заполняет диапазон формулой или рядом (<see cref="ExcelFill"/>).</summary>
    /// <returns>Сколько ячеек записано.</returns>
    public int Fill(string? sheet, CellFill fill)
    {
        var data = Data(sheet);
        var count = 0;
        foreach (var (column, row, input) in ExcelFill.Expand(fill))
        {
            ExcelCells.Set(data, column, row, input, _styles);
            count++;
        }

        return count;
    }

    /// <summary>Дописывает строки под последней занятой строкой листа.</summary>
    /// <returns>Номер первой дописанной строки.</returns>
    public int AppendRows(string? sheet, IReadOnlyList<IReadOnlyList<CellInput>> rows)
    {
        var data = Data(sheet);
        var start = (int)(data.Elements<S.Row>().Select(row => row.RowIndex?.Value ?? 0).DefaultIfEmpty(0U).Max() + 1);
        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < rows[r].Count; c++)
            {
                ExcelCells.Set(data, c + 1, start + r, rows[r][c], _styles);
            }
        }

        return start;
    }

    public void AddSheet(string name, IReadOnlyList<IReadOnlyList<CellInput>> rows, bool header)
    {
        var sheets = _book.Sheets ?? _book.AppendChild(new S.Sheets());
        var taken = sheets.Elements<S.Sheet>().Select(s => s.Name?.Value ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextId = sheets.Elements<S.Sheet>().Select(s => s.SheetId?.Value ?? 0).DefaultIfEmpty(0U).Max() + 1;
        var safe = ExcelWriter.SafeName(name, nextId);
        if (taken.Contains(safe))
        {
            throw new DocumentException($"A sheet named \"{safe}\" already exists.");
        }

        var part = _workbook.AddNewPart<WorksheetPart>();
        part.Worksheet = ExcelWriter.Sheet(new SheetInput(safe, rows, header), _styles, out _);
        sheets.Append(new S.Sheet { Id = _workbook.GetIdOfPart(part), SheetId = nextId, Name = safe });
    }

    /// <summary>
    /// Переименовывает лист вместе со ссылками на него в формулах и именованных диапазонах — иначе
    /// они превратились бы в #REF! при первом пересчёте.
    /// </summary>
    /// <remarks>
    /// Ссылка заменяется только целиком: «Лист1!» внутри «МойЛист1!» — ссылка на другой лист, и
    /// простая замена подстроки переписала бы и её. Диаграммы и сводные таблицы ссылаются на лист
    /// своими частями книги — их Excel поправит сам при открытии, если не сможет, предложит.
    /// </remarks>
    public void RenameSheet(string sheet, string name)
    {
        var target = Sheet(sheet);
        var safe = ExcelWriter.SafeName(name, target.SheetId?.Value ?? 1);
        if (_book.Sheets?.Elements<S.Sheet>().Any(s => !ReferenceEquals(s, target) && string.Equals(s.Name?.Value, safe, StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new DocumentException($"A sheet named \"{safe}\" already exists.");
        }

        var old = target.Name?.Value ?? "";
        target.Name = safe;
        if (old.Length == 0)
        {
            return;
        }

        var reference = SheetReference(old);
        var replacement = Quote(safe) + "!";
        foreach (var part in _workbook.WorksheetParts)
        {
            foreach (var formula in part.Worksheet?.Descendants<S.CellFormula>() ?? [])
            {
                formula.Text = reference.Replace(formula.Text, replacement);
            }
        }

        foreach (var defined in _book.DefinedNames?.Elements<S.DefinedName>() ?? [])
        {
            defined.Text = reference.Replace(defined.Text, replacement);
        }
    }

    /// <summary>Ссылка на лист в формуле: <c>'Имя'!</c> (с удвоенными кавычками внутри) или <c>Имя!</c> целиком.</summary>
    internal static Regex SheetReference(string name) => new(
        "'" + Regex.Escape(name.Replace("'", "''", StringComparison.Ordinal)) + "'!" +
        "|(?<![\\p{L}\\p{N}_.'])" + Regex.Escape(name) + "!",
        RegexOptions.CultureInvariant);

    internal static string Quote(string name) =>
        name.Any(c => !char.IsLetterOrDigit(c) && c != '_') ? "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'" : name;

    /// <summary>
    /// Очищает значения и формулы диапазона; оформление ячеек остаётся — пустая ячейка в рамке и с
    /// заливкой остаётся частью таблицы, которую человек сверстал.
    /// </summary>
    public int Clear(string? sheet, string range)
    {
        if (!CellAddress.TryParseRange(range, out var area))
        {
            throw new DocumentException($"'{range}' is not a cell range. Use A1:F50, B:D or 5:9.");
        }

        var data = Data(sheet);
        var cleared = 0;
        foreach (var row in data.Elements<S.Row>())
        {
            var number = (int)(row.RowIndex?.Value ?? 0);
            if (number < area.FromRow || number > area.ToRow)
            {
                continue;
            }

            foreach (var cell in row.Elements<S.Cell>().ToList())
            {
                if (!CellAddress.TryParse(cell.CellReference?.Value, out var column, out _) ||
                    column < area.FromColumn || column > area.ToColumn)
                {
                    continue;
                }

                if (cell.StyleIndex?.Value is null or 0)
                {
                    cell.Remove();
                }
                else
                {
                    cell.CellFormula = null;
                    cell.CellValue = null;
                    cell.InlineString = null;
                    cell.DataType = null;
                }

                cleared++;
            }
        }

        return cleared;
    }

    /// <returns>Сколько формул посчитано здесь и сколько оставлено Excel.</returns>
    public RecalcReport Save()
    {
        if (_workbook.CalculationChainPart is { } chain)
        {
            _workbook.DeletePart(chain);
        }

        ExcelCells.RecalculateOnOpen(_workbook);
        var report = ExcelRecalc.Apply(_workbook);
        _workbook.WorkbookStylesPart?.Stylesheet?.Save();
        foreach (var part in _workbook.WorksheetParts)
        {
            part.Worksheet?.Save();
        }

        _book.Save();
        return report;
    }

    public void Dispose() => _document.Dispose();

    private S.Sheet Sheet(string? name)
    {
        var sheets = _book.Sheets?.Elements<S.Sheet>().ToList() ?? [];
        if (sheets.Count == 0)
        {
            throw new DocumentException("The workbook has no sheets.");
        }

        return ExcelReader.Select(sheets, name)[0];
    }

    private S.SheetData Data(string? name)
    {
        var sheet = Sheet(name);
        if (sheet.Id?.Value is not { } id || _workbook.GetPartById(id) is not WorksheetPart part || part.Worksheet is null)
        {
            throw new DocumentException($"Sheet \"{sheet.Name}\" could not be opened.");
        }

        return part.Worksheet.GetFirstChild<S.SheetData>() ?? part.Worksheet.AppendChild(new S.SheetData());
    }
}
