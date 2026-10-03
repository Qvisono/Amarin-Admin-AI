using System.Text.Json;

namespace Amarin.Core;

internal static class ApiContextLimiter
{
    private const int MaxToolContentChars = 12_000;

    public static List<ChatMessage> Prepare(IReadOnlyList<ChatMessage> messages)
    {
        var prepared = new List<ChatMessage>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            prepared.Add(NormalizeMessage(messages[i]));
        }

        ReplaceStaleAttachmentMessages(prepared);
        TruncateToolMessages(prepared);
        return prepared;
    }

    private static ChatMessage NormalizeMessage(ChatMessage message)
    {
        var clone = CloneMessage(message);

        if (!clone.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ||
            clone.ToolCalls is not { Count: > 0 })
        {
            return clone;
        }

        var text = ChatContent.ReadText(clone.Content);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return clone;
        }

        return new ChatMessage
        {
            Role = clone.Role,
            Content = null,
            ToolCalls = clone.ToolCalls,
            ToolCallId = clone.ToolCallId,
            Name = clone.Name
        };
    }

    private static void TruncateToolMessages(List<ChatMessage> messages)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (!messages[i].Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = ChatContent.ReadText(messages[i].Content) ?? string.Empty;
            if (text.Length <= MaxToolContentChars)
            {
                continue;
            }

            var original = messages[i];
            messages[i] = new ChatMessage
            {
                Role = original.Role,
                Content = ChatContent.Text(
                    text[..MaxToolContentChars] + "\n... [обрезано для контекста API]"),
                ToolCalls = original.ToolCalls,
                ToolCallId = original.ToolCallId,
                Name = original.Name
            };
        }
    }

    /// <summary>
    /// В контексте остаётся только самое свежее сообщение с вложениями, прежние заменяются
    /// строкой-заметкой.
    /// </summary>
    /// <remarks>
    /// Вложение едет в запрос целиком, base64. Без этой замены каждый следующий ход длинной
    /// переписки заново тащил бы и все картинки, и все документы — на десятимегабайтном PDF
    /// это мегабайты трафика на каждую реплику. Существо ответа при этом не теряется: то, что
    /// модель вычитала из файла, уже написано в её предыдущем сообщении.
    /// </remarks>
    private static void ReplaceStaleAttachmentMessages(List<ChatMessage> messages)
    {
        var indexes = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role.Equals("user", StringComparison.OrdinalIgnoreCase) &&
                ContainsAttachmentContent(messages[i].Content))
            {
                indexes.Add(i);
            }
        }

        if (indexes.Count <= 1)
        {
            return;
        }

        for (var i = 0; i < indexes.Count - 1; i++)
        {
            var index = indexes[i];
            var original = messages[index];
            messages[index] = new ChatMessage
            {
                Role = original.Role,
                Content = ChatContent.Text(StaleNote(original.Content)),
                ToolCalls = original.ToolCalls,
                ToolCallId = original.ToolCallId,
                Name = original.Name
            };
        }
    }

    /// <summary>Заметка вместо вложений. Файлы называет по именам — модель на них ссылается.</summary>
    private static string StaleNote(JsonElement? content)
    {
        var names = FileNames(content);
        var note = names.Count > 0
            ? $"[Файлы уже были переданы модели: {string.Join(", ", names)}]"
            : "[Изображение уже было передано модели на предыдущем шаге]";

        var text = ChatContent.ReadText(content);
        return string.IsNullOrWhiteSpace(text) ? note : text + "\n\n" + note;
    }

    private static List<string> FileNames(JsonElement? content)
    {
        var names = new List<string>();
        if (content is null || content.Value.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (var part in content.Value.EnumerateArray())
        {
            if (part.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString() == "file" &&
                part.TryGetProperty("file", out var file) &&
                file.TryGetProperty("filename", out var name) &&
                name.GetString() is { Length: > 0 } value)
            {
                names.Add(value);
            }
        }

        return names;
    }

    private static bool ContainsAttachmentContent(JsonElement? content)
    {
        if (content is null || content.Value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var part in content.Value.EnumerateArray())
        {
            if (part.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString() is "image_url" or "file")
            {
                return true;
            }
        }

        return false;
    }

    private static ChatMessage CloneMessage(ChatMessage message) => new()
    {
        Role = message.Role,
        Content = ChatContent.Clone(message.Content),
        ToolCalls = message.ToolCalls?.ToList(),
        ToolCallId = message.ToolCallId,
        Name = message.Name
    };
}