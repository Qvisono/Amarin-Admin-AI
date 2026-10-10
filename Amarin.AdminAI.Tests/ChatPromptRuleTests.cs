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
        { nameof(ChatEngine.FormulaRules), ChatEngine.FormulaRules },
        { nameof(ChatEngine.FileRules), ChatEngine.FileRules },
        { nameof(ChatEngine.DeferredRules), ChatEngine.DeferredRules }
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
                new ReadInstructionTool(new InstructionLibrary(root)),
                new DeferredTaskTool(new DeferredBook(root))
            ];

            // Новый инструмент чата обязан попасть и сюда, иначе его описание никто не проверит.
            Assert.Equal(ToolCatalog.Chat.Order(StringComparer.Ordinal), tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
            foreach (var tool in tools)
            {
                AssertRulesOnly(tool.Name, tool.Description + "\n" + tool.ParametersSchema.GetRawText());
            }
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
