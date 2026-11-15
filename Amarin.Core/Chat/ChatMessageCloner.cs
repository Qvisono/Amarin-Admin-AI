namespace Amarin.Core;

internal static class ChatMessageCloner
{
    public static ChatMessage CloneForStorage(ChatMessage message, bool preserveReplayBlocks = true) => new()
    {
        Role = message.Role,
        Content = ChatContent.Clone(message.Content),
        ToolCalls = message.ToolCalls?.ToList(),
        ToolCallId = message.ToolCallId,
        Name = message.Name,
        ReplayBlocks = preserveReplayBlocks ? message.ReplayBlocks : null
    };

    public static List<ChatMessage> CloneAll(IEnumerable<ChatMessage> messages, bool preserveReplayBlocks = true)
    {
        if (messages is IReadOnlyList<ChatMessage> list)
        {
            var result = new List<ChatMessage>(list.Count);
            for (var i = 0; i < list.Count; i++)
            {
                result.Add(CloneForStorage(list[i], preserveReplayBlocks));
            }

            return result;
        }

        var fallback = new List<ChatMessage>();
        foreach (var message in messages)
        {
            fallback.Add(CloneForStorage(message, preserveReplayBlocks));
        }

        return fallback;
    }
}
