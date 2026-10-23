using System.Globalization;
using System.Text;

namespace Amarin.Core;

/// <summary>Таблица для анализа: шапка и строки значений, как их видно в файле.</summary>
internal sealed record TableData(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Условие отбора строк: столбец, сравнение и значение.</summary>
internal sealed record TableFilter(string Column, string Op, string? Value);

/// <summary>Итог по столбцу: <c>sum</c>, <c>avg</c>, <c>min</c>, <c>max</c>, <c>count</c> или <c>distinct</c>.</summary>
internal sealed record TableAggregate(string? Column, string Function);

internal sealed record TableSort(string Column, bool Descending);

/// <summary>Что сделать с таблицей: отобрать, сгруппировать, подвести итоги, упорядочить.</summary>
internal sealed record TableRequest(
    IReadOnlyList<TableFilter> Filters,
    IReadOnlyList<string> GroupBy,
    IReadOnlyList<TableAggregate> Aggregates,
    IReadOnlyList<string> Columns,
    IReadOnlyList<TableSort> Sort,
    int Limit,
    bool Totals);

/// <summary>Итог: показанные строки результата, сколько их всего, сколько строк файла подошло и строка «Итого», если просили.</summary>
/// <param name="Count">Строк результата до предела показа: групп — при группировке, отобранных строк — без неё.</param>
internal sealed record TableResult(
    IReadOnlyList<string> Header,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    int Count,
    int Matched,
    int Total,
    bool Grouped,
    IReadOnlyList<string>? TotalsRow);

/// <summary>
/// Фильтр, группировка и итоги по таблице CSV или Excel — программой, а не моделью.
/// </summary>
/// <remarks>
/// <para>
/// Модель складывает сотни строк с ошибками и не видит строк за пределами прочитанного окна. Здесь
/// счёт идёт по всем строкам файла, а модель получает готовый итог. Значения берутся такими, как их
/// видно (Excel — с посчитанными формулами, <see cref="DocumentConvert.ExcelTables"/>), поэтому
/// числа читаются и в русской записи: пробел между разрядами, запятая перед дробной частью, знак
/// процента и валюты.
/// </para>
/// <para>
/// Столбец называют заголовком, а если такого нет — буквой, как в Excel, считая от первого
/// столбца таблицы.
/// </para>
/// </remarks>
internal static class TableQuery
{
    public const int DefaultLimit = 100;

    public const int MaxLimit = 1000;

    public static readonly string[] Operators = ["eq", "ne", "gt", "ge", "lt", "le", "contains", "not_contains", "empty", "not_empty"];

    public static readonly string[] Functions = ["sum", "avg", "min", "max", "count", "distinct"];

    /// <summary>Таблица из файла: CSV и TSV — строками текста, Excel — листом (по умолчанию первым непустым).</summary>
    public static TableData Load(DocumentSource source, string? sheet)
    {
        List<IReadOnlyList<string>> rows;
        switch (DocumentKinds.Of(source))
        {
            case DocumentKind.Excel:
                var tables = DocumentConvert.ExcelTables(source, sheet);
                var filled = tables.FindIndex(table => table.Rows.Count > 0);
                rows = filled >= 0
                    ? tables[filled].Rows
                    : throw new DocumentException(sheet is null ? "The workbook has no values." : $"Sheet \"{sheet}\" has no values.");
                break;
            case DocumentKind.Text when Path.GetExtension(source.Path).ToLowerInvariant() is ".csv" or ".tsv" or ".txt":
                var text = TextFileReader.Decode(source.ReadAllBytes()).Text;
                rows = [.. ExcelWriter.FromDelimited(text).Rows.Select(row => (IReadOnlyList<string>)[.. row.Select(cell => cell.Text)])];
                break;
            default:
                throw new DocumentException("analyze_table reads CSV, TSV and Excel tables. Read other files with read_file.");
        }

        rows = [.. rows.Where(row => row.Any(cell => cell.Trim().Length > 0))];
        if (rows.Count == 0)
        {
            throw new DocumentException("The table is empty.");
        }

        var width = rows.Max(row => row.Count);
        var header = Enumerable.Range(0, width)
            .Select(column => column < rows[0].Count && rows[0][column].Trim().Length > 0 ? rows[0][column].Trim() : CellAddress.ColumnName(column + 1))
            .ToList();
        return new TableData(header, [.. rows.Skip(1).Select(row => (IReadOnlyList<string>)[.. Enumerable.Range(0, width).Select(column => column < row.Count ? row[column].Trim() : "")])]);
    }

    public static TableResult Run(TableData table, TableRequest request)
    {
        var filters = request.Filters.Select(filter => (Column: Column(table, filter.Column), Filter: filter)).ToList();
        foreach (var (_, filter) in filters)
        {
            if (!Operators.Contains(filter.Op))
            {
                throw new DocumentException($"Unknown filter op \"{filter.Op}\". Use one of: {string.Join(", ", Operators)}.");
            }
        }

        var matched = table.Rows.Where(row => filters.All(item => Matches(row[item.Column], item.Filter))).ToList();
        var limit = Math.Clamp(request.Limit <= 0 ? DefaultLimit : request.Limit, 1, MaxLimit);

        List<string> header;
        List<IReadOnlyList<string>> rows;
        IReadOnlyList<string>? totals = null;
        if (request.GroupBy.Count > 0 || request.Aggregates.Count > 0)
        {
            var keys = request.GroupBy.Select(name => Column(table, name)).ToList();
            var aggregates = request.Aggregates.Count > 0 ? request.Aggregates : [new TableAggregate(null, "count")];
            var measures = aggregates.Select(aggregate => (Aggregate: aggregate, Column: Measure(table, aggregate))).ToList();
            header = [.. keys.Select(key => table.Header[key]), .. measures.Select(item => Label(table, item.Aggregate, item.Column))];
            rows = [.. matched
                .GroupBy(row => string.Join("\u001F", keys.Select(key => row[key])))
                .Select(group => (IReadOnlyList<string>)[
                    .. keys.Select(key => group.First()[key]),
                    .. measures.Select(item => Aggregate(group.ToList(), item.Aggregate.Function, item.Column))])];
            if (request.Totals && keys.Count > 0)
            {
                totals = [.. keys.Select((_, index) => index == 0 ? "Total" : ""), .. measures.Select(item => Aggregate(matched, item.Aggregate.Function, item.Column))];
            }
        }
        else
        {
            var columns = request.Columns.Count > 0 ? request.Columns.Select(name => Column(table, name)).ToList() : [.. Enumerable.Range(0, table.Header.Count)];
            header = [.. columns.Select(column => table.Header[column])];
            rows = [.. matched.Select(row => (IReadOnlyList<string>)[.. columns.Select(column => row[column])])];
            if (request.Totals)
            {
                totals = [.. columns.Select((column, index) =>
                    matched.Count > 0 && matched.Any(row => TryNumber(row[column], out _))
                        ? Aggregate(matched, "sum", column)
                        : index == 0 ? "Total" : "")];
            }
        }

        foreach (var sort in request.Sort.Reverse())
        {
            var column = OutputColumn(header, sort.Column);
            // Пустые — в конце при любом направлении: «по убыванию» не должно начинаться с пропусков.
            var ordered = rows.OrderBy(row => row[column].Length == 0);
            rows = sort.Descending
                ? [.. ordered.ThenByDescending(row => row[column], ValueComparer.Instance)]
                : [.. ordered.ThenBy(row => row[column], ValueComparer.Instance)];
        }

        return new TableResult(header, [.. rows.Take(limit)], rows.Count, matched.Count, table.Rows.Count, request.GroupBy.Count > 0, totals);
    }

    /// <summary>Итог как Markdown: таблица, строка «Итого» и сколько строк подошло.</summary>
    public static string Format(TableResult result)
    {
        var text = new StringBuilder();
        text.Append(Row(result.Header)).Append('\n');
        text.Append('|').Append(string.Concat(Enumerable.Repeat("---|", result.Header.Count))).Append('\n');
        foreach (var row in result.Rows)
        {
            text.Append(Row(row)).Append('\n');
        }

        if (result.TotalsRow is { } totals)
        {
            text.Append(Row([.. totals.Select((cell, index) => index == 0 && cell.Length > 0 ? "**" + cell + "**" : cell)])).Append('\n');
        }

        text.Append('\n').Append(result.Matched).Append(" of ").Append(result.Total).Append(" rows matched");
        if (result.Grouped)
        {
            text.Append(", ").Append(result.Count).Append(" group(s)");
        }

        if (result.Rows.Count < result.Count)
        {
            text.Append("; the first ").Append(result.Rows.Count).Append(" are shown, raise limit or narrow the filter for the rest");
        }

        return text.Append('.').ToString();
    }

    /// <summary>
    /// Диаграмма по результату: подписи — первый столбец (или <paramref name="label"/>), ряды —
    /// названные столбцы, а без них — все числовые, кроме подписей.
    /// </summary>
    public static ChartSpec Chart(TableResult result, string type, string? title, string? label, IReadOnlyList<string> values)
    {
        if (type is not (ChartSpec.Bar or ChartSpec.Line or ChartSpec.Pie))
        {
            throw new DocumentException("chart.type is bar, line or pie.");
        }

        var labelColumn = label is { Length: > 0 } ? OutputColumn(result.Header, label) : 0;
        var valueColumns = values.Count > 0
            ? values.Select(name => OutputColumn(result.Header, name)).ToList()
            : Enumerable.Range(0, result.Header.Count)
                .Where(column => column != labelColumn && result.Rows.Count > 0 && result.Rows.All(row => row[column].Length == 0 || TryNumber(row[column], out _)) &&
                                 result.Rows.Any(row => TryNumber(row[column], out _)))
                .ToList();
        if (valueColumns.Count == 0)
        {
            throw new DocumentException("The result has no numeric column to chart: name chart.values, or group with an aggregate.");
        }

        var points = result.Rows.Take(ChartSpec.MaxPoints).ToList();
        var series = valueColumns.Take(type == ChartSpec.Pie ? 1 : ChartSpec.MaxSeries)
            .Select(column => new ChartSeries(result.Header[column], [.. points.Select(row => TryNumber(row[column], out var number) ? number : (double?)null)]))
            .ToList();
        return new ChartSpec(type, title, [.. points.Select(row => row[labelColumn])], series);
    }

    /// <summary>
    /// Число из того, как его видно: «1 234,5», «1,234.5», «12%», «₽ 300», «-7». Одна запятая без
    /// точки — дробная часть: так пишут числа в русской Windows, из которой и приходят эти таблицы.
    /// </summary>
    public static bool TryNumber(string? text, out double number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = new StringBuilder(text.Length);
        foreach (var ch in text.Trim())
        {
            if (char.IsDigit(ch) || ch is '-' or '+' or '.' or ',' or 'e' or 'E')
            {
                value.Append(ch);
            }
            else if (ch is '−')
            {
                value.Append('-');
            }
            else if (!(char.IsWhiteSpace(ch) || ch is '\'' or '%' or '$' or '€' or '£' or '¥' or '₽'))
            {
                return false;
            }
        }

        var raw = value.ToString();
        var comma = raw.LastIndexOf(',');
        var dot = raw.LastIndexOf('.');
        if (comma >= 0 && dot >= 0)
        {
            raw = comma > dot ? raw.Replace(".", "", StringComparison.Ordinal).Replace(',', '.') : raw.Replace(",", "", StringComparison.Ordinal);
        }
        else if (comma >= 0)
        {
            raw = raw.Count(ch => ch == ',') > 1 ? raw.Replace(",", "", StringComparison.Ordinal) : raw.Replace(',', '.');
        }

        return raw.Any(char.IsDigit) &&
               double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
               double.IsFinite(number);
    }

    private static bool Matches(string cell, TableFilter filter)
    {
        var value = filter.Value?.Trim() ?? "";
        switch (filter.Op)
        {
            case "empty":
                return cell.Length == 0;
            case "not_empty":
                return cell.Length > 0;
            case "contains":
                return cell.Contains(value, StringComparison.OrdinalIgnoreCase);
            case "not_contains":
                return !cell.Contains(value, StringComparison.OrdinalIgnoreCase);
        }

        var order = ValueComparer.Instance.Compare(cell, value);
        var numericValue = TryNumber(value, out _);
        var both = numericValue && TryNumber(cell, out _);
        if (filter.Op is "gt" or "ge" or "lt" or "le" && numericValue && !both)
        {
            // С числом сравниваются только числа: текст в столбце сумм не «больше нуля».
            return false;
        }

        return filter.Op switch
        {
            "eq" => both ? order == 0 : string.Equals(cell, value, StringComparison.OrdinalIgnoreCase),
            "ne" => both ? order != 0 : !string.Equals(cell, value, StringComparison.OrdinalIgnoreCase),
            // Число с пустой ячейкой не сравнивается: «больше нуля» не должно отбирать пустые строки.
            "gt" => cell.Length > 0 && order > 0,
            "ge" => cell.Length > 0 && order >= 0,
            "lt" => cell.Length > 0 && order < 0,
            "le" => cell.Length > 0 && order <= 0,
            _ => false
        };
    }

    private static string Aggregate(IReadOnlyList<IReadOnlyList<string>> rows, string function, int? column)
    {
        if (function == "count")
        {
            return (column is { } at ? rows.Count(row => row[at].Length > 0) : rows.Count).ToString(CultureInfo.InvariantCulture);
        }

        var at2 = column ?? throw new DocumentException($"{function} needs a column.");
        if (function == "distinct")
        {
            return rows.Select(row => row[at2]).Where(cell => cell.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString(CultureInfo.InvariantCulture);
        }

        var numbers = rows.Select(row => TryNumber(row[at2], out var number) ? number : (double?)null).OfType<double>().ToList();
        if (numbers.Count == 0)
        {
            return "";
        }

        var result = function switch
        {
            "sum" => numbers.Sum(),
            "avg" => numbers.Average(),
            "min" => numbers.Min(),
            "max" => numbers.Max(),
            _ => throw new DocumentException($"Unknown aggregate \"{function}\". Use one of: {string.Join(", ", Functions)}.")
        };
        return ExcelCalc.FormatNumber(Math.Round(result, 6));
    }

    private static int? Measure(TableData table, TableAggregate aggregate)
    {
        if (!Functions.Contains(aggregate.Function))
        {
            throw new DocumentException($"Unknown aggregate \"{aggregate.Function}\". Use one of: {string.Join(", ", Functions)}.");
        }

        return aggregate.Column is { Length: > 0 } name ? Column(table, name) : aggregate.Function == "count" ? null
            : throw new DocumentException($"{aggregate.Function} needs a column.");
    }

    private static string Label(TableData table, TableAggregate aggregate, int? column) =>
        column is { } at ? $"{aggregate.Function}({table.Header[at]})" : aggregate.Function;

    /// <summary>Столбец таблицы по заголовку, иначе по букве; нет такого — отказ со списком заголовков.</summary>
    private static int Column(TableData table, string name)
    {
        var wanted = name.Trim();
        for (var column = 0; column < table.Header.Count; column++)
        {
            if (string.Equals(table.Header[column], wanted, StringComparison.OrdinalIgnoreCase))
            {
                return column;
            }
        }

        if (wanted.Length is > 0 and <= 3 && wanted.All(char.IsAsciiLetter) &&
            CellAddress.TryParse(wanted + "1", out var letter, out _) && letter <= table.Header.Count)
        {
            return letter - 1;
        }

        throw new DocumentException($"No column \"{name}\". The columns are: {string.Join(", ", table.Header.Select(header => "\"" + header + "\""))}.");
    }

    /// <summary>Столбец результата: заголовок как есть или столбец, по которому считали итог.</summary>
    private static int OutputColumn(IReadOnlyList<string> header, string name)
    {
        var wanted = name.Trim();
        var exact = header.ToList().FindIndex(item => string.Equals(item, wanted, StringComparison.OrdinalIgnoreCase));
        if (exact >= 0)
        {
            return exact;
        }

        var measured = header.ToList().FindIndex(item => item.EndsWith("(" + wanted + ")", StringComparison.OrdinalIgnoreCase));
        return measured >= 0
            ? measured
            : throw new DocumentException($"No column \"{name}\" in the result. Its columns are: {string.Join(", ", header.Select(item => "\"" + item + "\""))}.");
    }

    private static string Row(IEnumerable<string> cells) =>
        "| " + string.Join(" | ", cells.Select(cell => cell.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' '))) + " |";

    /// <summary>Числа — как числа, остальное — как текст; число раньше текста, пустое — в конце.</summary>
    private sealed class ValueComparer : IComparer<string>
    {
        public static readonly ValueComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var left = x ?? "";
            var right = y ?? "";
            if (left.Length == 0 || right.Length == 0)
            {
                return (left.Length == 0).CompareTo(right.Length == 0);
            }

            var leftNumber = TryNumber(left, out var a);
            var rightNumber = TryNumber(right, out var b);
            return (leftNumber, rightNumber) switch
            {
                (true, true) => a.CompareTo(b),
                (true, false) => -1,
                (false, true) => 1,
                _ => CultureInfo.CurrentCulture.CompareInfo.Compare(left, right, CompareOptions.IgnoreCase)
            };
        }
    }
}
