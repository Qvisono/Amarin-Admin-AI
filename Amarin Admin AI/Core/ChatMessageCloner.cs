namespace Amarin.Core;

internal static class ChatMessageCloner
{
    public static ChatMessage CloneForStorage(ChatMessage message) => new()
    {
        Role = message.Role,
        Content = ChatContent.Clone(message.Content),
        ToolCalls = message.ToolCalls?.ToList(),
        ToolCallId = message.ToolCallId,
        Name = message.Name
    };

    public static List<ChatMessage> CloneAll(IEnumerable<ChatMessage> messages)
    {
        if (messages is IReadOnlyList<ChatMessage> list)
        {
            var result = new List<ChatMessage>(list.Count);
            for (var i = 0; i < list.Count; i++)
            {
                result.Add(CloneForStorage(list[i]));
            }

            return result;
        }

        var fallback = new List<ChatMessage>();
        foreach (var message in messages)
        {
            fallback.Add(CloneForStorage(message));
        }

        return fallback;
    }
}