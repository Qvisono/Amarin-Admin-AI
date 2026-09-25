using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Отдаёт модели чата полный текст инструкции пользователя по её идентификатору.
/// </summary>
/// <remarks>
/// В системном промпте лежит только оглавление — идентификатор, название и триггеры
/// (<see cref="InstructionBriefing"/>). Тексты целиком раздули бы каждый запрос, хотя к делу
/// обычно относится одна инструкция из десятка, а то и ни одной. Модель сама решает, какую
/// открыть, и открывает её этим вызовом.
/// <para>
/// Путь к файлу из аргумента не собирается никогда: поиск идёт только по уже загруженному
/// списку, поэтому «..\settings» в аргументе просто не найдётся.
/// </para>
/// </remarks>
public sealed class ReadInstructionTool : ITool
{
    public const string ToolName = "read_instruction";

    private readonly InstructionLibrary _library;

    internal ReadInstructionTool(InstructionLibrary library) => _library = library;

    public string Name => ToolName;

    public string Description =>
        "Load the full text of one of the user's saved instructions from the USER INSTRUCTIONS " +
        "index in the system prompt. Call it before acting whenever the request relates to an " +
        "entry's name or trigger words. Pass the id exactly as shown in square brackets.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "id": {
              "type": "string",
              "description": "The instruction id from the index, without the square brackets."
            }
          },
          "required": ["id"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var id = arguments.ValueKind == JsonValueKind.Object &&
                 arguments.TryGetProperty("id", out var idProperty) &&
                 idProperty.ValueKind == JsonValueKind.String
            ? idProperty.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(id))
        {
            return Task.FromResult(ToolResult.Fail(
                "Missing required parameter: id. " + Available()));
        }

        var instruction = _library.Find(id);
        if (instruction is null)
        {
            return Task.FromResult(ToolResult.Fail(
                $"No instruction with id \"{id.Trim()}\". {Available()} " +
                "Use an id from the index exactly; do not guess one."));
        }

        if (!instruction.Enabled)
        {
            return Task.FromResult(ToolResult.Fail(
                $"Instruction \"{instruction.Name}\" is turned off by the user. " +
                "Answer without it and do not call it again."));
        }

        return Task.FromResult(new ToolResult(
            true,
            Format(instruction),
            Instruction: new InstructionRef(instruction.Id, instruction.Name)));
    }

    /// <summary>
    /// Ответ для модели. Первая строка — название: её же лента показывает строкой результата.
    /// </summary>
    internal static string Format(Instruction instruction)
    {
        var text = instruction.Text;
        var cut = text.Length > InstructionLibrary.TextLimit;
        if (cut)
        {
            text = text[..InstructionLibrary.TextLimit];
        }

        var builder = new StringBuilder();
        builder.Append("Instruction «").Append(instruction.Name).Append("» [").Append(instruction.Id).Append(']').Append('\n');
        if (instruction.Triggers.Count > 0)
        {
            builder.Append("Triggers: ").Append(string.Join(", ", instruction.Triggers)).Append('\n');
        }

        builder.Append("---\n");
        builder.Append(text);
        if (cut)
        {
            builder.Append("\n[The rest of this instruction was cut off: it is longer than ")
                .Append(InstructionLibrary.TextLimit)
                .Append(" characters.]");
        }

        return builder.ToString();
    }

    private string Available()
    {
        var ids = _library.EnabledSnapshot().Select(item => item.Id).ToList();
        return ids.Count == 0
            ? "The user has no active instructions; answer without one."
            : "Available ids: " + string.Join(", ", ids) + ".";
    }
}
