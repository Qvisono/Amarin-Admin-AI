using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Документы, которые собирает и правит программа, открываются в Word и Excel без окна «найдено
/// содержимое, которое не удалось прочитать», а правка чужой таблицы не сносит её оформление.
/// </summary>
public sealed class DocumentWriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amarin-docs-" + Guid.NewGuid().ToString("N"));

    public DocumentWriteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static void AssertValid(OpenXmlPackage package)
    {
        var errors = new OpenXmlValidator(FileFormatVersions.Office2016).Validate(package)
            .Select(error => $"{error.Path?.XPath}: {error.Description}")
            .ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    private const string Markdown = """
        # Отчёт за квартал

        Обычный абзац с **жирным**, *курсивом*, `кодом` и **`жирным кодом`**, ~~зачёркнутым~~ и [ссылкой](https://example.com).

        ## Список

        - первый пункт
        - второй пункт
          1. вложенный

        > Цитата в два слова.

        | Месяц | Сумма |
        |---|---|
        | Июль | 120 |
        | Август | 140 |

        ```
        код в блоке
        ```
        """;

    [Fact]
    public void A_new_Word_document_passes_the_OpenXML_schema()
    {
        var path = PathOf("new.docx");

        WordWriter.Create(path, Markdown, _ => null);

        using var document = WordprocessingDocument.Open(path, false);
        AssertValid(document);
    }

    [Fact]
    public void Inserting_into_a_Russian_Word_document_reuses_its_own_heading_style()
    {
        // В документе из русского Word «heading 1» лежит под идентификатором «1».
        var path = PathOf("ru.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text("Текст")))));
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new W.Styles(
                new W.Style(new W.StyleName { Val = "Normal" }) { Type = W.StyleValues.Paragraph, StyleId = "a", Default = true },
                new W.Style(new W.StyleName { Val = "heading 1" }, new W.BasedOn { Val = "a" }) { Type = W.StyleValues.Paragraph, StyleId = "1" });
        }

        using (var editor = WordEditor.Open(path, _ => null))
        {
            editor.Append("# Новый раздел");
            editor.Save();
        }

        using var result = WordprocessingDocument.Open(path, false);
        var defined = result.MainDocumentPart?.StyleDefinitionsPart?.Styles?.Elements<W.Style>().ToList() ?? [];
        Assert.Single(defined, style => style.StyleName?.Val?.Value == "heading 1");
        var heading = result.MainDocumentPart?.Document?.Body?.Elements<W.Paragraph>().Last();
        Assert.Equal("1", heading?.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
        AssertValid(result);
    }

    [Fact]
    public void A_fragment_copied_from_the_read_with_its_markup_is_still_found()
    {
        var path = PathOf("edit.docx");
        WordWriter.Create(path, "# Итоги\n\nВыручка выросла на **12%** за квартал.", _ => null);

        using (var editor = WordEditor.Open(path, _ => null))
        {
            // Чтение показывает «[2] Выручка выросла на **12%** за квартал.» — так модель его и копирует.
            var (count, _) = editor.Replace("[2] Выручка выросла на **12%**", "Выручка выросла на **15%**", all: false);
            Assert.Equal(1, count);
            editor.Save();
        }

        using var document = WordprocessingDocument.Open(path, false);
        var text = document.MainDocumentPart?.Document?.Body?.InnerText ?? "";
        Assert.Contains("Выручка выросла на 15% за квартал.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("**", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_workbook_passes_the_OpenXML_schema()
    {
        var path = PathOf("new.xlsx");
        List<IReadOnlyList<CellInput>> rows =
        [
            [ExcelCells.Parse("Месяц"), ExcelCells.Parse("Сумма"), ExcelCells.Parse("Дата"), ExcelCells.Parse("Доля")],
            [ExcelCells.Parse("Июль"), ExcelCells.Parse("120"), ExcelCells.Parse("2026-07-01"), ExcelCells.Parse("12%")],
            [ExcelCells.Parse("Итого"), ExcelCells.Parse("=SUM(B2:B2)"), CellInput.Empty, CellInput.Empty]
        ];

        ExcelWriter.Create(path, [new SheetInput("Отчёт", rows)]);

        using var document = SpreadsheetDocument.Open(path, false);
        AssertValid(document);
    }

    [Theory]
    [InlineData("1,5", "Number")]
    [InlineData("1.25", "Number")]
    [InlineData("1,234", "Text")]
    [InlineData("1E10", "Text")]
    [InlineData("007", "Text")]
    [InlineData("12%", "Percent")]
    [InlineData("2026-10-09", "Date")]
    [InlineData("=A1+B1", "Formula")]
    [InlineData("TRUE", "Boolean")]
    public void Cell_text_keeps_its_type_only_when_it_is_unambiguous(string text, string kind)
    {
        Assert.Equal(kind, ExcelCells.Parse(text).Kind.ToString());
    }

    [Fact]
    public void A_JSON_number_with_an_exponent_is_still_a_number()
    {
        Assert.Equal(CellInputKind.Number, ExcelCells.Parse(JsonSerializer.SerializeToElement(1e10)).Kind);
    }

    [Fact]
    public void Editing_a_styled_cell_keeps_its_look_and_drops_the_stale_calc_chain()
    {
        var path = PathOf("styled.xlsx");
        ExcelWriter.Create(path, [new SheetInput("Лист1", [
            [ExcelCells.Parse("Статья"), ExcelCells.Parse("Сумма")],
            [ExcelCells.Parse("Аренда"), ExcelCells.Parse("100")],
            [ExcelCells.Parse("Итого"), ExcelCells.Parse("=SUM(B2:B2)")]])]);
        uint headerStyle;
        using (var document = SpreadsheetDocument.Open(path, true))
        {
            var workbook = document.WorkbookPart ?? throw new InvalidOperationException();
            var chain = workbook.AddNewPart<CalculationChainPart>();
            chain.CalculationChain = new S.CalculationChain(new S.CalculationCell { CellReference = "B3", SheetId = 1 });
            headerStyle = Cell(workbook, "A1").StyleIndex?.Value ?? 0;
        }

        Assert.NotEqual(0U, headerStyle);
        using (var editor = ExcelEditor.Open(path))
        {
            editor.SetCells("Лист1", [("A1", ExcelCells.Parse("Новая статья")), ("B2", ExcelCells.Parse("250"))]);
            editor.Clear("Лист1", "A1:A1");
            editor.SetCells("Лист1", [("A1", ExcelCells.Parse("Статья расходов"))]);
            editor.Save();
        }

        using var result = SpreadsheetDocument.Open(path, false);
        var book = result.WorkbookPart ?? throw new InvalidOperationException();
        Assert.Null(book.CalculationChainPart);
        Assert.Equal(headerStyle, Cell(book, "A1").StyleIndex?.Value ?? 0);
        Assert.True(book.Workbook?.CalculationProperties?.FullCalculationOnLoad?.Value);
        AssertValid(result);
    }

    [Fact]
    public void Renaming_a_sheet_rewrites_only_whole_references_to_it()
    {
        var path = PathOf("rename.xlsx");
        ExcelWriter.Create(path,
        [
            new SheetInput("Sheet1", [[ExcelCells.Parse("1")]], Header: false),
            new SheetInput("MySheet1", [[ExcelCells.Parse("2")]], Header: false),
            new SheetInput("Итог", [[ExcelCells.Parse("=Sheet1!A1+MySheet1!A1")]], Header: false)
        ]);

        using (var editor = ExcelEditor.Open(path))
        {
            editor.RenameSheet("Sheet1", "Доходы 2026");
            editor.Save();
        }

        using var result = SpreadsheetDocument.Open(path, false);
        var book = result.WorkbookPart ?? throw new InvalidOperationException();
        var formula = book.WorksheetParts.SelectMany(part => part.Worksheet?.Descendants<S.CellFormula>() ?? []).Single().Text;
        Assert.Equal("'Доходы 2026'!A1+MySheet1!A1", formula);
    }

    [Fact]
    public async Task Set_cells_accepts_the_list_form_of_the_schema()
    {
        var path = PathOf("cells.xlsx");
        ExcelWriter.Create(path, [new SheetInput("Лист1", [[ExcelCells.Parse("a")]], Header: false)]);

        var result = await new EditDocumentTool().ExecuteAsync(JsonSerializer.SerializeToElement(new
        {
            path,
            operations = new object[]
            {
                new { op = "set_cells", sheet = "Лист1", cells = new object[] { new { cell = "B2", value = "12.5" }, new { cell = "C2", value = "=B2*2" } } }
            }
        }));

        Assert.True(result.Success, result.Output);
        using var document = SpreadsheetDocument.Open(path, false);
        var book = document.WorkbookPart ?? throw new InvalidOperationException();
        Assert.Equal("12.5", Cell(book, "B2").CellValue?.Text);
        Assert.Equal("B2*2", Cell(book, "C2").CellFormula?.Text);
    }

    [Fact]
    public void A_short_PDF_is_not_six_whole_fonts_heavy()
    {
        var path = PathOf("short.pdf");

        PdfWriter.Create(path, "Короткий текст без начертаний.", _ => null);

        // Каждое добавленное начертание встраивается целиком: обычный текст — один шрифт.
        var oneFont = new FileInfo(path).Length;
        PdfWriter.Create(PathOf("styled.pdf"), "Текст с **жирным**, *курсивом* и `кодом`.", _ => null);
        Assert.True(new FileInfo(PathOf("styled.pdf")).Length > oneFont);
        Assert.True(oneFont < 2_500_000, $"{oneFont} bytes");
    }

    private static S.Cell Cell(WorkbookPart workbook, string reference) =>
        workbook.WorksheetParts
            .SelectMany(part => part.Worksheet?.Descendants<S.Cell>() ?? [])
            .First(cell => cell.CellReference?.Value == reference);
}
