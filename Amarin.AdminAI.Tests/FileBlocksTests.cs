using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Блок <c>```file</c>: модель называет файл, программа подставляет его текст человеку, а в истории
/// модели остаётся короткая запись.
/// </summary>
public sealed class FileBlocksTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("amarin-file-blocks-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Save(string name, string text)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static string Block(string path, string? lines = null) =>
        "```file\npath: " + path + "\n" + (lines is null ? "" : "lines: " + lines + "\n") + "```";

    private static string Expand(string text, bool known = true) => FileBlocks.Expand(text, _ => known);

    [Fact]
    public void The_block_becomes_the_text_of_the_file_with_its_language()
    {
        var path = Save("a.py", "print(1)\nprint(2)\n");

        var text = Expand("вот файл:\n\n" + Block(path) + "\n\nготово");

        Assert.Equal("вот файл:\n\n```python\nprint(1)\nprint(2)\n```\n\nготово", text);
    }

    [Fact]
    public void A_part_of_the_file_is_shown_by_lines()
    {
        var path = Save("a.txt", "один\nдва\nтри\nчетыре\n");

        Assert.Equal("```txt\nдва\nтри\n```", Expand(Block(path, "2-3")));
        Assert.Equal("```txt\nтри\n```", Expand(Block(path, "3")));
        Assert.Equal("```txt\nчетыре\n```", Expand(Block(path, "4-99")));
    }

    [Fact]
    public void Several_blocks_are_all_filled()
    {
        var first = Save("a.txt", "первый");
        var second = Save("b.txt", "второй");

        var text = Expand(Block(first) + "\n\nи\n\n" + Block(second));

        Assert.Contains("первый", text, StringComparison.Ordinal);
        Assert.Contains("второй", text, StringComparison.Ordinal);
        Assert.DoesNotContain("```file", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_without_blocks_stays_as_it_was()
    {
        const string text = "ответ\r\n\r\n```python\nprint(1)\n```\n\nконец\n";

        Assert.Equal(text, Expand(text));
    }

    /// <summary>Запись внутри чужого блока кода — это пример, а не просьба.</summary>
    [Fact]
    public void A_block_inside_another_code_block_is_left_alone()
    {
        var path = Save("a.txt", "содержимое");
        var text = "````markdown\n" + Block(path) + "\n````";

        Assert.Equal(text, Expand(text));
    }

    /// <summary>Незакрытый блок — оборванный ответ: путь в нём мог оборваться тоже.</summary>
    [Fact]
    public void An_unclosed_block_is_left_alone()
    {
        var path = Save("a.txt", "содержимое");
        var text = "```file\npath: " + path;

        Assert.Equal(text, Expand(text));
    }

    [Fact]
    public void A_block_without_a_path_stays_code()
    {
        const string text = "```file\nкакой-то текст\n```";

        Assert.Equal(text, Expand(text));
    }

    [Fact]
    public void A_missing_file_is_named()
    {
        var path = Path.Combine(_folder, "нет.txt");

        Assert.Equal(Loc.Format("S.FileBlock.Missing", "`" + path + "`"), Expand(Block(path)));
    }

    /// <summary>Текст чужой страницы мог бы попросить модель вывести в чат любой файл с диска.</summary>
    [Fact]
    public void A_file_the_model_has_not_opened_is_not_shown()
    {
        var path = Save("a.txt", "тайна");

        var text = Expand(Block(path), known: false);

        Assert.DoesNotContain("тайна", text, StringComparison.Ordinal);
        Assert.Equal(Loc.Format("S.FileBlock.Unknown", "`" + path + "`"), text);
    }

    [Fact]
    public void A_secret_is_not_shown_even_if_the_chat_knows_it()
    {
        var path = Save("logins.json", "тайна");

        var text = Expand(Block(path));

        Assert.DoesNotContain("тайна", text, StringComparison.Ordinal);
        Assert.Equal(Loc.Format("S.FileBlock.Forbidden", "`" + path + "`"), text);
    }

    [Theory]
    [InlineData("a.docx")]
    [InlineData("a.pdf")]
    [InlineData("a.png")]
    [InlineData("a.zip")]
    public void Only_text_goes_into_a_code_block(string name)
    {
        var path = Save(name, "содержимое");

        Assert.Equal(Loc.Format("S.FileBlock.NotText", "`" + path + "`"), Expand(Block(path)));
    }

    [Fact]
    public void Lines_past_the_end_of_the_file_are_a_failure_and_not_an_empty_block()
    {
        var path = Save("a.txt", "один\n");

        Assert.Equal(Loc.Format("S.FileBlock.Failed", "`" + path + "`"), Expand(Block(path, "5-9")));
    }

    /// <summary>Подсветка идёт на потоке окна: мегабайтный файл повесил бы программу.</summary>
    [Fact]
    public void A_long_file_is_cut_and_says_so()
    {
        var path = Save("a.txt", string.Concat(Enumerable.Repeat("строка файла\n", FileBlocks.MaxChars / 10)));

        var text = Expand(Block(path));

        Assert.True(text.Length < FileBlocks.MaxChars + 200);
        Assert.EndsWith(Loc.Format("S.FileBlock.Cut", FileBlocks.MaxChars), text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_with_a_fence_inside_gets_a_longer_one()
    {
        var path = Save("a.md", "# Заголовок\n\n```bash\nls\n```\n");

        var text = Expand(Block(path));

        Assert.StartsWith("````markdown\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\n````", text, StringComparison.Ordinal);
    }

    /// <summary>Культура задана явно: кодовую страницу ANSI она и решает, а раннер CI — английский.</summary>
    [Fact]
    public void A_file_in_the_russian_code_page_is_read_as_russian()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            var path = Path.Combine(_folder, "a.txt");
            File.WriteAllBytes(path, Encoding.GetEncoding(1251).GetBytes("привет"));

            Assert.Contains("привет", Expand(Block(path)), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static ChatMessage Calling(string name, string arguments) => new()
    {
        Role = "assistant",
        ToolCalls = [new ToolCall { Id = "c1", Function = new FunctionCall { Name = name, Arguments = arguments } }]
    };

    /// <summary>Штампы живут в памяти, а чат продолжают и после перезапуска.</summary>
    [Fact]
    public void The_history_remembers_what_the_model_opened()
    {
        var path = Save("a.txt", "текст");
        var named = JsonSerializer.Serialize(new { path });

        Assert.True(FileBlocks.InHistory([Calling("read_file", named)], path));
        Assert.True(FileBlocks.InHistory([Calling("write_file", named)], path));
        Assert.False(FileBlocks.InHistory([Calling("list_processes", named)], path));
        Assert.False(FileBlocks.InHistory([Calling("read_file", "ERROR: не аргументы")], path));
        Assert.False(FileBlocks.InHistory([Calling("read_file", JsonSerializer.Serialize(new { path = path + "x" }))], path));
    }

    private sealed class Answering(string answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var delta = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = answer } } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: " + delta + "\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\ndata: [DONE]\n",
                    Encoding.UTF8,
                    "text/event-stream")
            });
        }
    }

    private static async Task<ChatSession> AnswerAsync(string answer, FileToolState? files)
    {
        var options = new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "grok-4-6", MaxToolRounds = 2 };
        using var http = new HttpClient(new Answering(answer)) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var settings = AppSettings.CreateDefault();
        var engine = new ChatEngine(new VeniceClient(http, options), options, () => settings, new ToolRegistry([])) { Files = files };
        var session = new ChatSession { Id = "chat", SelectedModelId = "grok-4-6" };
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "покажи" });
        session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text("покажи") });

        await engine.GenerateAssistantAsync(session, SilentTurnObserver.Instance, CancellationToken.None);
        return session;
    }

    /// <summary>
    /// Ради этого всё и сделано: человек видит файл, а история модели остаётся короткой, и в
    /// следующих запросах текст файла не едет.
    /// </summary>
    [Fact]
    public async Task The_person_gets_the_file_and_the_model_keeps_the_short_record()
    {
        var path = Save("a.txt", "содержимое файла");
        var files = new FileToolState();
        files.NoteRead("chat", path);
        var answer = "вот он\n\n" + Block(path);

        var session = await AnswerAsync(answer, files);

        Assert.Contains("содержимое файла", session.Messages[^1].Text, StringComparison.Ordinal);
        Assert.Equal(answer, ChatContent.ReadText(session.ApiMessages[^1].Content));
    }

    [Fact]
    public async Task A_file_this_chat_never_opened_stays_closed_in_the_real_turn()
    {
        var path = Save("a.txt", "содержимое файла");

        var session = await AnswerAsync(Block(path), new FileToolState());

        Assert.DoesNotContain("содержимое файла", session.Messages[^1].Text, StringComparison.Ordinal);
    }
}
