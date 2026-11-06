using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amarin.Core;

/// <summary>
/// Метки <c>cache_control</c> в сообщениях OpenAI-совместимого запроса: Claude и Qwen за OpenRouter
/// и Qwen напрямую. У протокола Anthropic свои, в <see cref="AnthropicMessages"/>.
/// </summary>
/// <remarks>
/// Кэш — совпадение начала запроса, и метка говорит, докуда начало стоит запомнить. Меток три:
/// системный промпт (вместе с инструментами он общий для всех чатов и агентов той же модели), конец
/// истории и последний текст перед последним ответом модели. Третья нужна агенту с мышью и браузером:
/// прошлый снимок к следующему раунду заменяется заглушкой, и запись на конце истории не совпала бы
/// с новым запросом ни разу, то есть наценка за неё платилась бы впустую. Место перед ответом модели
/// к этому времени уже не меняется. Срок не указан намеренно: обычные пять минут стоят 1,25 цены
/// входа за запись, а часовые — вдвое.
/// </remarks>
internal static class PromptCacheMarks
{
    /// <summary>Сообщения с метками. Исходный список не меняется: на него уходит повтор без меток.</summary>
    /// <param name="conversation">
    /// Запрос из разговора. Разовому метится только системный промпт: его хвост не повторится.
    /// </param>
    public static List<ChatMessage> Apply(IReadOnlyList<ChatMessage> messages, bool conversation)
    {
        var marked = new List<ChatMessage>(messages);
        var system = marked.FindIndex(IsSystem);
        if (system >= 0)
        {
            marked[system] = Marked(marked[system]);
        }

        if (!conversation)
        {
            return marked;
        }

        var last = LastTextAtOrBefore(marked, marked.Count - 1);
        if (last < 0)
        {
            return marked;
        }

        marked[last] = Marked(marked[last]);
        var answer = marked.FindLastIndex(last, IsAssistant);
        var before = answer > 0 ? LastTextAtOrBefore(marked, answer - 1) : -1;
        if (before >= 0)
        {
            marked[before] = Marked(marked[before]);
        }

        return marked;
    }

    private static bool IsSystem(ChatMessage message) =>
        message.Role.Equals("system", StringComparison.OrdinalIgnoreCase);

    private static bool IsAssistant(ChatMessage message) =>
        message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase);

    /// <summary>Вызов инструмента без текста метку не несёт: она садится только на блок текста.</summary>
    private static int LastTextAtOrBefore(List<ChatMessage> messages, int index)
    {
        for (var i = index; i >= 0; i--)
        {
            if (!IsSystem(messages[i]) && !string.IsNullOrWhiteSpace(ChatContent.ReadText(messages[i].Content)))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Метка садится на последний блок текста: сообщение строкой становится массивом блоков.</summary>
    private static ChatMessage Marked(ChatMessage message)
    {
        var parts = message.Content is { ValueKind: JsonValueKind.Array } array
            ? JsonNode.Parse(array.GetRawText()) as JsonArray ?? []
            : [new JsonObject { ["type"] = "text", ["text"] = ChatContent.ReadText(message.Content) }];

        if (parts.OfType<JsonObject>().LastOrDefault(IsText) is not { } text)
        {
            return message;
        }

        text["cache_control"] = AnthropicMessages.CacheControl();
        return new ChatMessage
        {
            Role = message.Role,
            Content = AnthropicMessages.ToElement(parts),
            ToolCalls = message.ToolCalls,
            ToolCallId = message.ToolCallId,
            Name = message.Name,
            ReplayBlocks = message.ReplayBlocks
        };
    }

    private static bool IsText(JsonObject part) =>
        (string?)part["type"] == "text" && !string.IsNullOrEmpty((string?)part["text"]);
}
