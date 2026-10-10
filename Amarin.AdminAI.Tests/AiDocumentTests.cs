using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Документ, созданный нейросетью, не требует разрешения ни на создание, ни на изменения (1.33.0):
/// новый документ молча создаётся везде, кроме системных, чужих и сетевых мест, а созданный ИИ
/// молча правится. Прочее — как раньше.
/// </summary>
public sealed class AiDocumentGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-aidoc-gate-" + Guid.NewGuid().ToString("N"));

    public AiDocumentGateTests() => Directory.CreateDirectory(_root);

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

    private static GateCheck Check(string tool, object arguments, ApprovalMode mode = ApprovalMode.Normal) =>
        ToolGate.Check(tool, JsonSerializer.SerializeToElement(arguments), new AppSettings { ApprovalMode = mode });

    private static bool CreatesNewOnly(GateCheck check) => check.Arguments.TryGetProperty(SafeZone.CreateNewFlag, out _);

    /// <summary>Место вне «Загрузок» и «Рабочего стола», но и не системное: корень системного диска.</summary>
    private static string Outside(string name) =>
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "AmarinGateTest-" + Guid.NewGuid().ToString("N"), name);

    [Theory]
    [InlineData("create_document", "report.docx")]
    [InlineData("create_document", "table.xlsx")]
    [InlineData("create_document", "table.pdf")]
    [InlineData("write_file", "notes.txt")]
    [InlineData("write_file", "notes.md")]
    [InlineData("create_folder", "Reports")]
    public void A_new_document_outside_system_folders_is_created_without_a_question(string tool, string name)
    {
        var check = Check(tool, new { path = Outside(name), content = "x" });

        Assert.Null(check.Refusal);
        Assert.Null(check.Question);

        // Молча — только как новый файл: два вызова одного раунда не перепишут друг друга.
        Assert.True(CreatesNewOnly(check));
    }

    [Theory]
    [InlineData("run.ps1")]
    [InlineData("start.cmd")]
    [InlineData("app.exe.config")]
    public void A_new_script_or_config_outside_downloads_still_asks(string name)
    {
        Assert.NotNull(Check("write_file", new { path = Outside(name), content = "x" }).Question);
    }

    public static TheoryData<string> SystemPlaces() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "report.docx"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Vendor", "report.docx"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "report.docx"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vendor", "report.docx"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "report.html"),
        Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!, "SomeoneElse", "Documents", "report.docx"),
        @"\\server\share\report.docx"
    ];

    [Theory]
    [MemberData(nameof(SystemPlaces))]
    public void A_document_in_system_program_application_foreign_or_network_places_still_asks(string path)
    {
        Assert.False(DocumentZone.Allows(path));
        Assert.NotNull(Check("create_document", new { path, content = "x" }).Question);
    }

    [Fact]
    public void A_stream_of_an_existing_file_is_not_a_new_document()
    {
        Assert.False(DocumentZone.Allows(Outside("report.docx") + ":hidden.txt"));
    }

    [Fact]
    public void The_documents_the_ai_made_are_changed_without_a_question_and_others_are_asked_about()
    {
        var own = Path.Combine(_root, "own.docx");
        var theirs = Path.Combine(_root, "theirs.docx");
        File.WriteAllText(own, "x");
        File.WriteAllText(theirs, "x");
        var previous = ToolGate.AiDocument;
        ToolGate.AiDocument = path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(own), StringComparison.OrdinalIgnoreCase);
        try
        {
            static object Append(string path) => new { path, operations = new[] { new { op = "append", content = "more" } } };

            var edit = Check("edit_document", Append(own));
            Assert.Null(edit.Question);
            Assert.False(CreatesNewOnly(edit));
            Assert.Null(Check("edit_file", new { path = own, old_string = "x", new_string = "y" }).Question);
            Assert.Null(Check("create_document", new { path = own, content = "new", overwrite = true }).Question);

            Assert.NotNull(Check("edit_document", Append(theirs)).Question);
            Assert.NotNull(Check("edit_file", new { path = theirs, old_string = "x", new_string = "y" }).Question);

            // «Спрашивать всё» — осознанный выбор человека: он спрашивает и про документы ИИ.
            Assert.NotNull(Check("edit_document", Append(own), ApprovalMode.AskAll).Question);
            Assert.NotNull(Check("create_document", new { path = Outside("x.docx"), content = "x" }, ApprovalMode.AskAll).Question);

            // «Только чтение» не пускает запись вовсе, чья бы она ни была.
            Assert.NotNull(Check("edit_document", Append(own), ApprovalMode.ReadOnly).Refusal);
        }
        finally
        {
            ToolGate.AiDocument = previous;
        }
    }

    [Fact]
    public void A_script_the_ai_wrote_is_still_asked_about_when_overwritten()
    {
        var script = Path.Combine(_root, "run.ps1");
        File.WriteAllText(script, "x");
        var previous = ToolGate.AiDocument;
        ToolGate.AiDocument = path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(script), StringComparison.OrdinalIgnoreCase);
        try
        {
            Assert.NotNull(Check("write_file", new { path = script, content = "y" }).Question);
        }
        finally
        {
            ToolGate.AiDocument = previous;
        }
    }
}

/// <summary>Книга документов ИИ: что создал ИИ — его, что положил на то же место человек — нет.</summary>
public sealed class AiDocumentBookTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-aidoc-book-" + Guid.NewGuid().ToString("N"));

    public AiDocumentBookTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void A_noted_document_is_owned_across_restarts_and_only_in_its_profile()
    {
        var file = Path.Combine(_root, "report.docx");
        File.WriteAllText(file, "x");
        var profile = Path.Combine(_root, "profile");
        Directory.CreateDirectory(profile);

        new AiDocumentBook(profile).Note(file);
        var reopened = new AiDocumentBook(profile);

        Assert.True(reopened.Owns(file));
        reopened.UseRoot(Path.Combine(_root, "other"));
        Assert.False(reopened.Owns(file));
    }

    [Fact]
    public void A_file_the_user_put_in_its_place_is_not_the_ais()
    {
        var file = Path.Combine(_root, "report.docx");
        File.WriteAllText(file, "x");
        var book = new AiDocumentBook(_root);
        book.Note(file);

        File.Delete(file);
        File.WriteAllText(file, "the user's own");
        File.SetCreationTimeUtc(file, DateTime.UtcNow.AddDays(-3));

        Assert.False(book.Owns(file));
    }

    [Fact]
    public async Task What_the_ai_created_stays_its_after_edits_and_what_it_edited_for_the_user_does_not_become_its()
    {
        var state = new FileToolState { Documents = new AiDocumentBook(_root) };
        var own = Path.Combine(_root, "own.docx");
        var theirs = Path.Combine(_root, "theirs.docx");
        WordWriter.Create(theirs, "# Theirs", _ => null);

        Assert.True((await new CreateDocumentTool(state).ExecuteAsync(Json(new { path = own, content = "# Report" }))).Success);
        Assert.True((await new EditDocumentTool(state).ExecuteAsync(Json(new { path = own, operations = new[] { new { op = "append", content = "More" } } }))).Success);
        Assert.True((await new EditDocumentTool(state).ExecuteAsync(Json(new { path = theirs, operations = new[] { new { op = "append", content = "More" } } }))).Success);
        Assert.True((await new WriteFileTool(state).ExecuteAsync(Json(new { path = Path.Combine(_root, "run.ps1"), content = "Get-Date" }))).Success);

        Assert.True(state.Documents.Owns(own));
        Assert.False(state.Documents.Owns(theirs));

        // Скрипт, написанный ИИ, документом не становится: его перезапись спросят всегда.
        Assert.False(state.Documents.Owns(Path.Combine(_root, "run.ps1")));
    }

}

/// <summary>Большие документы: PDF дописывается страницами, широкая таблица печатается блоками столбцов.</summary>
public sealed class LargeDocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-large-doc-" + Guid.NewGuid().ToString("N"));

    public LargeDocumentTests() => Directory.CreateDirectory(_root);

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

    private static string WideTable(int columns, int rows)
    {
        var header = "| n | " + string.Join(" | ", Enumerable.Range(1, columns).Select(c => "c" + c)) + " |";
        var rule = "|" + string.Concat(Enumerable.Repeat("---|", columns + 1));
        var body = Enumerable.Range(1, rows).Select(r => "| " + r + " | " + string.Join(" | ", Enumerable.Range(1, columns).Select(c => (r * c).ToString(System.Globalization.CultureInfo.InvariantCulture))) + " |");
        return string.Join("\n", [header, rule, .. body]);
    }

    [Fact]
    public async Task A_pdf_is_built_in_parts_by_appending_pages()
    {
        var path = Path.Combine(_root, "parts.pdf");
        Assert.True((await new CreateDocumentTool().ExecuteAsync(JsonSerializer.SerializeToElement(new { path, content = "# Part one" }))).Success);

        var result = await new EditDocumentTool().ExecuteAsync(JsonSerializer.SerializeToElement(new
        {
            path,
            operations = new[] { new { op = "append", content = "# Part two" }, new { op = "append", content = "# Part three" } }
        }));

        Assert.True(result.Success, result.Output);
        Assert.Equal(3, PdfEditor.PageCount(File.ReadAllBytes(path)));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        Assert.Contains("three", string.Concat(pdf.GetPages().Select(page => page.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_wider_than_the_page_is_split_into_blocks_that_repeat_the_row_labels()
    {
        var table = (DocTable)MarkdownBlocks.Parse(WideTable(40, 2)).Single();
        double[] widths = [50, .. Enumerable.Repeat(PdfLayout.MinColumn, 40)];

        var blocks = MarkdownBlocks.ColumnBlocks(table, widths, 483);

        Assert.True(blocks.Count > 1);
        Assert.All(blocks, block => Assert.Equal("n", MarkdownBlocks.PlainText(block.Rows[0][0])));
        Assert.Equal(40, blocks.Sum(block => block.Rows[0].Count - 1));
        Assert.Single(MarkdownBlocks.ColumnBlocks(table, [.. Enumerable.Repeat(10.0, 41)], 483));
    }

    [Fact]
    public void A_wide_pdf_table_keeps_its_last_column_on_the_page()
    {
        var path = Path.Combine(_root, "wide.pdf");
        PdfWriter.Create(path, WideTable(40, 3), _ => null);

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        var text = string.Concat(pdf.GetPages().Select(page => page.Text));

        // До 1.33.0 всё правее четырнадцатого столбца уходило за край страницы.
        Assert.Contains("c40", text, StringComparison.Ordinal);
        Assert.Contains("120", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wide_word_table_becomes_several_tables_with_a_paragraph_between()
    {
        var path = Path.Combine(_root, "wide.docx");
        WordWriter.Create(path, WideTable(40, 3), _ => null);

        using var document = WordprocessingDocument.Open(path, false);
        var body = document.MainDocumentPart!.Document!.Body!;
        var tables = body.Elements<W.Table>().ToList();

        Assert.True(tables.Count > 1);
        Assert.Contains("c40", body.InnerText, StringComparison.Ordinal);

        // Две таблицы подряд Word склеил бы в одну.
        Assert.All(tables.Skip(1), table => Assert.IsType<W.Paragraph>(table.PreviousSibling()));
    }
}
