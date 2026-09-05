namespace Amarin.Core;

internal static class ChatSessionEdit
{
    public static bool TruncateFromMessage(ChatSession session, string messageId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var index = session.Messages.FindIndex(item => item.Id == messageId);
        if (index < 0)
        {
            return false;
        }

        session.Messages.RemoveRange(index, session.Messages.Count - index);
        TruncateApiToMatchDisplay(session);
        session.UpdatedAt = DateTime.Now;
        return true;
    }

    public static bool DeleteAssistantTurn(ChatSession session, string assistantId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var index = session.Messages.FindIndex(item => item.Id == assistantId);
        if (index < 0)
        {
            return false;
        }

        var start = index;
        if (index > 0 &&
            session.Messages[index - 1].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            start = index - 1;
        }

        session.Messages.RemoveRange(start, session.Messages.Count - start);
        TruncateApiToMatchDisplay(session);
        session.UpdatedAt = DateTime.Now;
        return true;
    }

    public static bool ReplaceUserText(ChatSession session, string userId, string newText)
    {
        ArgumentNullException.ThrowIfNull(session);
        var text = newText.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var index = session.Messages.FindIndex(item =>
            item.Id == userId && item.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        session.Messages[index].Text = text;
        if (index + 1 < session.Messages.Count)
        {
            session.Messages.RemoveRange(index + 1, session.Messages.Count - index - 1);
        }

        TruncateApiToMatchDisplay(session);
        var images = session.Messages[index].Images;
        for (var i = session.ApiMessages.Count - 1; i >= 0; i--)
        {
            if (session.ApiMessages[i].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                // Rewriting the text must not silently drop the images the user attached.
                session.ApiMessages[i] = new ChatMessage
                {
                    Role = "user",
                    Content = images.Count == 0
                        ? ChatContent.Text(text)
                        : ChatContent.VisionMultiple(
                            string.IsNullOrWhiteSpace(text) ? "Посмотри на изображение." : text,
                            images)
                };
                break;
            }
        }

        session.UpdatedAt = DateTime.Now;
        return true;
    }

    public static ChatDisplayMessage? LastUser(ChatSession session) =>
        session.Messages.LastOrDefault(item => item.Role.Equals("user", StringComparison.OrdinalIgnoreCase));

    private static int UserCount(ChatSession session) =>
        session.Messages.Count(item => item.Role.Equals("user", StringComparison.OrdinalIgnoreCase));

    private static bool IsUser(ChatMessage message) =>
        message.Role.Equals("user", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinalAssistant(ChatMessage message) =>
        message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
        (message.ToolCalls is null || message.ToolCalls.Count == 0);

    private static void TruncateApiToMatchDisplay(ChatSession session)
    {
        var usersWanted = UserCount(session);
        if (usersWanted <= 0)
        {
            session.ApiMessages.Clear();
            return;
        }

        var pendingUser = session.Messages.Count > 0 &&
                          session.Messages[^1].Role.Equals("user", StringComparison.OrdinalIgnoreCase);
        var turnsToClose = pendingUser ? usersWanted - 1 : usersWanted;
        var usersSeen = 0;
        var turnsClosed = 0;
        var keep = 0;

        for (var i = 0; i < session.ApiMessages.Count; i++)
        {
            var message = session.ApiMessages[i];
            if (IsUser(message))
            {
                usersSeen++;
                if (usersSeen > usersWanted)
                {
                    break;
                }
            }

            keep = i + 1;

            if (IsFinalAssistant(message))
            {
                turnsClosed++;
            }

            if (pendingUser && usersSeen == usersWanted && IsUser(message))
            {
                break;
            }

            if (!pendingUser && turnsClosed >= turnsToClose && IsFinalAssistant(message))
            {
                break;
            }
        }

        if (keep < session.ApiMessages.Count)
        {
            session.ApiMessages.RemoveRange(keep, session.ApiMessages.Count - keep);
        }
    }
}
