using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Инструкции пользователя: файлы в папке профиля, оглавление в системном промпте чата и
/// инструмент, которым модель открывает текст.
/// </summary>
/// <remarks>
/// Файлы инструкций человек правит и руками, и пересылает, поэтому разбор обязан пережить всё,
/// что делают с Markdown текстовые редакторы, — и ни в одном случае не показывать окно аварии.
/// </remarks>
public sealed class InstructionLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amarin-instructions-" + Guid.NewGuid().ToString("N"));

    private long _ticks = 1;

    public InstructionLibraryTests() => Directory.CreateDirectory(_root);

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

    private string Folder => Path.Combine(_root, InstructionLibrary.FolderName);

    /// <summary>Библиотека на подменённых часах: перерыв между обходами двигает сам тест.</summary>
    private InstructionLibrary Library(string? root = null) => new(root ?? _root, () => _ticks);

    private void Wait(TimeSpan span) => _ticks += (long)(span.TotalSeconds * Stopwatch.Frequency);

    private void WriteFile(string name, string content)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, name), content, new UTF8Encoding(false));
    }

    [Fact]
    public void A_saved_instruction_reads_back_from_a_markdown_file()
    {
        var saved = Library().Save(new Instruction
        {
            Name = "Wallpaper Engine",
            Triggers = ["обои", "Workshop"],
            Text = "Искать оригинал по ID в Steam Workshop.",
            Enabled = false
        });

        Assert.NotNull(saved);
        Assert.Matches("^[0-9a-f]{8}$", saved.Id);

        var file = Path.Combine(Folder, saved.Id + ".md");
        Assert.StartsWith("---", File.ReadAllText(file).TrimStart('﻿'), StringComparison.Ordinal);

        var loaded = Assert.Single(Library().Snapshot());
        Assert.Equal(saved.Id, loaded.Id);
        Assert.Equal("Wallpaper Engine", loaded.Name);
        Assert.Equal(["обои", "Workshop"], loaded.Triggers);
        Assert.Equal("Искать оригинал по ID в Steam Workshop.", loaded.Text);
        Assert.False(loaded.Enabled);
    }

    [Fact]
    public void A_missing_folder_is_an_empty_library() =>
        Assert.Empty(Library().Snapshot());

    /// <summary>
    /// Положенный в папку руками <c>.md</c> без всякой шапки — тоже инструкция: название из
    /// имени файла, текст — весь файл.
    /// </summary>
    [Fact]
    public void A_plain_markdown_file_dropped_into_the_folder_is_picked_up()
    {
        WriteFile("Wallpaper Engine.md", "# Как искать\r\nСмотреть ID в Workshop.\r\n");

        var loaded = Assert.Single(Library().Snapshot());
        Assert.Equal("Wallpaper Engine", loaded.Id);
        Assert.Equal("Wallpaper Engine", loaded.Name);
        Assert.Equal("# Как искать\nСмотреть ID в Workshop.", loaded.Text);
        Assert.True(loaded.Enabled);
        Assert.Empty(loaded.Triggers);
    }

    [Fact]
    public void The_header_survives_what_text_editors_do_to_it()
    {
        WriteFile(
            "a.md",
            "﻿---\r\n" +
            "title: \"Сеть: DNS\"\r\n" +
            "tags:\r\n" +
            "  - dns\r\n" +
            "  - 'Резолвер'\r\n" +
            "author: кто-то\r\n" +
            "enabled: no\r\n" +
            "---\r\n\r\nТекст.\r\n");

        var loaded = Assert.Single(Library().Snapshot());
        Assert.Equal("Сеть: DNS", loaded.Name);
        Assert.Equal(["dns", "Резолвер"], loaded.Triggers);
        Assert.False(loaded.Enabled);
        Assert.Equal("Текст.", loaded.Text);
    }

    [Fact]
    public void A_file_with_a_header_but_no_body_is_not_an_instruction()
    {
        WriteFile("empty.md", "---\nname: Пусто\n---\n\n   \n");
        Assert.Empty(Library().Snapshot());
    }

    [Fact]
    public void An_unclosed_header_is_read_as_text()
    {
        WriteFile("open.md", "---\nэто не шапка\n");
        var loaded = Assert.Single(Library().Snapshot());
        Assert.Equal("open", loaded.Name);
        Assert.Contains("это не шапка", loaded.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Triggers_are_split_trimmed_deduplicated_and_capped()
    {
        var many = Enumerable.Range(1, 30).Select(i => "слово" + i);
        var triggers = InstructionLibrary.NormalizeTriggers(
            ["  Обои , обои;WORKSHOP", "", "workshop", new string('x', 80), .. many]);

        Assert.Equal("Обои", triggers[0]);
        Assert.Equal("WORKSHOP", triggers[1]);
        Assert.Equal(InstructionLibrary.TriggerLengthLimit, triggers[2].Length);
        Assert.Equal(InstructionLibrary.TriggerLimit, triggers.Count);
    }

    [Fact]
    public void The_name_is_one_line_and_capped()
    {
        var name = InstructionLibrary.TrimName("  Первая\r\nвторая  " + new string('я', 100));
        Assert.DoesNotContain('\n', name);
        Assert.StartsWith("Первая вторая", name, StringComparison.Ordinal);
        Assert.Equal(InstructionLibrary.NameLimit, name.Length);
    }

    /// <summary>
    /// Кольцо контекста собирает промпт на каждое обновление экрана — папку оно не обходит, а
    /// берёт готовый список; перечитывается только файл, который поменялся.
    /// </summary>
    [Fact]
    public void Only_a_changed_file_is_read_again_and_not_more_often_than_the_interval()
    {
        WriteFile("a.md", "текст a");
        WriteFile("b.md", "текст b");
        var library = Library();

        Assert.Equal(2, library.Snapshot().Count);
        Assert.Equal(2, library.ParseCount);

        // Внутри перерыва папку не обходим вовсе: правка руками пока не видна.
        WriteFile("b.md", "текст b, исправленный и длиннее");
        Assert.Equal("текст b", library.Snapshot().Single(item => item.Id == "b").Text);
        Assert.Equal(2, library.ParseCount);

        Wait(InstructionLibrary.RescanInterval + TimeSpan.FromMilliseconds(1));
        Assert.Equal("текст b, исправленный и длиннее", library.Snapshot().Single(item => item.Id == "b").Text);
        Assert.Equal(3, library.ParseCount);
    }

    [Fact]
    public void Own_writes_are_visible_at_once()
    {
        var library = Library();
        Assert.Empty(library.Snapshot());

        var saved = library.Save(new Instruction { Name = "Сразу", Text = "видно" })!;
        Assert.Single(library.Snapshot());

        Assert.True(library.Delete(saved.Id));
        Assert.Empty(library.Snapshot());
        Assert.False(File.Exists(Path.Combine(Folder, saved.Id + ".md")));
    }

    [Fact]
    public void Switching_the_profile_switches_the_folder()
    {
        var other = Path.Combine(_root, "profiles", "p2");
        var library = Library();
        library.Save(new Instruction { Name = "Первый профиль", Text = "a" });

        library.UseRoot(other);
        Assert.Empty(library.Snapshot());
        library.Save(new Instruction { Name = "Второй профиль", Text = "b" });

        Assert.Equal("Второй профиль", Assert.Single(Library(other).Snapshot()).Name);
        Assert.Equal("Первый профиль", Assert.Single(Library().Snapshot()).Name);
    }

    [Fact]
    public void An_unfinished_write_is_not_an_instruction()
    {
        WriteFile("a.md.tmp", "полфайла");
        Assert.Empty(Library().Snapshot());
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..\\settings")]
    [InlineData("../settings")]
    [InlineData("a:b")]
    [InlineData(" pad")]
    [InlineData("")]
    public void An_id_that_could_leave_the_folder_is_refused(string id) =>
        Assert.False(InstructionLibrary.IsUsableId(id));

    [Fact]
    public void Saving_with_a_foreign_id_gets_a_fresh_one_instead_of_writing_outside()
    {
        var saved = Library().Save(new Instruction { Id = "..\\settings", Name = "x", Text = "y" })!;
        Assert.Matches("^[0-9a-f]{8}$", saved.Id);
        Assert.False(File.Exists(Path.Combine(_root, "settings.md")));
    }

    [Fact]
    public void Find_takes_an_id_or_a_name_in_any_case()
    {
        var library = Library();
        var saved = library.Save(new Instruction { Name = "Wallpaper Engine", Text = "t" })!;

        Assert.Equal(saved.Id, library.Find(saved.Id.ToUpperInvariant())?.Id);
        Assert.Equal(saved.Id, library.Find("[" + saved.Id + "]")?.Id);
        Assert.Equal(saved.Id, library.Find("wallpaper engine")?.Id);
        Assert.Null(library.Find("нет такой"));
        Assert.Null(library.Find("  "));
    }

    [Fact]
    public void Import_takes_new_ids_renames_clashes_and_counts_the_empty()
    {
        var library = Library();
        library.Save(new Instruction { Name = "Сеть", Text = "своя" });

        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var first = Path.Combine(outside, "Сеть.md");
        var second = Path.Combine(outside, "empty.md");
        File.WriteAllText(first, "чужая");
        File.WriteAllText(second, "   ");

        var result = library.Import([first, second, Path.Combine(outside, "missing.md")]);

        var imported = Assert.Single(result.Imported);
        Assert.Equal("Сеть (2)", imported.Name);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(2, library.Snapshot().Count);
    }

    [Fact]
    public void An_exported_file_imports_back_as_the_same_instruction()
    {
        var original = new Instruction
        {
            Name = "Экспорт",
            Triggers = ["один", "два"],
            Text = "строка 1\nстрока 2",
            Enabled = true,
            CreatedAt = DateTime.Now
        };
        var path = Path.Combine(_root, "out", "export.md");

        Assert.True(InstructionLibrary.Export(original, path));
        var imported = Assert.Single(Library().Import([path]).Imported);

        Assert.Equal(original.Name, imported.Name);
        Assert.Equal(original.Triggers, imported.Triggers);
        Assert.Equal(original.Text, imported.Text);
    }

    [Fact]
    public void Switching_off_keeps_the_file_and_hides_it_from_the_model()
    {
        var library = Library();
        var saved = library.Save(new Instruction { Name = "Выкл", Text = "t" })!;

        Assert.False(library.SetEnabled(saved.Id, false)!.Enabled);
        Assert.Single(library.Snapshot());
        Assert.Empty(library.EnabledSnapshot());
    }

    [Fact]
    public void Unique_names_count_up_and_stay_within_the_limit()
    {
        Assert.Equal("Имя", InstructionLibrary.UniqueName("Имя", ["Другое"]));
        Assert.Equal("Имя (3)", InstructionLibrary.UniqueName("Имя", ["имя", "Имя (2)"]));

        var longName = new string('я', InstructionLibrary.NameLimit);
        var unique = InstructionLibrary.UniqueName(longName, [longName]);
        Assert.EndsWith(" (2)", unique, StringComparison.Ordinal);
        Assert.True(unique.Length <= InstructionLibrary.NameLimit);
    }
}

public sealed class InstructionBriefingTests
{
    private static Instruction Item(string id, string name, bool enabled = true, params string[] triggers) =>
        new() { Id = id, Name = name, Text = "текст", Enabled = enabled, Triggers = triggers };

    [Fact]
    public void Without_active_instructions_there_is_no_block()
    {
        Assert.Equal("", InstructionBriefing.ForChat([]));
        Assert.Equal("", InstructionBriefing.ForChat([Item("a", "Выключена", enabled: false)]));
    }

    [Fact]
    public void The_index_lists_ids_names_and_triggers_but_never_the_text()
    {
        var block = InstructionBriefing.ForChat(
        [
            Item("a1b2c3d4", "Wallpaper Engine", true, "обои", "workshop"),
            Item("off", "Скрытая", enabled: false),
            Item("e5f6", "Сеть")
        ]);

        Assert.StartsWith(InstructionBriefing.Header, block, StringComparison.Ordinal);
        Assert.Contains("[a1b2c3d4] Wallpaper Engine — triggers: обои, workshop", block, StringComparison.Ordinal);
        Assert.Contains("[e5f6] Сеть", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Скрытая", block, StringComparison.Ordinal);
        Assert.DoesNotContain("текст", block, StringComparison.Ordinal);
        Assert.Contains(ReadInstructionTool.ToolName, block, StringComparison.Ordinal);

        // Порядок — как у списка: старые первыми, строка за строкой.
        Assert.True(block.IndexOf("[a1b2c3d4]", StringComparison.Ordinal) <
                    block.IndexOf("[e5f6]", StringComparison.Ordinal));
    }

    /// <summary>
    /// Системные промпты программы пишутся правилами, без образцов (см. памятку): модель тянет
    /// ответ к приведённому случаю и путает с ним похожие.
    /// </summary>
    [Fact]
    public void The_rules_carry_no_worked_example()
    {
        var block = InstructionBriefing.ForChat([Item("a", "Тема")]);
        Assert.DoesNotContain("example", block, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("e.g.", block, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("for instance", block, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ReadInstructionToolTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amarin-read-instruction-" + Guid.NewGuid().ToString("N"));

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

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task A_known_id_returns_the_text_and_marks_the_call()
    {
        var library = new InstructionLibrary(_root);
        var saved = library.Save(new Instruction
        {
            Name = "Wallpaper Engine",
            Triggers = ["обои"],
            Text = "Смотреть ID в Workshop."
        })!;

        var result = await new ReadInstructionTool(library).ExecuteAsync(Args($$"""{"id":"{{saved.Id}}"}"""));

        Assert.True(result.Success);
        Assert.StartsWith("Instruction «Wallpaper Engine»", result.Output, StringComparison.Ordinal);
        Assert.Contains("Смотреть ID в Workshop.", result.Output, StringComparison.Ordinal);
        Assert.Equal(new InstructionRef(saved.Id, "Wallpaper Engine"), result.Instruction);

        // Первая строка — то, что лента покажет строкой результата.
        Assert.StartsWith("Instruction «Wallpaper Engine»", ChatToolPreview.Summarize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_id_fails_and_names_the_ones_that_exist()
    {
        var library = new InstructionLibrary(_root);
        var saved = library.Save(new Instruction { Name = "Есть", Text = "t" })!;

        var result = await new ReadInstructionTool(library).ExecuteAsync(Args("""{"id":"nope"}"""));

        Assert.False(result.Success);
        Assert.Contains(saved.Id, result.Output, StringComparison.Ordinal);
        Assert.Contains("do not guess", result.Output, StringComparison.Ordinal);
        Assert.Null(result.Instruction);
    }

    [Fact]
    public async Task A_switched_off_instruction_is_not_handed_out()
    {
        var library = new InstructionLibrary(_root);
        var saved = library.Save(new Instruction { Name = "Выкл", Text = "секрет", Enabled = false })!;

        var result = await new ReadInstructionTool(library).ExecuteAsync(Args($$"""{"id":"{{saved.Id}}"}"""));

        Assert.False(result.Success);
        Assert.DoesNotContain("секрет", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"id":"..\\settings"}""")]
    [InlineData("""{"id":""}""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    public async Task Bad_arguments_are_a_refusal_not_a_crash(string json)
    {
        var result = await new ReadInstructionTool(new InstructionLibrary(_root)).ExecuteAsync(Args(json));
        Assert.False(result.Success);
    }

    /// <summary>
    /// С заголовком ответ обязан уложиться в срез, после которого движок молча обрезает вывод
    /// инструмента: иначе модель прочла бы инструкцию без конца и не узнала бы об этом.
    /// </summary>
    [Fact]
    public void The_longest_allowed_text_reaches_the_model_whole()
    {
        var text = new string('ж', InstructionLibrary.TextLimit);
        var output = ReadInstructionTool.Format(new Instruction
        {
            Id = "abcdef12",
            Name = new string('н', InstructionLibrary.NameLimit),
            Triggers = Enumerable.Range(0, InstructionLibrary.TriggerLimit)
                .Select(i => new string('т', InstructionLibrary.TriggerLengthLimit - 2) + i.ToString("00"))
                .ToList(),
            Text = text
        });

        var forApi = ChatToolPreview.FormatForApi(ToolResult.Ok(output));
        Assert.Contains(text, forApi, StringComparison.Ordinal);
        Assert.DoesNotContain("обрезано", forApi, StringComparison.Ordinal);
    }

    [Fact]
    public void A_longer_hand_written_text_is_cut_and_says_so()
    {
        var output = ReadInstructionTool.Format(new Instruction
        {
            Id = "x",
            Name = "Длинная",
            Text = new string('ж', InstructionLibrary.TextLimit + 500)
        });

        Assert.Contains("cut off", output, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('ж', InstructionLibrary.TextLimit + 1), output, StringComparison.Ordinal);
    }
}

/// <summary>
/// Движок чата: оглавление и инструмент чтения ездят вместе и только при включённых
/// инструкциях.
/// </summary>
public sealed class ChatInstructionsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amarin-chat-instructions-" + Guid.NewGuid().ToString("N"));

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

    private static AgentOptions Options() => new()
    {
        ApiKey = "test",
        BaseUrl = "https://api.venice.ai/api/v1",
        Model = "grok-4-6",
        MaxToolRounds = 3
    };

    private static ChatEngine Engine(HttpMessageHandler handler, InstructionLibrary library)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = Options();
        var settings = AppSettings.CreateDefault();
        return new ChatEngine(
            new VeniceClient(http, options),
            options,
            () => settings,
            new ToolRegistry([new ReadInstructionTool(library)]),
            agents: null,
            instructions: library);
    }

    [Fact]
    public void Without_instructions_the_prompt_and_the_tools_say_nothing_about_them()
    {
        var library = new InstructionLibrary(_root);
        library.Save(new Instruction { Name = "Выключена", Text = "t", Enabled = false });
        var engine = Engine(new ScriptedHandler(_ => Sse("")), library);

        Assert.DoesNotContain(InstructionBriefing.Header, engine.CurrentSystemPrompt(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            engine.ToolsFor(library.EnabledSnapshot()),
            tool => tool.Function.Name == ReadInstructionTool.ToolName);
    }

    [Fact]
    public void An_active_instruction_brings_the_index_and_the_tool()
    {
        var library = new InstructionLibrary(_root);
        var saved = library.Save(new Instruction { Name = "Wallpaper Engine", Text = "t" })!;
        var engine = Engine(new ScriptedHandler(_ => Sse("")), library);

        var prompt = engine.CurrentSystemPrompt();
        Assert.Contains("[" + saved.Id + "] Wallpaper Engine", prompt, StringComparison.Ordinal);

        // Оглавление стоит до блока моделей: тот меняется с моделью хода, и неизменная голова
        // промпта должна оставаться неизменной.
        var models = prompt.IndexOf("MODELS", StringComparison.Ordinal);
        if (models >= 0)
        {
            Assert.True(prompt.IndexOf(InstructionBriefing.Header, StringComparison.Ordinal) < models);
        }

        Assert.Contains(
            engine.ToolsFor(library.EnabledSnapshot()),
            tool => tool.Function.Name == ReadInstructionTool.ToolName);
    }

    [Fact]
    public async Task The_model_reads_an_instruction_and_the_answer_is_marked()
    {
        var library = new InstructionLibrary(_root);
        var saved = library.Save(new Instruction
        {
            Name = "Wallpaper Engine",
            Triggers = ["обои"],
            Text = "secret-trick-from-the-instruction"
        })!;

        var bodies = new List<string>();
        var handler = new ScriptedHandler(body =>
        {
            bodies.Add(body);
            return bodies.Count == 1
                ? SseWithToolCall(ReadInstructionTool.ToolName, $$"""{"id":"{{saved.Id}}"}""")
                : Sse("Готово.");
        });

        var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
        await Engine(handler, library).RunTurnAsync(session, "где найти оригинал обоев?", new SilentObserver(), CancellationToken.None);

        Assert.Equal(2, bodies.Count);
        Assert.Contains(InstructionBriefing.Header, bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"" + ReadInstructionTool.ToolName + "\"", bodies[0], StringComparison.Ordinal);
        // Маркер латиницей: тело запроса — JSON, и кириллицу сериализатор экранирует.
        Assert.Contains("secret-trick-from-the-instruction", bodies[1], StringComparison.Ordinal);

        var assistant = session.Messages.Last(message => message.Role == "assistant");
        var call = Assert.Single(Assert.Single(assistant.ToolRounds).Calls);
        Assert.Equal(new InstructionRef(saved.Id, "Wallpaper Engine"), call.Instruction);
        Assert.Equal("Готово.", assistant.Text);
    }

    private static HttpResponseMessage Sse(string text)
    {
        var payload =
            "data: {\"model\":\"grok-4-6\",\"choices\":[{\"delta\":{\"content\":" +
            JsonSerializer.Serialize(text) + "}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n" +
            "data: [DONE]\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }

    private static HttpResponseMessage SseWithToolCall(string tool, string arguments)
    {
        var payload =
            "data: {\"model\":\"grok-4-6\",\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\"," +
            "\"function\":{\"name\":\"" + tool + "\",\"arguments\":" + JsonSerializer.Serialize(arguments) + "}}]}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n" +
            "data: [DONE]\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }

    private sealed class ScriptedHandler(Func<string, HttpResponseMessage> script) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return script(body);
        }
    }

    private sealed class SilentObserver : IChatTurnObserver
    {
        public void OnUserAppended(ChatDisplayMessage user)
        {
        }

        public void OnAssistantStarted(ChatDisplayMessage assistant)
        {
        }

        public void OnAssistantText(ChatDisplayMessage assistant)
        {
        }

        public void OnToolsChanged(ChatDisplayMessage assistant)
        {
        }

        public void OnAssistantCompleted(ChatDisplayMessage assistant)
        {
        }

        public void OnAssistantCancelled(ChatDisplayMessage assistant)
        {
        }

        public void OnError(string message)
        {
        }
    }
}

/// <summary>Инструкции уезжают в архив данных и возвращаются из него.</summary>
public sealed class InstructionBundleTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-instr-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "chats"));
        return root;
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    [Theory]
    [InlineData("instructions/a1b2c3d4.md")]
    [InlineData("profiles/p1/instructions/Wallpaper Engine.md")]
    public void Instruction_files_travel_with_the_settings(string relative) =>
        Assert.Equal(DataUsage.SettingsKey, DataUsage.ClassifyAppFile(relative));

    [Fact]
    public void A_replace_import_brings_the_instructions_back()
    {
        var source = NewRoot();
        var target = NewRoot();
        var archive = Path.Combine(Path.GetTempPath(), "amarin-instr-" + Guid.NewGuid().ToString("N") + DataBundle.FileExtension);

        try
        {
            new AppSettingsStore(source).Save(AppSettings.CreateDefault());
            var saved = new InstructionLibrary(source).Save(new Instruction
            {
                Name = "Сеть",
                Triggers = ["dns"],
                Text = "Сначала ipconfig /flushdns."
            })!;
            new InstructionLibrary(target).Save(new Instruction { Name = "Старая", Text = "уйдёт" });

            new DataBundleExporter(source).Write(archive, DataCategory.All);
            var result = new DataBundleImporter(target).Apply(archive, DataCategory.All, DataImportMode.Replace);
            Assert.True(result.Ok, result.Error.ToString());

            var restored = Assert.Single(new InstructionLibrary(target).Snapshot());
            Assert.Equal(saved.Id, restored.Id);
            Assert.Equal("Сеть", restored.Name);
            Assert.Equal(["dns"], restored.Triggers);
            Assert.Equal("Сначала ipconfig /flushdns.", restored.Text);
        }
        finally
        {
            Cleanup(source, target, archive);
        }
    }

    /// <summary>
    /// Одна и та же инструкция, заведённая руками на двух машинах, приходит с другим
    /// идентификатором — и всё равно не двоится.
    /// </summary>
    [Fact]
    public void A_merge_import_adds_the_missing_and_keeps_what_is_there()
    {
        var source = NewRoot();
        var target = NewRoot();
        var archive = Path.Combine(Path.GetTempPath(), "amarin-instr-" + Guid.NewGuid().ToString("N") + DataBundle.FileExtension);

        try
        {
            new AppSettingsStore(source).Save(AppSettings.CreateDefault());
            var sourceLibrary = new InstructionLibrary(source);
            sourceLibrary.Save(new Instruction { Name = "Одна", Text = "одинаковая" });
            sourceLibrary.Save(new Instruction { Name = "Своя", Text = "другой текст" });
            sourceLibrary.Save(new Instruction { Name = "Новая", Text = "новая" });

            var targetLibrary = new InstructionLibrary(target);
            targetLibrary.Save(new Instruction { Name = "Одна", Text = "одинаковая" });
            targetLibrary.Save(new Instruction { Name = "Своя", Text = "свой текст" });

            new DataBundleExporter(source).Write(archive, DataCategory.All);
            var result = new DataBundleImporter(target).Apply(archive, DataCategory.All, DataImportMode.Merge);
            Assert.True(result.Ok, result.Error.ToString());

            var names = new InstructionLibrary(target).Snapshot().Select(item => item.Name).Order().ToList();
            Assert.Equal(["Новая", "Одна", "Своя", "Своя (2)"], names);
        }
        finally
        {
            Cleanup(source, target, archive);
        }
    }
}
