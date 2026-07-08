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
            return history.Select(CloneMessage).ToList();
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

    private static string ToSingleLine(string text, int maxLength)
    {
        var line = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        while (line.Contains("  ", StringComparison.Ordinal))
        {
            line = line.Replace("  ", " ", StringComparison.Ordinal);
        }

        return line.Length <= maxLength ? line : line[..maxLength] + "…";
    }
}