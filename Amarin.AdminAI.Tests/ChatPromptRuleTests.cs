using System.Text.RegularExpressions;
using Amarin.Composition;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Всё, что модель чата читает как указание, — системный промпт и описания её инструментов —
/// говорит правилом, а не разбором случая (памятка: «В системных промптах — правило, а не разбор
/// случая»).
/// </summary>
/// <remarks>
/// Пример в промпте тянет ответ к себе и к похожим формулировкам вместо того, о чём спросили.
/// Хуже всего пример, замаскированный под образец синтаксиса: в 1.33.0 описание edit_document
/// показывало fill формулой и диапазоном таблицы умножения — ровно того случая, ради которого
/// fill и появился. Поэтому формат здесь показывают обозначением (A1-style, YYYY-MM-DD, HH:mm),
/// а не значением; а процитированные по-русски фразы человека из прежних версий промпта
/// переписаны правилом, которое они иллюстрировали.
/// </remarks>
public sealed partial class ChatPromptRuleTests
{
    private static readonly string[] ExampleMarkers =
        ["e.g.", "for example", "for instance", "such as", "например", "пример"];

    public static TheoryData<string, string> SystemParts => new()
    {
        { nameof(ChatEngine.DefaultTechPrompt), ChatEngine.DefaultTechPrompt },
        { nameof(ChatEngine.TaskRules), ChatEngine.TaskRules },
        { nameof(ChatEngine.FormulaRules), ChatEngine.FormulaRules },
        { nameof(ChatEngine.FileRules), ChatEngine.FileRules },
        { nameof(ChatEngine.DeferredRules), ChatEngine.DeferredRules },
        { nameof(ChatEngine.ComputerRules), ChatEngine.ComputerRules },
        { nameof(ChatEngine.ComputerOffRules), ChatEngine.ComputerOffRules }
    };

    [Theory]
    [MemberData(nameof(SystemParts))]
    public void The_chat_system_prompt_states_rules_and_quotes_no_cases(string name, string text) =>
        AssertRulesOnly(name, text);

    [Fact]
    public void The_chat_tools_describe_themselves_by_rule_and_notation()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-prompt-rules-" + Guid.NewGuid().ToString("N"));
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
                new InitAgentTool(new AgentSlotLimiter(), null!),
                new MessageAgentTool(new AgentSlotLimiter(), null!),
                new AgentRefusedTool(new AgentSlotLimiter(), null!),
                new ReadInstructionTool(new InstructionLibrary(root)),
                new DeferredTaskTool(new DeferredBook(root))
            ];

            // Новый инструмент чата обязан попасть и сюда, иначе его описание никто не проверит.
            // Инструменты без своего переключателя (ToolCatalog.SwitchOf) — тоже.
            Assert.Equal(
                ToolCatalog.Chat.Concat(ToolCatalog.SwitchOf.Keys).Order(StringComparer.Ordinal),
                tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
            foreach (var tool in tools)
            {
                AssertRulesOnly(tool.Name, tool.Description + "\n" + tool.ParametersSchema.GetRawText());
            }

            // i_refuse — не инструмент реестра, но его описание модель читает так же.
            AssertRulesOnly(
                Refusals.ToolName,
                Refusals.Definition.Function.Description + "\n" + Refusals.Definition.Function.Parameters.GetRawText());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_picture_is_found_unless_the_user_asks_for_a_new_one_to_be_made()
    {
        // Вместо списка русских глаголов «найди / скинь» и «нарисуй» — правило, которое они
        // иллюстрировали: искать по умолчанию, рисовать — когда просят сделать новую.
        Assert.Contains("FIND whenever the user wants a picture and does not ask for a new one", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains("DRAW with generate_image when the user asks for a new picture to be made", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains("Try at least three", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Правило «делать, а не отвечать «не знаю»» доходит и до техпромпта, сохранённого человеком,
    /// и стоит после него: в старой копии остаётся «ответы из своих знаний инструментов не
    /// требуют», и чат отвечал «проверьте сами» вместо поиска и агента.
    /// </summary>
    [Fact]
    public void The_task_rules_reach_a_saved_old_tech_prompt_and_come_after_it()
    {
        var settings = AppSettings.CreateDefault();
        settings.TechAiPrompt = LegacyTechPrompts.V18;
        var options = new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "grok-4-6", MaxToolRounds = 3 };
        var engine = new ChatEngine(
            new VeniceClient(new HttpClient { BaseAddress = new Uri("https://api.venice.ai/api/v1/") }, options),
            options,
            () => settings,
            new ToolRegistry([]),
            agents: null);

        var prompt = engine.CurrentSystemPrompt(new ChatSession());

        var tech = prompt.IndexOf(LegacyTechPrompts.V18.Trim(), StringComparison.Ordinal);
        var rules = prompt.IndexOf(ChatEngine.TaskRules, StringComparison.Ordinal);
        Assert.True(tech >= 0, "the saved tech prompt is missing");
        Assert.True(rules > tech, "the task rules must come after the saved tech prompt they override");
        Assert.True(rules < prompt.IndexOf(ChatEngine.FileRules, StringComparison.Ordinal));
    }

    /// <summary>
    /// Отказ — только вызовом i_refuse с причинами (1.36.0), и только после проверки причины в
    /// сети: модель отказывала, сочтя сайт неофициальным, не проверив этого. Текст «Я, модель,
    /// отказываюсь…» собирает программа (<see cref="Refusals.FinalText"/>), когда моделей сильнее нет.
    /// </summary>
    [Fact]
    public void A_refusal_is_a_call_and_comes_only_after_an_online_check()
    {
        foreach (var rules in new[] { ChatEngine.TaskRules, Agent.ExecutorRules })
        {
            Assert.Contains("You refuse only by calling i_refuse with your reasons", Flat(rules), StringComparison.Ordinal);
            Assert.DoesNotContain("I, <the name of your model>,", rules, StringComparison.Ordinal);
            Assert.Contains("check with search_web every fact the refusal would rest on", rules, StringComparison.Ordinal);
            Assert.Contains("completely sure", rules, StringComparison.Ordinal);

            // Отказ, названный «не получилось» или «я этого не делал», — тоже отказ: так агент
            // обошёл правило, ни разу не проверив причину.
            Assert.Contains("is a refusal, whatever words you put it in", Flat(rules), StringComparison.Ordinal);
        }

        // Чат пересказывал отказ агента своими словами и сам ничего не проверял. Теперь он может
        // переубедить агента или отдать его задачу модели сильнее.
        var chat = Flat(ChatEngine.TaskRules);
        Assert.Contains("When the agent leaves out all or part of the task by choice, that is a refusal too", chat, StringComparison.Ordinal);
        Assert.Contains("including what the agent reports to you", chat, StringComparison.Ordinal);
        Assert.Contains("message_agent", chat, StringComparison.Ordinal);
        Assert.Contains("call agent_refused", chat, StringComparison.Ordinal);
        Assert.Contains("never call i_refuse for it", chat, StringComparison.Ordinal);

        // Агент знает, что после отчёта ему может написать чат.
        Assert.Contains("the chat assistant that gave you the task may write to you", Flat(Agent.ExecutorRules), StringComparison.Ordinal);

        static string Flat(string text) => Regex.Replace(text, @"\s+", " ");
    }

    private static void AssertRulesOnly(string name, string text)
    {
        foreach (var marker in ExampleMarkers)
        {
            Assert.False(text.Contains(marker, StringComparison.OrdinalIgnoreCase), $"{name}: \"{marker}\"");
        }

        // Промпт и описания пишутся по-английски: кириллица в них — процитированная фраза человека.
        Assert.False(text.Any(c => c is >= 'Ѐ' and <= 'ӿ'), $"{name} quotes Cyrillic text");

        var sample = SampleValue().Match(text);
        Assert.False(sample.Success, $"{name} shows a sample value instead of notation: {sample.Value}");
    }

    /// <summary>Значение вместо обозначения: адрес ячейки, время, дата, длительность, формула.</summary>
    [GeneratedRegex(@"(?<![A-Za-z])\$?[A-Z]{1,3}\$?\d+(?![\d-])|\b\d{1,2}:\d{2}\b|\b\d{4}-\d{2}-\d{2}\b|\b\d+(?:\.\d+)?\s?(?:m|h|d|s|w|min|minutes?|hours?|days?)\b|=[A-Z]+\(", RegexOptions.CultureInvariant)]
    private static partial Regex SampleValue();
}
