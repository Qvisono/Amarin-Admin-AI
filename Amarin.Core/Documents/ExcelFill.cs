using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Amarin.Core;

/// <summary>Заполнение диапазона: одна формула или ряд чисел на весь диапазон.</summary>
/// <param name="Range">Диапазон листа: <c>B2:K11</c>.</param>
/// <param name="Value">Формула с относительными ссылками, число начала ряда или значение для всех ячеек.</param>
/// <param name="Step">Шаг ряда; null — значение одно на все ячейки.</param>
internal sealed record CellFill(string Range, CellInput Value, double? Step = null);

/// <summary>
/// Заполнение диапазона, как протяжка в Excel: относительные ссылки формулы сдвигаются на каждую
/// ячейку, закреплённые (<c>$</c>) остаются; число с шагом становится рядом.
/// </summary>
/// <remarks>
/// Ради больших таблиц: модель не может вписать двадцать тысяч значений в один ответ и не должна
/// считать их в уме, а одна строка заполнения описывает их все — и программа сама посчитает
/// формулы (<see cref="ExcelRecalc"/>).
/// </remarks>
internal static partial class ExcelFill
{
    /// <summary>Больше ячеек одно заполнение не пишет: дальше это уже не таблица, а ошибка в диапазоне.</summary>
    internal const int MaxCells = 1_000_000;

    public static IEnumerable<(int Column, int Row, CellInput Input)> Expand(CellFill fill)
    {
        if (!CellAddress.TryParseRange(fill.Range, out var range) || range.FromColumn < 1 || range.FromRow < 1 ||
            range.ToColumn > CellAddress.MaxColumn || range.ToRow > CellAddress.MaxRow)
        {
            throw new DocumentException($"'{fill.Range}' is not a cell range to fill. Give an A1-style range: the top-left cell, a colon, the bottom-right cell.");
        }

        var width = range.ToColumn - range.FromColumn + 1;
        var height = range.ToRow - range.FromRow + 1;
        if ((long)width * height > MaxCells)
        {
            throw new DocumentException($"{fill.Range} is {(long)width * height} cells; one fill writes at most {MaxCells}.");
        }

        return Cells();

        IEnumerable<(int, int, CellInput)> Cells()
        {
            for (var row = range.FromRow; row <= range.ToRow; row++)
            {
                for (var column = range.FromColumn; column <= range.ToColumn; column++)
                {
                    var index = ((row - range.FromRow) * width) + (column - range.FromColumn);
                    yield return (column, row, At(fill, column - range.FromColumn, row - range.FromRow, index));
                }
            }
        }
    }

    private static CellInput At(CellFill fill, int columns, int rows, int index)
    {
        var value = fill.Value;
        if (value.Kind == CellInputKind.Formula)
        {
            return value with { Text = Shift(value.Text, columns, rows) };
        }

        if (fill.Step is { } step && value.Kind is CellInputKind.Number or CellInputKind.Percent or CellInputKind.Date or CellInputKind.DateTime)
        {
            var number = value.Number + (step * index);
            return value with { Number = number, Text = number.ToString("R", CultureInfo.InvariantCulture) };
        }

        return value;
    }

    /// <summary>
    /// Формула, сдвинутая на <paramref name="columns"/> столбцов и <paramref name="rows"/> строк:
    /// относительные части ссылок двигаются, закреплённые <c>$</c> — нет. Текст в кавычках и имена
    /// листов в апострофах не трогаются: «Q1 2024» в имени листа — не ячейка Q1.
    /// </summary>
    public static string Shift(string formula, int columns, int rows)
    {
        if (columns == 0 && rows == 0)
        {
            return formula;
        }

        var result = new StringBuilder(formula.Length + 8);
        var plain = new StringBuilder();
        for (var i = 0; i < formula.Length; i++)
        {
            var c = formula[i];
            if (c is '"' or '\'')
            {
                result.Append(ShiftPlain(plain.ToString(), columns, rows));
                plain.Clear();
                var end = Closing(formula, i, c);
                result.Append(formula, i, end - i + 1);
                i = end;
                continue;
            }

            plain.Append(c);
        }

        return result.Append(ShiftPlain(plain.ToString(), columns, rows)).ToString();
    }

    private static int Closing(string formula, int start, char quote)
    {
        for (var i = start + 1; i < formula.Length; i++)
        {
            if (formula[i] != quote)
            {
                continue;
            }

            if (i + 1 < formula.Length && formula[i + 1] == quote)
            {
                i++;
                continue;
            }

            return i;
        }

        return formula.Length - 1;
    }

    private static string ShiftPlain(string text, int columns, int rows) =>
        Reference().Replace(text, match =>
        {
            var fixedColumn = match.Groups[1].Value.Length > 0;
            var fixedRow = match.Groups[3].Value.Length > 0;
            if (!CellAddress.TryParse(match.Groups[2].Value + match.Groups[4].Value, out var column, out var row))
            {
                return match.Value;
            }

            var newColumn = fixedColumn ? column : column + columns;
            var newRow = fixedRow ? row : row + rows;
            if (newColumn < 1 || newRow < 1 || newColumn > CellAddress.MaxColumn || newRow > CellAddress.MaxRow)
            {
                throw new DocumentException($"Filling moves the reference {match.Value} off the sheet; fix its column or row with $.");
            }

            return match.Groups[1].Value + CellAddress.ColumnName(newColumn) + match.Groups[3].Value +
                   newRow.ToString(CultureInfo.InvariantCulture);
        });

    /// <summary>
    /// Ссылка на ячейку: не часть имени (буква, цифра, точка, подчёркивание до неё), не имя функции
    /// (скобка после) и не имя листа (восклицательный знак после).
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_.])(\$?)([A-Za-z]{1,3})(\$?)([0-9]{1,7})(?![A-Za-z0-9_(!])")]
    private static partial Regex Reference();
}
