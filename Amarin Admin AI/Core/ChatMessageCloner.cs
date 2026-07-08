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

    public static List<ChatMessage> CloneAll(IEnumerable<ChatMessage> messages) =>
        messages.Select(CloneForStorage).ToList();
}