using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>
/// Отказ модели (1.36.0): вызов <c>i_refuse</c> с причинами, а не текст.
/// </summary>
/// <remarks>
/// <para>
/// Отказ текстом программа не отличает от ответа, и модель, сославшаяся на свои правила, ставила
/// точку там, где задачу сделала бы другая. Вызов программа видит: в чате она отдаёт задачу
/// модели сильнее (тяжёлая, затем обход), у агента отказ видит чат и сам решает — переубедить
/// агента или поднять его выше (<c>agent_refused</c>).
/// </para>
/// <para>
/// Это не <see cref="ITool"/>: вызов не исполняется и до шлюза не доходит. Ответ, в котором он
/// стоит, разбирают чат (<c>ChatEngine.RunToolLoopAsync</c>) и агент (<c>Agent</c>) сами.
/// </para>
/// </remarks>
internal static class Refusals
{
    public const string ToolName = "i_refuse";

    public static ToolDefinition Definition { get; } = new()
    {
        Function = new FunctionDefinition
        {
            Name = ToolName,
            Description =
                "Refuse all or part of the task you were given. This call is the only way to refuse: " +
                "never write a refusal, a partial refusal or a reason for not doing something as text. " +
                "Nothing else in the reply that contains this call is kept or shown. " +
                "Call it only after the checks your instructions require before a refusal.",
            Parameters = JsonSchema.Parse("""
                {
                  "type": "object",
                  "properties": {
                    "reasons": {
                      "type": "string",
                      "description": "Why you refuse, in the user's language: each reason in a few words with the fact it rests on. No apologies or advice."
                    }
                  },
                  "required": ["reasons"]
                }
                """)
        }
    };

    /// <summary>
    /// Есть ли в ответе отказ. Причины — из аргументов (грязный JSON модели разбирает
    /// <see cref="ToolArguments.Parse"/>); не названы — пустая строка.
    /// </summary>
    public static bool TryFind(IReadOnlyList<ToolCall>? calls, out string reasons)
    {
        reasons = "";
        var call = calls?.FirstOrDefault(static call =>
            string.Equals(call.Function.Name, ToolName, StringComparison.OrdinalIgnoreCase));
        if (call is null)
        {
            return false;
        }

        try
        {
            var arguments = ToolArguments.Parse(call.Function.Arguments ?? "{}");
            if (arguments.ValueKind == JsonValueKind.Object &&
                arguments.TryGetProperty("reasons", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                reasons = value.GetString()?.Trim() ?? "";
            }
        }
        catch (JsonException)
        {
            // Нечитаемые аргументы — всё равно отказ: модель его заявила, причины просто не дошли.
        }

        return true;
    }

    /// <summary>
    /// Отказ человеку, когда моделей сильнее не осталось: «Я, модель, отказываюсь…».
    /// </summary>
    /// <param name="fallbackReasons">Текст ответа, если причины в вызове не названы.</param>
    public static string FinalText(string modelId, string reasons, string? fallbackReasons = null)
    {
        var why = !string.IsNullOrWhiteSpace(reasons)
            ? reasons.Trim()
            : !string.IsNullOrWhiteSpace(fallbackReasons)
                ? fallbackReasons.Trim()
                : Loc.Get("S.Refusal.NoReasons");
        return Loc.Format("S.Refusal.Final", VeniceModelCatalog.GetDisplayName(modelId), why);
    }
}
