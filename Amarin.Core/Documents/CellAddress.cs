namespace Amarin.Core;

/// <summary>Адреса ячеек Excel: <c>B7</c>, столбцы буквами (<c>A</c> = 1, <c>AA</c> = 27), диапазоны <c>A1:F50</c>.</summary>
internal static class CellAddress
{
    /// <summary>Последний столбец Excel — XFD.</summary>
    public const int MaxColumn = 16_384;

    public const int MaxRow = 1_048_576;

    public static string ColumnName(int column)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(column, 1);
        var name = "";
        while (column > 0)
        {
            var rest = (column - 1) % 26;
            name = (char)('A' + rest) + name;
            column = (column - 1) / 26;
        }

        return name;
    }

    public static string Of(int column, int row) => ColumnName(column) + row.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Разбирает адрес вида <c>B7</c> (регистр и знаки <c>$</c> не важны).</summary>
    public static bool TryParse(string? text, out int column, out int row)
    {
        column = 0;
        row = 0;
        var value = (text ?? "").Trim().Replace("$", "", StringComparison.Ordinal).ToUpperInvariant();
        var i = 0;
        while (i < value.Length && value[i] is >= 'A' and <= 'Z')
        {
            column = (column * 26) + (value[i] - 'A' + 1);
            if (column > MaxColumn)
            {
                return false;
            }

            i++;
        }

        if (i == 0 || i == value.Length ||
            !int.TryParse(value[i..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out row))
        {
            return false;
        }

        return row is >= 1 and <= MaxRow;
    }

    /// <summary>
    /// Разбирает диапазон <c>A1:F50</c>. Одна ячейка — диапазон из неё одной; целые строки
    /// (<c>5:9</c>) и столбцы (<c>B:D</c>) тоже годятся.
    /// </summary>
    public static bool TryParseRange(string? text, out (int FromColumn, int FromRow, int ToColumn, int ToRow) range)
    {
        range = default;
        var parts = (text ?? "").Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || parts[0].Length == 0)
        {
            return false;
        }

        if (!TryPart(parts[0], out var c1, out var r1) || !TryPart(parts[^1], out var c2, out var r2))
        {
            return false;
        }

        // Целый столбец — все строки, целая строка — все столбцы.
        if (r1 == 0 && r2 == 0)
        {
            r1 = 1;
            r2 = MaxRow;
        }

        if (c1 == 0 && c2 == 0)
        {
            c1 = 1;
            c2 = MaxColumn;
        }

        if (c1 == 0 || c2 == 0 || r1 == 0 || r2 == 0)
        {
            return false;
        }

        range = (Math.Min(c1, c2), Math.Min(r1, r2), Math.Max(c1, c2), Math.Max(r1, r2));
        return true;

        static bool TryPart(string part, out int column, out int row)
        {
            if (TryParse(part, out column, out row))
            {
                return true;
            }

            var value = part.Replace("$", "", StringComparison.Ordinal).ToUpperInvariant();
            if (value.Length > 0 && value.All(c => c is >= 'A' and <= 'Z'))
            {
                row = 0;
                return TryParse(value + "1", out column, out _);
            }

            column = 0;
            return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out row) &&
                   row is >= 1 and <= MaxRow;
        }
    }
}
