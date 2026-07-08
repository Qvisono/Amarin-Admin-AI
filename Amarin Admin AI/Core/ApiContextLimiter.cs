using System.Text.Json;

namespace Amarin.Core;

internal static class ApiContextLimiter
{
    private const int MaxToolContentChars = 12_000;

    public static List<ChatMessage> Prepare(IReadOnlyList<ChatMessage> messages)
    {
        var prepared = messages.Select(NormalizeMessage).ToList();
        ReplaceStaleVisionMessages(prepared);
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

    private static void ReplaceStaleVisionMessages(List<ChatMessage> messages)
    {
        var visionIndexes = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role.Equals("user", StringComparison.OrdinalIgnoreCase) &&
                ContainsVisionContent(messages[i].Content))
            {
                visionIndexes.Add(i);
            }
        }

        if (visionIndexes.Count <= 1)
        {
            return;
        }

        for (var i = 0; i < visionIndexes.Count - 1; i++)
        {
            var index = visionIndexes[i];
            var original = messages[index];
            messages[index] = new ChatMessage
            {
                Role = original.Role,
                Content = ChatContent.Text("[Изображение уже было передано модели на предыдущем шаге]"),
                ToolCalls = original.ToolCalls,
                ToolCallId = original.ToolCallId,
                Name = original.Name
            };
        }
    }

    private static bool ContainsVisionContent(JsonElement? content)
    {
        if (content is null || content.Value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var part in content.Value.EnumerateArray())
        {
            if (part.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString() == "image_url")
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