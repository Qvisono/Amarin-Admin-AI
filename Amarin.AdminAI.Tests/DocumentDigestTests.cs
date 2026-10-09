using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Документ, приложенный к сообщению, читается на этом ПК: модель получает его начало блоком
/// <c>&lt;document&gt;</c> и дочитывает остальное по пути или ручке — без копий во <c>%TEMP%</c>.
/// </summary>
public sealed class DocumentDigestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amarin-digest-" + Guid.NewGuid().ToString("N"));

    public DocumentDigestTests() => Directory.CreateDirectory(_dir);

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

    private FileAttachment WordAttachment(int paragraphs, string? sourcePath = null)
    {
        var path = Path.Combine(_dir, "отчёт-" + Guid.NewGuid().ToString("N")[..6] + ".docx");
        var markdown = "# Отчёт\n\n" + string.Join("\n\n", Enumerable.Range(1, paragraphs).Select(i => $"Абзац номер {i}: " + new string('т', 80)));
        WordWriter.Create(path, markdown, _ => null);
        var bytes = File.ReadAllBytes(path);
        return new FileAttachment(Convert.ToBase64String(bytes), AttachmentTypes.GuessMimeType(path), Path.GetFileName(path), bytes.Length, sourcePath);
    }

    [Fact]
    public void A_Word_attachment_arrives_as_text_with_numbered_paragraphs()
    {
        var file = WordAttachment(paragraphs: 3);

        var block = DocumentDigest.TextOf(file);

        Assert.NotNull(block);
        Assert.StartsWith(DocumentDigest.OpenTag, block, StringComparison.Ordinal);
        Assert.Contains("[1] # Отчёт", block, StringComparison.Ordinal);
        Assert.Contains("Абзац номер 3", block, StringComparison.Ordinal);
        Assert.EndsWith(DocumentDigest.CloseTag, block, StringComparison.Ordinal);
    }

    [Fact]
    public void A_read_attachment_goes_as_text_not_as_a_file_part()
    {
        var user = new ChatDisplayMessage { Role = "user", Id = "m", Text = "что в отчёте?", Files = [WordAttachment(paragraphs: 2)] };

        var content = ChatContent.ForUser(user, [user], 0);

        Assert.Equal(JsonValueKind.String, content.ValueKind);
        Assert.Contains("<document ", content.GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_attachment_without_a_file_is_read_further_by_its_handle()
    {
        var file = WordAttachment(paragraphs: 400);
        var block = DocumentDigest.TextOf(file) ?? "";
        var handle = DocumentDigest.PathOf(file);

        Assert.StartsWith(FileToolPaths.AttachmentScheme, handle, StringComparison.Ordinal);
        Assert.Contains("path=\"" + handle + "\"", block, StringComparison.Ordinal);
        Assert.Contains("More: read_file", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Абзац номер 400", block, StringComparison.Ordinal);

        var tail = await new ReadFileTool().ExecuteAsync(JsonSerializer.SerializeToElement(new { path = handle, offset = 395 }));

        Assert.True(tail.Success, tail.Output);
        Assert.Contains("Абзац номер 400", tail.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_attachment_handle_cannot_be_edited_in_place()
    {
        var handle = DocumentDigest.PathOf(WordAttachment(paragraphs: 1));

        var result = await new EditFileTool().ExecuteAsync(JsonSerializer.SerializeToElement(new { path = handle, old_string = "Отчёт", new_string = "Итог" }));

        Assert.False(result.Success);
        Assert.Contains("save_as", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edited_copy_of_an_attachment_is_saved_where_asked()
    {
        var handle = DocumentDigest.PathOf(WordAttachment(paragraphs: 1));
        var copy = Path.Combine(_dir, "копия.docx");

        var result = await new EditDocumentTool().ExecuteAsync(JsonSerializer.SerializeToElement(new
        {
            path = handle,
            save_as = copy,
            operations = new object[] { new { op = "append", content = "Дописано." } }
        }));

        Assert.True(result.Success, result.Output);
        Assert.Contains("Дописано.", DocumentReader.Format(copy, DocumentReader.Read(copy, new DocumentWindow()), new DocumentWindow()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_paragraph_is_read_in_parts_to_its_end()
    {
        var path = Path.Combine(_dir, "long.txt");
        File.WriteAllText(path, new string('a', 25_000) + "КОНЕЦ");
        var window = new DocumentWindow();

        var first = DocumentReader.Format(path, DocumentReader.Read(path, window), window);
        Assert.Contains("part=2", first, StringComparison.Ordinal);

        var third = new DocumentWindow(Offset: 1, Limit: 1, Part: 3);
        Assert.Contains("КОНЕЦ", DocumentReader.Format(path, DocumentReader.Read(path, third), third), StringComparison.Ordinal);
    }
}
