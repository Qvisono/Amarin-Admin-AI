using System.Text.Json;

namespace Amarin.Tools;

public sealed class AskUserTool : ITool
{
    public const string ToolName = "ask_user";

    private readonly Func<string, IReadOnlyList<string>, bool, CancellationToken, Task<string>> _prompt;

    public AskUserTool(Func<string, IReadOnlyList<string>, bool, CancellationToken, Task<string>> prompt) =>
        _prompt = prompt;

    public string Name => ToolName;
    public string Description =>
        "Ask the user ONE question with numbered options when you need their explicit choice to continue. " +
        "Never use for download/install confirmation or non-AllowedDomains permission " +
        "(use download_file — the app confirms, including unlisted domains). " +
        "Never for opening URLs or facts from other tools. " +
        "The user replies by option number. Use 2-6 clear, distinct options in Russian.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "question": {
              "type": "string",
              "description": "Question to ask the user in Russian"
            },
            "options": {
              "type": "array",
              "items": { "type": "string" },
              "minItems": 2,
              "maxItems": 6,
              "description": "Numbered answer choices shown to the user"
            },
            "allow_custom_answer": {
              "type": "boolean",
              "description": "If true, user may type free text instead of a number"
            }
          },
          "required": ["question", "options"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("question", out var questionProp) ||
            string.IsNullOrWhiteSpace(questionProp.GetString()))
        {
            return ToolResult.Fail("Missing required parameter: question");
        }

        if (!arguments.TryGetProperty("options", out var optionsProp) ||
            optionsProp.ValueKind != JsonValueKind.Array)
        {
            return ToolResult.Fail("Missing required parameter: options");
        }

        var options = optionsProp.EnumerateArray()
            .Select(o => o.GetString())
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o!)
            .ToList();

        if (options.Count < 2)
        {
            return ToolResult.Fail("At least 2 non-empty options are required.");
        }

        if (options.Count > 6)
        {
            options = options.Take(6).ToList();
        }

        var allowCustom = arguments.TryGetProperty("allow_custom_answer", out var customProp) &&
                          customProp.ValueKind == JsonValueKind.True;

        var question = questionProp.GetString()!;
        var answer = await _prompt(question, options, allowCustom, cancellationToken);

        return ToolResult.Ok($"Пользователь выбрал: {answer}");
    }
}