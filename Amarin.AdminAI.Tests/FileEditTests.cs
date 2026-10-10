using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Правка файлов «как у Claude Code»: только прочитанного в этом чате и не изменившегося с тех
/// пор, точной заменой, без потери соседних правок и без порчи концов строк и кодировки.
/// </summary>
public sealed class FileEditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amarin-edit-" + Guid.NewGuid().ToString("N"));

    public FileEditTests() => Directory.CreateDirectory(_dir);

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

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static IDisposable Chat(string id) => AgentRunScope.Push(new AgentRunContext
    {
        Call = new ToolCallRecord { Id = "call", Name = "edit_file" },
        Assistant = new ChatDisplayMessage { Role = "assistant" },
        Observer = SilentTurnObserver.Instance,
        SessionId = id
    });

    private string File(string name, string text, Encoding? encoding = null)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public async Task A_file_not_read_in_this_chat_is_not_edited()
    {
        var state = new FileToolState();
        var path = File("a.txt", "alpha\nbeta\n");

        using (Chat("chat-1"))
        {
            var result = await new EditFileTool(state).ExecuteAsync(Args(new { path, old_string = "beta", new_string = "gamma" }));

            Assert.False(result.Success);
            Assert.Contains("read_file", result.Output, StringComparison.Ordinal);
        }

        Assert.Equal("alpha\nbeta\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task After_reading_the_file_two_edits_in_a_row_go_through()
    {
        var state = new FileToolState();
        var path = File("a.txt", "alpha\nbeta\ngamma\n");

        using (Chat("chat-1"))
        {
            Assert.True((await new ReadFileTool(state).ExecuteAsync(Args(new { path }))).Success);
            Assert.True((await new EditFileTool(state).ExecuteAsync(Args(new { path, old_string = "beta", new_string = "BETA" }))).Success);
            Assert.True((await new EditFileTool(state).ExecuteAsync(Args(new { path, old_string = "gamma", new_string = "GAMMA" }))).Success);
        }

        Assert.Equal("alpha\nBETA\nGAMMA\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task A_read_in_another_chat_does_not_count()
    {
        var state = new FileToolState();
        var path = File("a.txt", "alpha\n");
        using (Chat("chat-1"))
        {
            await new ReadFileTool(state).ExecuteAsync(Args(new { path }));
        }

        using (Chat("chat-2"))
        {
            var result = await new EditFileTool(state).ExecuteAsync(Args(new { path, old_string = "alpha", new_string = "omega" }));
            Assert.False(result.Success);
        }
    }

    [Fact]
    public async Task A_file_changed_on_disk_after_the_read_must_be_read_again()
    {
        var state = new FileToolState();
        var path = File("a.txt", "alpha\nbeta\n");

        using (Chat("chat-1"))
        {
            await new ReadFileTool(state).ExecuteAsync(Args(new { path }));
            System.IO.File.WriteAllText(path, "alpha\nbeta\nadded by the user\n");

            var result = await new EditFileTool(state).ExecuteAsync(Args(new { path, old_string = "beta", new_string = "BETA" }));

            Assert.False(result.Success);
            Assert.Contains("changed on disk", result.Output, StringComparison.Ordinal);
        }

        Assert.Contains("added by the user", System.IO.File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_chat_there_is_nothing_to_check()
    {
        // Рецепт и прогон инструментов идут вне чата: «прочитанного раньше» там нет.
        var path = File("a.txt", "alpha\n");

        var result = await new EditFileTool(new FileToolState()).ExecuteAsync(Args(new { path, old_string = "alpha", new_string = "omega" }));

        Assert.True(result.Success);
        Assert.Equal("omega\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task Parallel_edits_of_one_file_both_land()
    {
        // Вызовы одного раунда идут параллельно; без замка вторая запись затирала первую.
        var state = new FileToolState();
        var lines = Enumerable.Range(1, 40).Select(i => "line " + i).ToList();
        var path = File("many.txt", string.Join("\n", lines) + "\n");

        using (Chat("chat-1"))
        {
            await new ReadFileTool(state).ExecuteAsync(Args(new { path }));
            var edits = Enumerable.Range(1, 40)
                .Select(i => Task.Run(() => new EditFileTool(state).ExecuteAsync(Args(new { path, old_string = $"line {i}\n", new_string = $"LINE {i}\n" }))))
                .ToList();
            var results = await Task.WhenAll(edits);
            Assert.All(results, result => Assert.True(result.Success, result.Output));
        }

        var text = System.IO.File.ReadAllText(path);
        Assert.DoesNotContain("line ", text, StringComparison.Ordinal);
        Assert.Equal(40, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Line_endings_of_the_file_stay_as_they_were()
    {
        var path = File("mixed.txt", "one\r\ntwo\r\nthree\nfour\n");

        TextEdits.Replace(path, "two\nthree", "2\n3", all: false);

        // Найденный кусок был с \r\n между строками — замена его же вида; чужие концы не тронуты.
        Assert.Equal("one\r\n2\r\n3\nfour\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public void A_fragment_copied_with_line_numbers_is_found()
    {
        var path = File("code.cs", "int a = 1;\nint b = 2;\n");

        TextEdits.Replace(path, "     2\tint b = 2;", "     2\tint b = 3;", all: false);

        Assert.Equal("int a = 1;\nint b = 3;\n", System.IO.File.ReadAllText(path));
    }

    [Theory]
    [InlineData("ru-RU", "привет мир\n", "мир")]
    [InlineData("en-US", "café olé\n", "olé")]
    public void A_character_the_files_code_page_lacks_is_refused_not_turned_into_a_question_mark(string culture, string text, string word)
    {
        // Файл без метки и не в UTF-8 читается в кодовой странице ANSI по культуре, как его читает
        // любая программа Windows: у русской это 1251, у английской 1252. Культура задана здесь
        // явно: прежде тест писал русский текст и полагался на машину, и на раннере GitHub
        // (английская Windows) «мир» не находился — тест падал только в CI.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
        try
        {
            var ansi = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            var path = File("old.txt", text, ansi);

            var error = Assert.Throws<DocumentException>(() => TextEdits.Replace(path, word, word + " ✓", all: false));

            Assert.Contains("encoding", error.Message, StringComparison.Ordinal);
            Assert.Equal(text, ansi.GetString(System.IO.File.ReadAllBytes(path)));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void An_ambiguous_fragment_is_refused_until_it_is_unique()
    {
        var path = File("dup.txt", "x = 1\nx = 1\n");

        var error = Assert.Throws<DocumentException>(() => TextEdits.Replace(path, "x = 1", "x = 2", all: false));

        Assert.Contains("2 times", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, TextEdits.Replace(path, "x = 1", "x = 2", all: true).Count);
    }

    [Fact]
    public async Task Writing_over_a_file_the_chat_never_read_is_refused()
    {
        var state = new FileToolState();
        var path = File("notes.md", "user notes\n");

        using (Chat("chat-1"))
        {
            var result = await new WriteFileTool(state).ExecuteAsync(Args(new { path, content = "model text" }));
            Assert.False(result.Success);
        }

        Assert.Equal("user notes\n", System.IO.File.ReadAllText(path));
    }
}
