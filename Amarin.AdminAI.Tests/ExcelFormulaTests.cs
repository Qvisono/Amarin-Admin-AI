using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Amarin.AdminAI.Tests;

/// <summary>Подсчёт формул Excel: порядок операций и функции — как в Excel, неизвестное — не угадывается.</summary>
public sealed class ExcelCalcTests
{
    private static ExcelCalc Calc(Dictionary<string, object> cells, string sheet = "S") => new((name, column, row) =>
    {
        var key = (name ?? sheet) + "!" + CellAddress.Of(column, row);
        if (!cells.TryGetValue(key, out var value))
        {
            return ExcelCalc.CellSource.None(name ?? sheet);
        }

        return value switch
        {
            string text when text.StartsWith('=') => new ExcelCalc.CellSource(XValue.Empty, text[1..], Sheet: name ?? sheet),
            string text => new ExcelCalc.CellSource(XValue.Of(text), Sheet: name ?? sheet),
            double number => new ExcelCalc.CellSource(XValue.Of(number), Sheet: name ?? sheet),
            _ => throw new ArgumentException("cell")
        };
    });

    private static XValue? Eval(string formula, Dictionary<string, object>? cells = null)
    {
        var all = new Dictionary<string, object>(cells ?? []) { ["S!Z99"] = formula };
        return Calc(all).TryValue("S", 26, 99);
    }

    [Theory]
    [InlineData("=1+2*3", 7)]
    [InlineData("=(1+2)*3", 9)]
    [InlineData("=-2^2", 4)]
    [InlineData("=2^3^2", 64)]
    [InlineData("=50%*10", 5)]
    [InlineData("=10/4", 2.5)]
    [InlineData("=ROUND(2.345, 2)", 2.35)]
    [InlineData("=ROUND(-2.5, 0)", -3)]
    [InlineData("=ROUNDDOWN(2.99, 0)", 2)]
    [InlineData("=MOD(-7, 3)", 2)]
    [InlineData("=IF(3>2, 10, 20)", 10)]
    [InlineData("=SUM(1, 2, 3)*2", 12)]
    public void Arithmetic_follows_excel(string formula, double expected)
    {
        var value = Eval(formula);

        Assert.NotNull(value);
        Assert.Equal(XKind.Number, value.Value.Kind);
        Assert.Equal(expected, value.Value.Number, 9);
    }

    [Fact]
    public void References_ranges_and_other_sheets_are_followed()
    {
        var cells = new Dictionary<string, object>
        {
            ["S!A1"] = 2.0,
            ["S!A2"] = 3.0,
            ["S!A3"] = "text",
            ["S!B1"] = "=A1*A2",
            ["Other sheet!C5"] = 100.0
        };

        Assert.Equal(6, Eval("=B1", cells)!.Value.Number);
        Assert.Equal(5, Eval("=SUM(A1:A3)", cells)!.Value.Number);
        Assert.Equal(2.5, Eval("=AVERAGE(A1:A3)", cells)!.Value.Number);
        Assert.Equal(2, Eval("=COUNT(A1:A3)", cells)!.Value.Number);
        Assert.Equal(106, Eval("='Other sheet'!C5+B1", cells)!.Value.Number);
        Assert.Equal("2 x 3", Eval("=A1&\" x \"&A2", cells)!.Value.Text);
        Assert.Equal(0, Eval("=A9", cells)!.Value.Number);
    }

    [Fact]
    public void Errors_are_values_and_the_unknown_is_left_uncalculated()
    {
        Assert.Equal("#DIV/0!", Eval("=1/0")!.Value.Text);
        Assert.Equal(XKind.Error, Eval("=1/0")!.Value.Kind);
        Assert.Equal(7, Eval("=IFERROR(1/0, 7)")!.Value.Number);

        // Динамические массивы и круг ссылок не угадываются: их посчитает Excel.
        Assert.Null(Eval("=VSTACK(1, 2)"));
        Assert.Null(Calc(new Dictionary<string, object> { ["S!A1"] = "=B1", ["S!B1"] = "=A1" }).TryValue("S", 1, 1));
    }

    [Fact]
    public void Row_and_column_name_where_the_formula_is()
    {
        Assert.Equal(26 * 99, Eval("=ROW()*COLUMN()")!.Value.Number);
        Assert.Equal(5, Eval("=ROW(B5)")!.Value.Number);
    }
}

/// <summary>Заполнение диапазона, как протяжка в Excel.</summary>
public sealed class ExcelFillTests
{
    [Theory]
    [InlineData("B$1*$A2", 2, 3, "D$1*$A5")]
    [InlineData("SUM(A1:A3)", 1, 0, "SUM(B1:B3)")]
    [InlineData("\"A1\"&A1", 1, 1, "\"A1\"&B2")]
    [InlineData("'Q1 2024'!A1+B2", 1, 0, "'Q1 2024'!B1+C2")]
    [InlineData("LOG10(A1)", 0, 1, "LOG10(A2)")]
    [InlineData("$A$1", 5, 5, "$A$1")]
    public void Relative_references_move_and_fixed_ones_stay(string formula, int columns, int rows, string expected)
    {
        Assert.Equal(expected, ExcelFill.Shift(formula, columns, rows));
    }

    [Fact]
    public void A_reference_moved_off_the_sheet_is_refused_with_a_hint()
    {
        var error = Assert.Throws<DocumentException>(() => ExcelFill.Shift("A2", 0, -5));
        Assert.Contains("$", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_number_with_a_step_becomes_a_series()
    {
        var cells = ExcelFill.Expand(new CellFill("B1:E1", ExcelCells.Parse("1"), 1)).ToList();

        Assert.Equal([1.0, 2, 3, 4], cells.Select(cell => cell.Input.Number));
        Assert.Equal([2, 3, 4, 5], cells.Select(cell => cell.Column));
    }
}

/// <summary>
/// «Создай Excel с таблицей умножения до 150, а потом PDF с тем же»: три строки заполнения вместо
/// двадцати двух тысяч чисел, а PDF — из книги одним вызовом, без единого пропавшего столбца.
/// </summary>
public sealed class LargeTableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-large-table-" + Guid.NewGuid().ToString("N"));

    public LargeTableTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private async Task<string> MultiplicationTable()
    {
        var path = Path.Combine(_root, "table.xlsx");
        var result = await new CreateDocumentTool().ExecuteAsync(Json(new
        {
            path,
            sheets = new[]
            {
                new
                {
                    name = "Таблица",
                    rows = new[] { new[] { "×" } },
                    fill = new object[]
                    {
                        new { range = "B1:EU1", value = "1", step = 1 },
                        new { range = "A2:A151", value = "1", step = 1 },
                        new { range = "B2:EU151", value = "=B$1*$A2" }
                    }
                }
            }
        }));
        Assert.True(result.Success, result.Output);
        return path;
    }

    [Fact]
    public async Task A_table_built_by_fill_knows_its_values_before_excel_opens_it()
    {
        var path = await MultiplicationTable();

        using var document = SpreadsheetDocument.Open(path, false);
        var cells = document.WorkbookPart!.WorksheetParts.Single().Worksheet!.Descendants<S.Cell>().ToDictionary(cell => cell.CellReference!.Value!);

        Assert.Equal("B$1*$A2", cells["B2"].CellFormula!.Text);
        Assert.Equal("EU$1*$A151", cells["EU151"].CellFormula!.Text);
        Assert.Equal("22500", cells["EU151"].CellValue!.Text);
        Assert.Equal("150", cells["EU1"].CellValue!.Text);
        Assert.Equal(151 * 151, cells.Count);
    }

    [Fact]
    public async Task A_pdf_is_made_from_the_workbook_in_one_call_with_every_value()
    {
        var workbook = await MultiplicationTable();
        var pdf = Path.Combine(_root, "table.pdf");

        var result = await new CreateDocumentTool().ExecuteAsync(Json(new { path = pdf, source = workbook }));

        Assert.True(result.Success, result.Output);
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var text = string.Concat(document.GetPages().Select(page => page.Text));
        Assert.Contains("22500", text, StringComparison.Ordinal);
        Assert.Contains("22350", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fill_in_an_existing_workbook_writes_and_calculates_the_range()
    {
        var path = await MultiplicationTable();

        var result = await new EditDocumentTool().ExecuteAsync(Json(new
        {
            path,
            operations = new[] { new { op = "fill", sheet = "Таблица", range = "B153:EU153", value = "=SUM(B2:B151)" } }
        }));

        Assert.True(result.Success, result.Output);
        Assert.Contains("filled 150 cell(s)", result.Output, StringComparison.Ordinal);
        Assert.Contains("Formulas calculated:", result.Output, StringComparison.Ordinal);
        using var document = SpreadsheetDocument.Open(path, false);
        var total = document.WorkbookPart!.WorksheetParts.Single().Worksheet!.Descendants<S.Cell>().Single(cell => cell.CellReference?.Value == "B153");
        Assert.Equal("11325", total.CellValue!.Text);
    }
}

/// <summary>Документ из документа: перенос делает программа, а не модель перепечаткой.</summary>
public sealed class DocumentConvertTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-convert-" + Guid.NewGuid().ToString("N"));

    public DocumentConvertTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static string PdfText(string path)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        return string.Concat(document.GetPages().Select(page => page.Text));
    }

    [Fact]
    public async Task Word_and_csv_become_pdf_with_their_text()
    {
        var word = Path.Combine(_root, "report.docx");
        WordWriter.Create(word, "# Quarterly report\n\nRevenue grew.\n\n| Item | Sum |\n|---|---|\n| Rent | 1200 |", _ => null);
        var csv = Path.Combine(_root, "data.csv");
        File.WriteAllText(csv, "City;Population\nKazan;1300000\n");

        Assert.True((await new CreateDocumentTool().ExecuteAsync(Json(new { path = Path.Combine(_root, "report.pdf"), source = word }))).Success);
        Assert.True((await new CreateDocumentTool().ExecuteAsync(Json(new { path = Path.Combine(_root, "data.pdf"), source = csv }))).Success);

        var report = PdfText(Path.Combine(_root, "report.pdf"));
        Assert.Contains("Quarterly report", report, StringComparison.Ordinal);
        Assert.Contains("1200", report, StringComparison.Ordinal);
        Assert.Contains("Kazan", PdfText(Path.Combine(_root, "data.pdf")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_workbook_whose_formulas_nobody_calculated_is_refused_with_what_to_do()
    {
        var path = Path.Combine(_root, "dynamic.xlsx");
        using (var document = SpreadsheetDocument.Create(path, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook(new S.Sheets());
            var part = workbook.AddNewPart<WorksheetPart>();
            part.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(new S.Cell
            {
                CellReference = "A1",
                CellFormula = new S.CellFormula("_xlfn.VSTACK(1,2)")
            })
            { RowIndex = 1 }));
            workbook.Workbook.Sheets!.Append(new S.Sheet { Id = workbook.GetIdOfPart(part), SheetId = 1, Name = "Sheet1" });
        }

        var result = await new CreateDocumentTool().ExecuteAsync(Json(new { path = Path.Combine(_root, "dynamic.pdf"), source = path }));

        Assert.False(result.Success);
        Assert.Contains("fill", result.Output, StringComparison.Ordinal);
        Assert.Contains("agent", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_secret_is_not_read_through_a_conversion()
    {
        var key = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_rsa");

        var result = await new CreateDocumentTool().ExecuteAsync(Json(new { path = Path.Combine(_root, "key.pdf"), source = key }));

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(_root, "key.pdf")));
    }
}
