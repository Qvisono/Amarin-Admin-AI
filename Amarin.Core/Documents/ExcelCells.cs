using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Amarin.Core;

/// <summary>Значение ячейки, как его понял разбор: число, дата, процент, формула, логическое или текст.</summary>
internal readonly record struct CellInput(CellInputKind Kind, string Text, double Number = 0)
{
    public static CellInput Empty { get; } = new(CellInputKind.Empty, "");
}

internal enum CellInputKind
{
    Empty,
    Text,
    Number,
    Percent,
    Date,
    DateTime,
    Boolean,
    Formula
}

/// <summary>
/// Значения ячеек из того, что пишет модель, и запись их в лист — для новой книги и для правки чужой.
/// </summary>
/// <remarks>
/// Число остаётся числом, дата — датой: иначе в собранной таблице не работали бы ни сумма, ни
/// сортировка, ни фильтр, а Excel ставил бы на каждую ячейку зелёный уголок «число как текст».
/// Номера телефонов, коды с ведущими нулями и длинные номера счетов — текст: числом они бы
/// потеряли нули и знаки.
/// </remarks>
internal static partial class ExcelCells
{
    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"];

    public static CellInput Parse(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out var number) => new CellInput(CellInputKind.Number, value.GetRawText(), number),
        JsonValueKind.True => new CellInput(CellInputKind.Boolean, "1"),
        JsonValueKind.False => new CellInput(CellInputKind.Boolean, "0"),
        JsonValueKind.Null or JsonValueKind.Undefined => CellInput.Empty,
        JsonValueKind.String => Parse(value.GetString()),
        _ => new CellInput(CellInputKind.Text, value.GetRawText())
    };

    public static CellInput Parse(string? raw)
    {
        var text = raw ?? "";
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return CellInput.Empty;
        }

        if (trimmed.StartsWith('=') && trimmed.Length > 1)
        {
            return new CellInput(CellInputKind.Formula, trimmed[1..]);
        }

        if (trimmed.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
        {
            return new CellInput(CellInputKind.Boolean, trimmed.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ? "1" : "0");
        }

        if (NumberText().IsMatch(trimmed) &&
            double.TryParse(trimmed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return new CellInput(CellInputKind.Number, trimmed, number);
        }

        if (trimmed.EndsWith('%') && NumberText().IsMatch(trimmed[..^1].Trim()) &&
            double.TryParse(trimmed[..^1].Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return new CellInput(CellInputKind.Percent, trimmed, percent / 100);
        }

        if (DateText().IsMatch(trimmed) &&
            DateTime.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return new CellInput(date.TimeOfDay == TimeSpan.Zero ? CellInputKind.Date : CellInputKind.DateTime, trimmed, date.ToOADate());
        }

        return new CellInput(CellInputKind.Text, text);
    }

    /// <summary>
    /// Число без ведущего нуля (кроме «0» и «0.5»), не длиннее 15 значащих цифр — дальше Excel
    /// теряет точность, и номер карты превратился бы в другой.
    /// </summary>
    /// <remarks>
    /// Запятая — десятичная, только если после неё не ровно три цифры: «1,5» — полтора, а «1,234» —
    /// скорее тысяча двести тридцать четыре, и угадывать здесь нельзя, поэтому такое остаётся текстом.
    /// Порядка вида «1E10» в строке нет: так пишут коды и артикулы, а не числа; число с порядком
    /// модель передаёт числом JSON.
    /// </remarks>
    [GeneratedRegex(@"^-?(0|[1-9]\d{0,14})(\.\d{1,15}|,(\d{1,2}|\d{4,15}))?$")]
    private static partial Regex NumberText();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}([ T]\d{2}:\d{2}(:\d{2})?)?$")]
    private static partial Regex DateText();

    /// <summary>
    /// Ставит значение в ячейку листа: строка и ячейка создаются, если их нет, на своём месте по порядку.
    /// </summary>
    /// <remarks>Excel требует строки по возрастанию номера и ячейки в строке — по столбцам; иначе книга «повреждена».</remarks>
    public static void Set(S.SheetData data, int column, int row, CellInput input, ExcelStyleIds styles, bool bold = false)
    {
        var line = Row(data, row);
        var reference = CellAddress.Of(column, row);
        var cell = line.Elements<S.Cell>().FirstOrDefault(c => string.Equals(c.CellReference?.Value, reference, StringComparison.OrdinalIgnoreCase));
        if (input.Kind == CellInputKind.Empty)
        {
            // Пустое значение — очистка, а не снос: у оформленной ячейки рамка и заливка остаются.
            if (cell?.StyleIndex?.Value is null or 0)
            {
                cell?.Remove();
            }
            else
            {
                cell.CellFormula = null;
                cell.CellValue = null;
                cell.InlineString = null;
                cell.DataType = null;
            }

            return;
        }

        if (cell is null)
        {
            cell = new S.Cell { CellReference = reference };
            var after = line.Elements<S.Cell>().LastOrDefault(c => CellAddress.TryParse(c.CellReference?.Value, out var at, out _) && at < column);
            if (after is null)
            {
                line.PrependChild(cell);
            }
            else
            {
                line.InsertAfter(cell, after);
            }
        }

        Fill(cell, input, styles, bold);
    }

    private static S.Row Row(S.SheetData data, int number)
    {
        var index = (uint)number;
        S.Row? before = null;
        foreach (var row in data.Elements<S.Row>())
        {
            var at = row.RowIndex?.Value ?? 0;
            if (at == index)
            {
                return row;
            }

            if (at > index)
            {
                break;
            }

            before = row;
        }

        var created = new S.Row { RowIndex = index };
        if (before is null)
        {
            data.PrependChild(created);
        }
        else
        {
            data.InsertAfter(created, before);
        }

        return created;
    }

    private static void Fill(S.Cell cell, CellInput input, ExcelStyleIds styles, bool bold)
    {
        var existing = cell.StyleIndex?.Value;
        cell.CellFormula = null;
        cell.CellValue = null;
        cell.InlineString = null;
        cell.DataType = null;
        cell.StyleIndex = StyleFor(input.Kind, existing, styles, bold) is { } style ? style : null;
        switch (input.Kind)
        {
            case CellInputKind.Formula:
                cell.CellFormula = new S.CellFormula(input.Text);
                break;
            case CellInputKind.Number or CellInputKind.Percent or CellInputKind.Date or CellInputKind.DateTime:
                cell.CellValue = new S.CellValue(input.Number.ToString("R", CultureInfo.InvariantCulture));
                break;
            case CellInputKind.Boolean:
                cell.DataType = S.CellValues.Boolean;
                cell.CellValue = new S.CellValue(input.Text);
                break;
            default:
                // Строкой в самой ячейке, а не в общей таблице строк: так правка чужой книги не
                // трогает её общую таблицу, а Excel при сохранении сам переложит их как ему удобно.
                cell.DataType = S.CellValues.InlineString;
                cell.InlineString = new S.InlineString(new S.Text(input.Text) { Space = SpaceProcessingModeValues.Preserve });
                break;
        }
    }

    /// <summary>
    /// Оформление ячейки после записи: у правленой ячейки — её собственное, у новой — нужное значению.
    /// </summary>
    /// <remarks>
    /// До 1.33.0 запись сбрасывала оформление каждой ячейки, которой касалась: в чужом отчёте после
    /// правки трёх цифр пропадали заливка, рамки и формат валюты. Теперь у ячейки остаётся её вид, а
    /// формат числа меняется, только если значение без него читалось бы не тем (дата числом,
    /// процент дробью) — и тогда это та же ячейка с другим форматом числа, а не голая.
    /// </remarks>
    private static uint? StyleFor(CellInputKind kind, uint? existing, ExcelStyleIds styles, bool bold)
    {
        if (bold)
        {
            return styles.Header;
        }

        uint? wanted = kind switch
        {
            CellInputKind.Percent => styles.Percent,
            CellInputKind.Date => styles.Date,
            CellInputKind.DateTime => styles.DateTime,
            _ => null
        };

        if (wanted is null || existing is null or 0)
        {
            return wanted ?? existing;
        }

        return styles.Restyle?.Invoke(existing.Value, kind) ?? wanted;
    }

    /// <summary>
    /// Книге с правленными значениями — пересчитать формулы при открытии: сохранённые в файле итоги
    /// посчитаны по прежним числам.
    /// </summary>
    public static void RecalculateOnOpen(WorkbookPart workbook)
    {
        if (workbook.Workbook is not { } book)
        {
            return;
        }

        var calculation = book.CalculationProperties ?? book.AppendChild(new S.CalculationProperties());
        calculation.FullCalculationOnLoad = true;
    }
}

/// <summary>Номера оформлений ячеек книги: шапка, дата, дата со временем, процент.</summary>
/// <param name="Restyle">
/// Оформление «как у этой ячейки, но с форматом числа для такого значения» — найденное в книге или
/// новое. Null — у книги таблицы стилей нет, и брать вид не у кого.
/// </param>
internal readonly record struct ExcelStyleIds(
    uint Header,
    uint Date,
    uint DateTime,
    uint Percent,
    Func<uint, CellInputKind, uint>? Restyle = null);

/// <summary>Оформления, которые нужны записи, — в таблице стилей книги: свои или дописанные к чужим.</summary>
internal static class ExcelStyles
{
    private const string DateCode = "yyyy-mm-dd";
    private const string DateTimeCode = "yyyy-mm-dd hh:mm";

    public static ExcelStyleIds Ensure(WorkbookPart workbook)
    {
        var part = workbook.WorkbookStylesPart ?? workbook.AddNewPart<WorkbookStylesPart>();
        part.Stylesheet ??= NewStylesheet();
        var sheet = part.Stylesheet;

        sheet.NumberingFormats ??= new S.NumberingFormats();
        sheet.Fonts ??= new S.Fonts(new S.Font());
        sheet.Fills ??= new S.Fills(new S.Fill(new S.PatternFill { PatternType = S.PatternValues.None }), new S.Fill(new S.PatternFill { PatternType = S.PatternValues.Gray125 }));
        sheet.Borders ??= new S.Borders(new S.Border());
        sheet.CellFormats ??= new S.CellFormats(new S.CellFormat());
        sheet.CellStyleFormats ??= new S.CellStyleFormats(new S.CellFormat());

        var dateFormat = NumberFormat(sheet.NumberingFormats, DateCode);
        var dateTimeFormat = NumberFormat(sheet.NumberingFormats, DateTimeCode);

        // Всё ищется перед тем, как заводиться: вторая правка той же книги не должна плодить
        // в ней ещё один жирный шрифт и ещё четыре одинаковых оформления.
        var boldFont = Index(sheet.Fonts.Elements<S.Font>(), font => font.ChildElements.Count == 1 && font.Bold is not null)
                       ?? Append(sheet.Fonts, new S.Font(new S.Bold()));
        var headerFill = Index(sheet.Fills.Elements<S.Fill>(), fill => fill.PatternFill?.ForegroundColor?.Rgb?.Value == "FFF2F2F2")
                         ?? Append(sheet.Fills, new S.Fill(new S.PatternFill(new S.ForegroundColor { Rgb = "FFF2F2F2" }) { PatternType = S.PatternValues.Solid }));

        var formats = sheet.CellFormats;
        var header = Format(formats, boldFont, headerFill, 0U);
        var date = Format(formats, 0U, 0U, dateFormat);
        var dateTime = Format(formats, 0U, 0U, dateTimeFormat);
        var percent = Format(formats, 0U, 0U, 10U);

        sheet.NumberingFormats.Count = (uint)sheet.NumberingFormats.ChildElements.Count;
        sheet.Fonts.Count = (uint)sheet.Fonts.ChildElements.Count;
        sheet.Fills.Count = (uint)sheet.Fills.ChildElements.Count;
        formats.Count = (uint)formats.ChildElements.Count;

        uint FormatOf(CellInputKind kind) => kind switch
        {
            CellInputKind.Date => dateFormat,
            CellInputKind.DateTime => dateTimeFormat,
            _ => 10U
        };

        return new ExcelStyleIds(header, date, dateTime, percent,
            (existing, kind) => WithNumberFormat(sheet, formats, existing, kind, FormatOf(kind)));
    }

    /// <summary>
    /// Оформление ячейки <paramref name="existing"/> с форматом числа для значения этого вида. Если
    /// её формат уже подходит (дата любым из форматов дат, процент — любым процентным), остаётся её.
    /// </summary>
    private static uint WithNumberFormat(S.Stylesheet sheet, S.CellFormats formats, uint existing, CellInputKind kind, uint numberFormat)
    {
        if (formats.Elements<S.CellFormat>().ElementAtOrDefault((int)existing) is not { } current)
        {
            return existing;
        }

        if (Suits(sheet, current.NumberFormatId?.Value ?? 0, kind))
        {
            return existing;
        }

        var clone = (S.CellFormat)current.CloneNode(true);
        clone.NumberFormatId = numberFormat;
        clone.ApplyNumberFormat = true;
        var xml = clone.OuterXml;
        var found = Index(formats.Elements<S.CellFormat>(), format => format.OuterXml == xml);
        if (found is { } index)
        {
            return index;
        }

        var added = Append(formats, clone);
        formats.Count = (uint)formats.ChildElements.Count;
        return added;
    }

    /// <summary>
    /// Годится ли формат числа для значения: встроенные форматы дат (14–22, 45–47) и процентов (9, 10),
    /// свои — по коду.
    /// </summary>
    private static bool Suits(S.Stylesheet sheet, uint numberFormat, CellInputKind kind)
    {
        var code = sheet.NumberingFormats?.Elements<S.NumberingFormat>()
            .FirstOrDefault(format => format.NumberFormatId?.Value == numberFormat)?.FormatCode?.Value ?? "";
        return kind switch
        {
            CellInputKind.Percent => numberFormat is 9 or 10 || code.Contains('%', StringComparison.Ordinal),
            CellInputKind.Date or CellInputKind.DateTime =>
                numberFormat is (>= 14 and <= 22) or (>= 45 and <= 47) ||
                (code.Length > 0 && code.Any(c => c is 'y' or 'Y' or 'd' or 'D') && !code.Contains('#', StringComparison.Ordinal)),
            _ => true
        };
    }

    private static S.Stylesheet NewStylesheet() => new();

    private static uint? Index<T>(IEnumerable<T> items, Func<T, bool> match)
    {
        var index = 0U;
        foreach (var item in items)
        {
            if (match(item))
            {
                return index;
            }

            index++;
        }

        return null;
    }

    private static uint Append(OpenXmlCompositeElement list, OpenXmlElement item)
    {
        list.Append(item);
        return (uint)list.ChildElements.Count - 1;
    }

    /// <summary>Оформление ячейки с этими шрифтом, заливкой и форматом числа — найденное или новое.</summary>
    private static uint Format(S.CellFormats formats, uint font, uint fill, uint numberFormat) =>
        Index(formats.Elements<S.CellFormat>(), format =>
            (format.FontId?.Value ?? 0) == font &&
            (format.FillId?.Value ?? 0) == fill &&
            (format.NumberFormatId?.Value ?? 0) == numberFormat &&
            (format.BorderId?.Value ?? 0) == 0 &&
            format.Alignment is null)
        ?? Append(formats, new S.CellFormat
        {
            FontId = font,
            FillId = fill,
            NumberFormatId = numberFormat,
            ApplyFont = font != 0 ? true : null,
            ApplyFill = fill != 0 ? true : null,
            ApplyNumberFormat = numberFormat != 0 ? true : null
        });

    /// <summary>Свой формат числа: найденный по коду или заведённый с первым свободным номером (с 164).</summary>
    private static uint NumberFormat(S.NumberingFormats formats, string code)
    {
        var existing = formats.Elements<S.NumberingFormat>().FirstOrDefault(f => f.FormatCode?.Value == code);
        if (existing?.NumberFormatId?.Value is { } id)
        {
            return id;
        }

        var next = Math.Max(164U, formats.Elements<S.NumberingFormat>().Select(f => (f.NumberFormatId?.Value ?? 0) + 1).DefaultIfEmpty(164U).Max());
        formats.Append(new S.NumberingFormat { NumberFormatId = next, FormatCode = code });
        return next;
    }
}
