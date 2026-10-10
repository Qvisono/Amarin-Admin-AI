using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Amarin.Core;

/// <summary>Сколько формул книги посчитано, а сколько оставлено Excel.</summary>
internal readonly record struct RecalcReport(int Computed, int Left);

/// <summary>
/// Все ячейки книги, все её листы: значения, формулы и что из этого считается
/// (<see cref="ExcelCalc"/>). Общее для пересчёта после записи и для перевода книги в PDF или Word.
/// </summary>
internal sealed class ExcelGrid
{
    private readonly Dictionary<string, Dictionary<(int Column, int Row), Entry>> _sheets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];

    /// <summary>Ячейка листа: сам элемент и что в нём для подсчёта.</summary>
    internal sealed record Entry(S.Cell Cell, ExcelCalc.CellSource Source);

    private ExcelGrid()
    {
    }

    /// <summary>Листы по порядку книги.</summary>
    public IReadOnlyList<string> Sheets => _order;

    public bool HasFormulas => _sheets.Values.Any(cells => cells.Values.Any(entry => entry.Source.Formula is not null || entry.Source.Opaque));

    public IReadOnlyDictionary<(int Column, int Row), Entry> Cells(string sheet) =>
        _sheets.TryGetValue(sheet, out var cells) ? cells : new Dictionary<(int, int), Entry>();

    public static ExcelGrid Load(WorkbookPart workbook)
    {
        var grid = new ExcelGrid();
        var shared = workbook.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>().Select(item => item.InnerText).ToList() ?? [];
        foreach (var sheet in workbook.Workbook?.Sheets?.Elements<S.Sheet>() ?? [])
        {
            var name = sheet.Name?.Value ?? "";
            if (sheet.Id?.Value is not { } id || workbook.GetPartById(id) is not WorksheetPart part || part.Worksheet is null)
            {
                continue;
            }

            var cells = new Dictionary<(int, int), Entry>();
            var nextRow = 0;
            foreach (var row in part.Worksheet.GetFirstChild<S.SheetData>()?.Elements<S.Row>() ?? [])
            {
                var number = row.RowIndex?.Value is { } index ? (int)index : nextRow + 1;
                nextRow = number;
                var column = 0;
                foreach (var cell in row.Elements<S.Cell>())
                {
                    column = CellAddress.TryParse(cell.CellReference?.Value, out var at, out _) ? at : column + 1;
                    cells[(column, number)] = new Entry(cell, Source(cell, shared, name));
                }
            }

            grid._sheets[name] = cells;
            grid._order.Add(name);
        }

        return grid;
    }

    /// <summary>Подсчёт по этой книге; пустая ячейка — пустое значение.</summary>
    public ExcelCalc Calc() => new((sheet, column, row) =>
    {
        var name = sheet ?? "";
        return _sheets.TryGetValue(name, out var cells) && cells.TryGetValue((column, row), out var entry)
            ? entry.Source with { Sheet = name }
            : _sheets.ContainsKey(name)
                ? ExcelCalc.CellSource.None(name)
                : throw new FormulaUnsupportedException("no sheet " + name);
    });

    /// <summary>
    /// Что в ячейке для подсчёта. Общая формула (кроме первой ячейки), формула массива и таблица
    /// данных — непрозрачны: их текста в ячейке нет или он значит не то, что написано.
    /// </summary>
    private static ExcelCalc.CellSource Source(S.Cell cell, List<string> shared, string sheet)
    {
        if (cell.CellFormula is { } formula)
        {
            var type = formula.FormulaType?.Value;
            var text = formula.Text;
            if (type == S.CellFormulaValues.Array || type == S.CellFormulaValues.DataTable ||
                string.IsNullOrWhiteSpace(text))
            {
                return new ExcelCalc.CellSource(Stored(cell, shared), Opaque: true, Sheet: sheet);
            }

            return new ExcelCalc.CellSource(Stored(cell, shared), text.TrimStart('='), Sheet: sheet);
        }

        return new ExcelCalc.CellSource(Stored(cell, shared), Sheet: sheet);
    }

    /// <summary>Значение, сохранённое в файле, — у формулы это итог прошлого подсчёта.</summary>
    internal static XValue Stored(S.Cell cell, List<string> shared)
    {
        var raw = cell.CellValue?.Text ?? "";
        var type = cell.DataType?.Value;
        if (type == S.CellValues.SharedString)
        {
            return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var at) && at >= 0 && at < shared.Count
                ? XValue.Of(shared[at])
                : XValue.Empty;
        }

        if (type == S.CellValues.InlineString)
        {
            return XValue.Of(cell.InlineString?.InnerText ?? "");
        }

        if (type == S.CellValues.String)
        {
            return XValue.Of(raw);
        }

        if (type == S.CellValues.Boolean)
        {
            return XValue.Of(raw == "1");
        }

        if (type == S.CellValues.Error)
        {
            return XValue.Error(raw);
        }

        return raw.Length > 0 && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? XValue.Of(number)
            : XValue.Empty;
    }
}

/// <summary>
/// Итоги формул — в файл: книга, собранная или поправленная здесь, знает свои значения до открытия в
/// Excel. Что посчитать нельзя, сохраняет прежний итог, а Excel пересчитает всё при открытии
/// (<see cref="ExcelCells.RecalculateOnOpen"/>).
/// </summary>
internal static class ExcelRecalc
{
    public static RecalcReport Apply(WorkbookPart workbook)
    {
        var grid = ExcelGrid.Load(workbook);
        if (!grid.HasFormulas)
        {
            return new RecalcReport(0, 0);
        }

        var calc = grid.Calc();
        var computed = 0;
        var left = 0;
        foreach (var sheet in grid.Sheets)
        {
            foreach (var ((column, row), entry) in grid.Cells(sheet))
            {
                if (entry.Source.Formula is null && !entry.Source.Opaque)
                {
                    continue;
                }

                if (calc.TryValue(sheet, column, row) is { } value)
                {
                    Store(entry.Cell, value);
                    computed++;
                }
                else
                {
                    left++;
                }
            }
        }

        return new RecalcReport(computed, left);
    }

    /// <summary>Итог формулы в ячейку, рядом с самой формулой.</summary>
    private static void Store(S.Cell cell, XValue value)
    {
        switch (value.Kind)
        {
            case XKind.Text:
                cell.DataType = S.CellValues.String;
                cell.CellValue = new S.CellValue(value.Text);
                break;
            case XKind.Bool:
                cell.DataType = S.CellValues.Boolean;
                cell.CellValue = new S.CellValue(value.Number != 0 ? "1" : "0");
                break;
            case XKind.Error:
                cell.DataType = S.CellValues.Error;
                cell.CellValue = new S.CellValue(value.Text);
                break;
            default:
                cell.DataType = null;
                cell.CellValue = new S.CellValue(value.Number.ToString("R", CultureInfo.InvariantCulture));
                break;
        }
    }
}
