using System.Buffers;
using System.Text.Json;

namespace Amarin.Core;

internal static class SessionHistoryCompressor
{
    private const int PreserveRecentMessages = 10;
    private const int ToolSummaryMaxLength = 220;
    private const int AssistantSummaryMaxLength = 400;

    public static List<ChatMessage> Compress(IReadOnlyList<ChatMessage> history)
    {
        if (history.Count <= PreserveRecentMessages)
        {
            var clone = new List<ChatMessage>(history.Count);
            for (var i = 0; i < history.Count; i++)
            {
                clone.Add(CloneMessage(history[i]));
            }

            return clone;
        }

        var cutoff = history.Count - PreserveRecentMessages;
        var result = new List<ChatMessage>(history.Count);

        for (var i = 0; i < history.Count; i++)
        {
            result.Add(i < cutoff ? CompressMessage(history[i]) : CloneMessage(history[i]));
        }

        return result;
    }

    private static ChatMessage CompressMessage(ChatMessage message) => message.Role switch
    {
        "tool" => new ChatMessage
        {
            Role = message.Role,
            ToolCallId = message.ToolCallId,
            Name = message.Name,
            Content = ChatContent.Text(CompressToolContent(message))
        },
        "assistant" when message.ToolCalls is null or { Count: 0 } => new ChatMessage
        {
            Role = message.Role,
            Content = CompressAssistantContent(message.Content),
            ToolCalls = message.ToolCalls
        },
        _ => CloneMessage(message)
    };

    private static ChatMessage CloneMessage(ChatMessage message) =>
        ChatMessageCloner.CloneForStorage(message);

    private static string CompressToolContent(ChatMessage message)
    {
        var text = ChatContent.ReadText(message.Content) ?? string.Empty;
        var toolName = message.Name ?? "tool";
        var prefix = text.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) ? "FAIL" : "OK";
        var body = text.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
            ? text["ERROR:".Length..].Trim()
            : text;

        var summary = ToSingleLine(body, ToolSummaryMaxLength);
        return $"[{prefix}] {toolName}: {summary}";
    }

    private static JsonElement? CompressAssistantContent(JsonElement? content)
    {
        var text = ChatContent.ReadText(content);
        if (string.IsNullOrWhiteSpace(text))
        {
            return ChatContent.Clone(content);
        }

        return ChatContent.Text(ToSingleLine(text, AssistantSummaryMaxLength));
    }

    internal static string ToSingleLine(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var rented = ArrayPool<char>.Shared.Rent(text.Length);
        try
        {
            var written = 0;
            var pendingSpace = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\r')
                {
                    continue;
                }

                if (c is '\n' or '\t')
                {
                    c = ' ';
                }

                if (c == ' ')
                {
                    if (written == 0 || pendingSpace)
                    {
                        continue;
                    }

                    pendingSpace = true;
                    continue;
                }

                if (pendingSpace)
                {
                    rented[written++] = ' ';
                    pendingSpace = false;
                }

                rented[written++] = c;
            }

            if (written == 0)
            {
                return string.Empty;
            }

            if (written <= maxLength)
            {
                return new string(rented, 0, written);
            }

            return string.Concat(rented.AsSpan(0, maxLength), "…");
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }
}