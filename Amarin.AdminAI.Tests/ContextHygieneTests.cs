using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Отправляемая история без устаревших версий файлов: прочитанное до правки — заглушкой, длинные
/// тексты старых записей — началом, документы старых сообщений — ссылкой, прежние версии блока
/// кода — пометкой. Хранимая история при этом не меняется ни на байт.
/// </summary>
public sealed class ContextHygieneTests
{
    private static readonly string Doc = Path.Combine(DownloadPaths.DownloadsDirectory, "план.txt");

    private static ChatMessage User(string text) => new() { Role = "user", Content = ChatContent.Text(text) };

    private static ChatMessage Assistant(string text) => new() { Role = "assistant", Content = ChatContent.Text(text) };

    private static ChatMessage Calls(params (string Id, string Name, object Arguments)[] calls) => new()
    {
        Role = "assistant",
        ToolCalls = [.. calls.Select(call => new ToolCall
        {
            Id = call.Id,
            Function = new FunctionCall { Name = call.Name, Arguments = JsonSerializer.Serialize(call.Arguments) }
        })]
    };

    private static ChatMessage Result(string id, string name, string text) => new()
    {
        Role = "tool",
        ToolCallId = id,
        Name = name,
        Content = ChatContent.Text(text)
    };

    /// <summary>Три старых хода с работой над файлом и два свежих — их защищает правило.</summary>
    private static List<ChatMessage> History(string oldWrite)
    {
        var longRead = string.Concat(Enumerable.Repeat("строка файла\n", 100));
        return
        [
            new ChatMessage { Role = "system", Content = ChatContent.Text("system") },
            User("прочитай план"),
            Calls(("r1", "read_file", new { path = Doc })),
            Result("r1", "read_file", longRead),
            Assistant("прочитал"),
            User("поправь пункт"),
            Calls(("e1", "edit_file", new { path = "план.txt", old_string = "a", new_string = "b" })),
            Result("e1", "edit_file", "Edited"),
            Calls(("w1", "write_file", new { path = Doc, content = oldWrite })),
            Result("w1", "write_file", "ok"),
            Assistant("готово"),
            User("ещё раз прочитай"),
            Calls(("r2", "read_file", new { path = Doc })),
            Result("r2", "read_file", longRead),
            Assistant("свежее"),
            User("последний вопрос"),
            Assistant("ответ")
        ];
    }

    [Fact]
    public void A_read_followed_by_a_change_of_the_same_file_becomes_a_stub()
    {
        var outgoing = History("x");

        ContextHygiene.Apply(outgoing);

        // «план.txt» без папки — тот же файл, что полный путь в «Загрузках».
        Assert.Contains("out of date", ChatContent.ReadText(outgoing[3].Content), StringComparison.Ordinal);
        Assert.Equal("r1", outgoing[3].ToolCallId);
    }

    [Fact]
    public void The_last_two_turns_of_the_person_are_never_touched()
    {
        var outgoing = History("x");
        var before = outgoing.Select(message => JsonSerializer.Serialize(message)).ToList();

        ContextHygiene.Apply(outgoing);

        // Второе чтение — во втором с конца ходе человека: оно остаётся как было.
        for (var i = 11; i < outgoing.Count; i++)
        {
            Assert.Equal(before[i], JsonSerializer.Serialize(outgoing[i]));
        }
    }

    [Fact]
    public void Long_arguments_of_old_calls_are_cut_and_stay_valid_JSON()
    {
        var big = new string('ж', 5000);
        var outgoing = History(big);

        ContextHygiene.Apply(outgoing);

        var arguments = outgoing[8].ToolCalls?.Single().Function.Arguments ?? "";
        var parsed = ToolArguments.Parse(arguments);
        var content = parsed.GetProperty("content").GetString() ?? "";
        Assert.True(content.Length < 600, content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("omitted", content, StringComparison.Ordinal);
        Assert.Equal("w1", outgoing[8].ToolCalls?.Single().Id);
    }

    [Fact]
    public void The_stored_history_is_not_changed()
    {
        var stored = History(new string('ж', 5000));
        var snapshot = JsonSerializer.Serialize(stored);
        var outgoing = new List<ChatMessage>(stored);

        ContextHygiene.Apply(outgoing);

        Assert.Equal(snapshot, JsonSerializer.Serialize(stored));
        Assert.NotEqual(snapshot, JsonSerializer.Serialize(outgoing));
    }

    [Fact]
    public void The_same_history_gives_the_same_request()
    {
        var first = History(new string('ж', 5000));
        var second = History(new string('ж', 5000));

        ContextHygiene.Apply(first);
        ContextHygiene.Apply(second);

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    [Fact]
    public void A_read_of_another_page_does_not_supersede_the_first()
    {
        var page = string.Concat(Enumerable.Repeat("текст\n", 100));
        List<ChatMessage> outgoing =
        [
            User("прочитай начало"),
            Calls(("r1", "read_file", new { path = Doc })),
            Result("r1", "read_file", page),
            Assistant("ок"),
            User("дальше"),
            Calls(("r2", "read_file", new { path = Doc, offset = 101 })),
            Result("r2", "read_file", page),
            Assistant("ок"),
            User("ещё"),
            Assistant("ок"),
            User("последний"),
            Assistant("ок")
        ];

        ContextHygiene.Apply(outgoing);

        Assert.Equal(page, ChatContent.ReadText(outgoing[2].Content));
    }

    [Fact]
    public void A_document_block_of_an_old_message_collapses_to_its_path()
    {
        var block = "<document name=\"отчёт.docx\" path=\"C:\\docs\\отчёт.docx\">\n[1] # Отчёт\n[2] много текста\n</document>";
        List<ChatMessage> outgoing =
        [
            User("посмотри\n\n" + block),
            Assistant("посмотрел"),
            User("а теперь"),
            Assistant("ок"),
            User("и ещё " + block),
            Assistant("ок")
        ];

        ContextHygiene.Apply(outgoing);

        var old = ChatContent.ReadText(outgoing[0].Content) ?? "";
        Assert.DoesNotContain("много текста", old, StringComparison.Ordinal);
        Assert.Contains("path=\"C:\\docs\\отчёт.docx\"", old, StringComparison.Ordinal);
        Assert.Contains("много текста", ChatContent.ReadText(outgoing[4].Content), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_versions_of_a_code_block_older_than_the_latest_two_collapse()
    {
        static string Prompt(int version) =>
            "```text\n" + string.Join("\n", Enumerable.Range(1, 30).Select(i => $"tag number {i}")) + $"\nversion {version}\n```";

        List<ChatMessage> outgoing =
        [
            User("сделай промпт"), Assistant(Prompt(1)),
            User("поправь"), Assistant(Prompt(2)),
            User("ещё"), Assistant(Prompt(3)),
            User("и ещё"), Assistant(Prompt(4))
        ];

        ContextHygiene.Apply(outgoing);

        Assert.Contains("earlier version", ChatContent.ReadText(outgoing[1].Content), StringComparison.Ordinal);
        Assert.Contains("earlier version", ChatContent.ReadText(outgoing[3].Content), StringComparison.Ordinal);
        Assert.Contains("version 3", ChatContent.ReadText(outgoing[5].Content), StringComparison.Ordinal);
        Assert.Contains("version 4", ChatContent.ReadText(outgoing[7].Content), StringComparison.Ordinal);
    }
}
