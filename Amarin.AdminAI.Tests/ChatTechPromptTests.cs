using System.Text.RegularExpressions;
using Amarin.Composition;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Сокращённый технический промпт чата (1.38.0): короче прежнего, но без потерянных правил.
/// </summary>
/// <remarks>
/// Убраны только повторы: список инструментов дублировал их схемы, а блок вложений - правила файлов.
/// Здесь проверяется, что каждое правило прежнего текста осталось в новом или в том, что модель
/// получает рядом с ним. Поведение моделей проверено отдельно платными парными прогонами
/// (<c>tools/prompt-eval</c>); эти тесты не дают тексту незаметно растерять правила при следующей правке.
/// </remarks>
public sealed class ChatTechPromptTests
{
    /// <summary>Правила прежнего текста, которые нигде больше модели не сообщаются.</summary>
    public static TheoryData<string> OwnRules => new()
    {
        // Тон и язык.
        "casual, warm, a bit playful",
        "Short replies for small talk, thorough ones for real tasks",
        "Match the user's language and energy",
        "ASCII only",
        "at most one per reply",
        // Вложения.
        "Attached pictures you simply see",
        "<document> block with numbered parts",
        "never guess what the unread rest says",
        // Найти или нарисовать.
        "FIND whenever the user wants a picture and does not ask for a new one",
        "a drawing instead is a wrong answer",
        "Try at least three",
        "never tell them to open the site themselves",
        "DRAW with generate_image when the user asks for a new picture to be made",
        "carry the answer better than words",
        "say so plainly first, and only then offer to draw one",
        "Never quietly substitute one for the other",
        "Do not refuse over copyright, licences, ratings or authorship",
        // Показ картинок.
        "![short caption](handle)",
        "A handle you never write is never shown",
        "Never invent a handle",
        "data: URI",
        "Show the handle, not the original URL",
        "rather than the page's caption",
        "\"here is the image\"",
        "One picture per request unless the user asked for several",
        "Do not redraw because you dislike the result",
        "near-duplicate second generate_image",
        // Агент.
        "open URLs in the browser, scrape pages, run programs, inspect the disk, change Windows",
        "Call init_agent as a tool, never as chat text",
        "Nothing after }",
        "Escape \" and \\\\ inside the strings",
        "never cut the prompt with \"...\"",
        "restates the user's actual request: goal, paths, what to change",
        "blank slate",
        "You do not pick the model",
        "which agent runs it is not",
        "Never a model name, a tier or an instruction about which agent to use",
        "A wish about speed goes into notes, not into the prompt",
        "Up to 4 agents run in parallel",
        "Wait for all reports before answering",
        "re-run init_agent with a clearer prompt",
        "facts, numbers, names, statuses",
        "Never refuse, send the user elsewhere, or hand them steps to do by hand",
        "Conversation and answers from your own knowledge need no tools",
        // Строка перед инструментом.
        "THINKING OUT LOUD",
        "Right before every tool call",
        "tools block",
        "Never recap what already happened",
        "a line that only announces the call is not worth writing",
        // Строка, набранная во время работы.
        "A LINE TYPED WHILE YOU WORK",
        "never ignore it or answer as if it had been there all",
        "reaches it at its next step without losing what it has found",
        "stops the agent or moves it to the fast model",
        "do not re-launch the same agent to pass the line on",
        "say in one sentence what actually happened to it"
    };

    [Theory]
    [MemberData(nameof(OwnRules))]
    public void Every_rule_of_the_previous_prompt_is_kept(string rule) =>
        Assert.Contains(rule, Flat(ChatEngine.DefaultTechPrompt), StringComparison.Ordinal);

    [Fact]
    public void The_prompt_is_noticeably_shorter_than_the_previous_default()
    {
        var now = Flat(ChatEngine.DefaultTechPrompt).Length;
        var before = Flat(LegacyTechPrompts.V19).Length;

        Assert.True(now <= before * 0.85, $"{now} of {before} characters");
    }

    /// <summary>
    /// Прежний список TOOLS повторял описания инструментов. Удалить его можно, пока то же самое
    /// модель читает в схемах и в правилах файлов, которые приписываются к любому техпромпту.
    /// </summary>
    [Fact]
    public void What_the_removed_tool_list_said_reaches_the_model_through_schemas_and_file_rules()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-tech-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            List<ITool> tools =
            [
                .. AppComposition.FileTools(new FileToolState()),
                new WebSearchTool((_, _) => Task.FromResult("")),
                new GenerateImageTool((_, _, _, _, _) => Task.FromResult("")),
                new FetchImageTool(),
                new YouTubeTranscriptTool(),
                new InitAgentTool(new AgentSlotLimiter(), null!)
            ];
            var described = tools.ToDictionary(tool => tool.Name, tool => tool.Description, StringComparer.Ordinal);

            Assert.Contains("list a folder", described["read_file"], StringComparison.Ordinal);
            Assert.Contains("Word, Excel, PowerPoint and PDF", described["read_file"], StringComparison.Ordinal);
            Assert.Contains("Long files come in parts", described["read_file"], StringComparison.Ordinal);
            Assert.Contains("plain-text file", described["write_file"], StringComparison.Ordinal);
            Assert.Contains("Word document by exact replacement", described["edit_file"], StringComparison.Ordinal);
            Assert.Contains("Create a folder", described["create_folder"], StringComparison.Ordinal);
            Assert.Contains("PDF, HTML or text file", described["create_document"], StringComparison.Ordinal);
            Assert.Contains("Change a Word, Excel, PowerPoint or PDF file", described["edit_document"], StringComparison.Ordinal);
            Assert.Contains("Save a picture from this chat to disk", described["save_image"], StringComparison.Ordinal);
            Assert.Contains("list of the source URLs", described["search_web"], StringComparison.Ordinal);
            Assert.Contains("Generate a new image", described["generate_image"], StringComparison.Ordinal);
            Assert.Contains("no domain allowlist", described["fetch_image"], StringComparison.Ordinal);
            Assert.Contains("nothing is saved to disk", described["fetch_image"], StringComparison.Ordinal);
            Assert.Contains("subtitle transcript", described["youtube_transcript"], StringComparison.Ordinal);
            Assert.Contains("sysadmin agent on this PC", described["init_agent"], StringComparison.Ordinal);

            // Блок ATTACHMENTS прежнего текста: путь вложения и папки относительных путей.
            Assert.Contains("read the rest of it with read_file by that path", Flat(ChatEngine.FileRules), StringComparison.Ordinal);
            Assert.Contains("A relative path means the user's Downloads folder", Flat(ChatEngine.FileRules), StringComparison.Ordinal);
            Assert.Contains("Desktop\\... and Documents\\... mean those folders", Flat(ChatEngine.FileRules), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Чат отвечает на языке, на котором ему написали. Язык интерфейса в его промпт не
    /// подставляется: иначе английский вопрос при русском интерфейсе получал бы русский ответ.
    /// </summary>
    [Fact]
    public void The_chat_prompt_follows_the_users_language_and_not_the_interface_language()
    {
        var settings = AppSettings.CreateDefault();
        var options = new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "grok-4-6", MaxToolRounds = 3 };
        var engine = new ChatEngine(
            new VeniceClient(new HttpClient { BaseAddress = new Uri("https://api.venice.ai/api/v1/") }, options),
            options,
            () => settings,
            new ToolRegistry([]),
            agents: null);

        var prompt = engine.CurrentSystemPrompt(new ChatSession());

        Assert.Contains(ChatEngine.DefaultTechPrompt, prompt, StringComparison.Ordinal);
        Assert.Contains("Match the user's language and energy", Flat(prompt), StringComparison.Ordinal);
        Assert.DoesNotContain("Interface language", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Loc.Get(Loc.LanguageNameKey), prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"(?i)(reply|answer|respond|write) (only )?in (russian|english)", prompt);
    }

    /// <summary>
    /// Начало промпта - тон и язык - не переписывается. Подробное правило языка со словами о
    /// промпте на другом языке уводило Haiku с размышлением на язык русского основного промпта,
    /// а пересказ тона сдвигал оценки ответов вниз; прежний абзац таких сдвигов не давал.
    /// </summary>
    [Fact]
    public void The_opening_about_tone_and_language_stays_word_for_word()
    {
        static string Opening(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n")[0];

        Assert.Equal(Opening(LegacyTechPrompts.V19), Opening(ChatEngine.DefaultTechPrompt));
        Assert.DoesNotContain("this prompt", ChatEngine.DefaultTechPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_saved_copy_of_the_previous_default_is_migrated_to_the_new_one()
    {
        // Архив - именно прежний текст со списком инструментов, а не копия нового.
        Assert.Contains("read_file(path, offset", LegacyTechPrompts.V19, StringComparison.Ordinal);
        Assert.DoesNotContain("read_file(path", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains(LegacyTechPrompts.V19, LegacyTechPrompts.All);
        Assert.DoesNotContain(ChatEngine.DefaultTechPrompt, LegacyTechPrompts.All);

        // Сохранённая копия могла получить другие переводы строк и пробелы по краям.
        var settings = AppSettings.CreateDefault();
        settings.TechAiPrompt = "\n" + LegacyTechPrompts.V19.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        Assert.True(AppSettingsStore.MigrateLegacyChatPrompts(settings));
        Assert.Equal("", settings.TechAiPrompt);

        // Свой текст человека не трогается.
        settings.TechAiPrompt = LegacyTechPrompts.V19 + "\nMy own rule.";
        Assert.False(AppSettingsStore.MigrateLegacyChatPrompts(settings));
    }

    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");
}
