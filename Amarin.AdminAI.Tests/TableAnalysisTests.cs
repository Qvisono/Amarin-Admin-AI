using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using C = DocumentFormat.OpenXml.Drawing.Charts;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Анализ таблиц считает по всем строкам файла и читает числа так, как их пишут в русской
/// Windows; диаграмма для ленты разбирается терпимо, а в книге Excel проходит схему OpenXml.
/// </summary>
public sealed class TableAnalysisTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amarin-tables-" + Guid.NewGuid().ToString("N"));

    public TableAnalysisTests() => Directory.CreateDirectory(_dir);

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

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private const string Sales = """
        Регион;Месяц;Сумма;Скидка
        Север;Январь;1 200,50;5%
        Юг;Январь;800;0%
        Север;Февраль;300;10%
        Юг;Февраль;;0%
        Запад;Февраль;n/a;0%
        """;

    private string SalesCsv()
    {
        var path = PathOf("sales.csv");
        File.WriteAllText(path, Sales, new UTF8Encoding(true));
        return path;
    }

    private static TableRequest Request(
        IReadOnlyList<TableFilter>? filters = null,
        IReadOnlyList<string>? groupBy = null,
        IReadOnlyList<TableAggregate>? aggregates = null,
        IReadOnlyList<TableSort>? sort = null,
        bool totals = false,
        int limit = 0) =>
        new(filters ?? [], groupBy ?? [], aggregates ?? [], [], sort ?? [], limit, totals);

    [Theory]
    [InlineData("1 200,50", 1200.5)]
    [InlineData("1 200,5", 1200.5)]
    [InlineData("1,234.5", 1234.5)]
    [InlineData("1.234,5", 1234.5)]
    [InlineData("12%", 12)]
    [InlineData("-7", -7)]
    [InlineData("$300", 300)]
    [InlineData("1,5", 1.5)]
    public void Numbers_are_read_as_people_write_them(string text, double expected)
    {
        Assert.True(TableQuery.TryNumber(text, out var number));
        Assert.Equal(expected, number, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("n/a")]
    [InlineData("Север")]
    [InlineData("2024-01-05")]
    public void Text_is_not_taken_for_a_number(string text) =>
        Assert.False(TableQuery.TryNumber(text, out _));

    [Fact]
    public void Grouping_totals_every_row_and_skips_what_is_not_a_number()
    {
        var table = TableQuery.Load(new DocumentSource(SalesCsv(), null), null);

        var result = TableQuery.Run(table, Request(
            groupBy: ["Регион"],
            aggregates: [new TableAggregate("Сумма", "sum"), new TableAggregate(null, "count")],
            sort: [new TableSort("Сумма", Descending: true)],
            totals: true));

        Assert.Equal(["Регион", "sum(Сумма)", "count"], result.Header);
        Assert.Equal(["Север", "1500.5", "2"], result.Rows[0]);
        Assert.Equal(["Юг", "800", "2"], result.Rows[1]);
        Assert.Equal(["Запад", "", "1"], result.Rows[2]);
        Assert.Equal(["Total", "2300.5", "5"], result.TotalsRow);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void A_numeric_filter_passes_only_numbers_and_the_column_letter_works_too()
    {
        var table = TableQuery.Load(new DocumentSource(SalesCsv(), null), null);

        var result = TableQuery.Run(table, Request(filters: [new TableFilter("C", "gt", "500")]));

        Assert.Equal(2, result.Matched);
        Assert.All(result.Rows, row => Assert.NotEqual("n/a", row[2]));
        var text = TableQuery.Format(result);
        Assert.Contains("2 of 5 rows matched", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_column_names_the_real_ones()
    {
        var table = TableQuery.Load(new DocumentSource(SalesCsv(), null), null);

        var error = Assert.Throws<DocumentException>(() => TableQuery.Run(table, Request(groupBy: ["Город"])));

        Assert.Contains("\"Регион\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Excel_sheet_is_read_with_its_formula_results()
    {
        var path = PathOf("book.xlsx");
        ExcelWriter.Create(path, ExcelWriter.FromMarkdown("""
            | Товар | Цена | Кол-во | Итого |
            |---|---|---|---|
            | A | 10 | 2 | =B2*C2 |
            | B | 5 | 4 | =B3*C3 |
            """), overwrite: false);

        var table = TableQuery.Load(new DocumentSource(path, null), null);
        var result = TableQuery.Run(table, Request(aggregates: [new TableAggregate("Итого", "sum")]));

        Assert.Equal(["40"], result.Rows[0]);
    }

    [Fact]
    public void The_chart_block_round_trips_and_keeps_cyrillic_readable()
    {
        var spec = new ChartSpec(ChartSpec.Bar, "Продажи", ["Север", "Юг"], [new ChartSeries("Сумма", [1500.5, null])]);

        var fence = spec.ToFence();
        Assert.StartsWith("```chart\n", fence, StringComparison.Ordinal);
        Assert.Contains("Север", fence, StringComparison.Ordinal);
        Assert.True(ChartSpec.TryParse(spec.ToJson(), out var back));
        Assert.Equal(spec.Labels, back.Labels);
        Assert.Equal([1500.5, null], back.Series[0].Values);
    }

    [Theory]
    [InlineData("""{"type":"bar","labels":["a","b"],"series":[{"name":"x","values":[1,2,],},],}""", true)]
    [InlineData("""{"type":"pie","labels":["a"],"series":[{"values":[1]},{"values":[2]}]}""", true)]
    [InlineData("""{"type":"radar","labels":["a"],"series":[{"values":[1]}]}""", false)]
    [InlineData("""{"type":"bar","labels":["a"],"series":[{"values":["x"]}]}""", false)]
    [InlineData("""{"type":"bar","labels":["a","b"],"series":[{"name":"x","values":[1""", false)]
    [InlineData("not json", false)]
    public void A_chart_block_parses_leniently_and_a_broken_one_stays_code(string json, bool valid)
    {
        Assert.Equal(valid, ChartSpec.TryParse(json, out var spec));
        if (spec is { Type: ChartSpec.Pie })
        {
            Assert.Single(spec.Series);
        }
    }

    [Fact]
    public void Too_many_points_are_cut_to_the_limit()
    {
        var labels = string.Join(",", Enumerable.Range(0, 500).Select(i => "\"" + i + "\""));
        var values = string.Join(",", Enumerable.Range(0, 500));

        Assert.True(ChartSpec.TryParse($$"""{"labels":[{{labels}}],"series":[{"values":[{{values}}]}]}""", out var spec));

        Assert.Equal(ChartSpec.MaxPoints, spec.Labels.Count);
        Assert.Equal(ChartSpec.MaxPoints, spec.Series[0].Values.Count);
    }

    [Fact]
    public async Task The_tool_returns_a_table_and_a_ready_chart_block()
    {
        var result = await new AnalyzeTableTool().ExecuteAsync(Args(new
        {
            path = SalesCsv(),
            group_by = new[] { "Месяц" },
            aggregate = new[] { new { column = "Сумма", fn = "sum" } },
            chart = new { type = "bar", title = "По месяцам" }
        }));

        Assert.True(result.Success, result.Output);
        Assert.Contains("| Январь | 2000.5 |", result.Output, StringComparison.Ordinal);
        var block = result.Output[(result.Output.IndexOf("```chart", StringComparison.Ordinal) + 9)..].Split("\n```")[0];
        Assert.True(ChartSpec.TryParse(block, out var spec));
        Assert.Equal(["Январь", "Февраль"], spec.Labels);
    }

    [Fact]
    public async Task A_chart_lands_in_the_workbook_and_passes_the_schema()
    {
        var path = PathOf("chart.xlsx");
        ExcelWriter.Create(path, ExcelWriter.FromMarkdown("""
            | Месяц | Север | Юг |
            |---|---|---|
            | Январь | 10 | 20 |
            | Февраль | 15 | 5 |
            """), overwrite: false);
        var state = new FileToolState();
        await new ReadFileTool(state).ExecuteAsync(Args(new { path }));

        var edited = await new EditDocumentTool(state).ExecuteAsync(Args(new
        {
            path,
            operations = new object[]
            {
                new { op = "add_chart", range = "A1:C3", chart = "line", title = "Продажи" },
                new { op = "add_chart", range = "A1:B3", chart = "pie", cell = "A20" }
            }
        }));

        Assert.True(edited.Success, edited.Output);
        using var document = SpreadsheetDocument.Open(path, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2016).Validate(document)
            .Select(error => $"{error.Path?.XPath}: {error.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        var charts = document.WorkbookPart!.WorksheetParts.Single().DrawingsPart!.ChartParts.ToList();
        Assert.Equal(2, charts.Count);
        var line = charts.Select(part => part.ChartSpace!.Descendants<C.LineChart>().SingleOrDefault()).Single(item => item is not null)!;
        Assert.Equal(2, line.Elements<C.LineChartSeries>().Count());
        Assert.Equal("Sheet1!$B$2:$B$3", line.Elements<C.LineChartSeries>().First().Descendants<C.Formula>().Last().Text);
    }

    [Fact]
    public void The_ocr_output_is_split_into_pages_and_a_missing_language_is_explained()
    {
        var (language, pages) = LocalOcr.Parse("LANGUAGE ru\n=== PAGE 2\nпервая\nвторая\n=== PAGE 3\n\n");

        Assert.Equal("ru", language);
        Assert.Equal([new OcrPage(2, "первая\nвторая"), new OcrPage(3, "")], pages);
        var error = Assert.Throws<DocumentException>(() => LocalOcr.Parse("NO_OCR en-US, de-DE"));
        Assert.Contains("en-US, de-DE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_reads_the_text_of_a_picture()
    {
        var path = PathOf("text.png");
        using (var bitmap = new System.Drawing.Bitmap(900, 200))
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        using (var font = new System.Drawing.Font("Arial", 48))
        {
            graphics.Clear(System.Drawing.Color.White);
            graphics.DrawString("HELLO OCR 2024", font, System.Drawing.Brushes.Black, 20, 50);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }

        var result = await new RecognizeTextTool().ExecuteAsync(Args(new { path, language = "en-US" }));

        // На машине без английского OCR Windows отвечает понятным отказом — это тоже правильный ответ.
        if (!result.Success)
        {
            Assert.Contains("text recognition", result.Output, StringComparison.OrdinalIgnoreCase);
            return;
        }

        Assert.Contains("HELLO", result.Output, StringComparison.Ordinal);
        Assert.Contains("2024", result.Output, StringComparison.Ordinal);

        // Тот же текст картинкой в PDF без текстового слоя — скан: страницу рисует Windows.
        var scan = PathOf("scan.pdf");
        PdfWriter.Create(scan, "![scan](" + path + ")", DocumentImages.Resolve);
        var pages = await new RecognizeTextTool().ExecuteAsync(Args(new { path = scan, language = "en-US" }));
        Assert.True(pages.Success, pages.Output);
        Assert.Contains("--- Page 1 ---", pages.Output, StringComparison.Ordinal);
        Assert.Contains("HELLO", pages.Output, StringComparison.Ordinal);
    }
}
