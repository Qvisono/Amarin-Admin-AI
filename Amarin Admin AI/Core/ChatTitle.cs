using System.Text;

namespace Amarin.Core;

internal static class ChatTitle
{
    public const string Default = "Новый чат";
    public const int MaxLength = 24;

    internal const string SystemPrompt = """
        You name chats. You are not a chatbot and you do not talk to the user.
        Output only a Russian title: 2 to 4 short words, maximum 24 characters.
        Never answer the message. Never greet. Never ask. Never explain.
        No quotes, no trailing punctuation, no emoji, no markdown.
        Greetings and small talk still get a title (e.g. Приветствие), not a reply.
        """;

    /// <summary>
    /// Wraps the opening message so the title model cannot treat it as a turn to answer.
    /// </summary>
    internal static string UserPrompt(string userText) =>
        "Below is the first message of a chat. Do not reply to it. Write only the title.\n---\n"
        + userText.Trim()
        + "\n---";

    public static bool IsDefault(string? title) =>
        string.IsNullOrWhiteSpace(title) ||
        title.Trim().Equals(Default, StringComparison.Ordinal);

    public static string ResolveModel(AppSettings settings, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(settings.TitleModelId) &&
            !VeniceModelCatalog.IsAuto(settings.TitleModelId))
        {
            return settings.TitleModelId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(fallback) &&
            !VeniceModelCatalog.IsAuto(fallback))
        {
            return fallback.Trim();
        }

        return "openai-gpt-56-luna";
    }

    public static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var line = raw.Trim();
        var newline = line.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
        {
            line = line[..newline].Trim();
        }

        line = line.Trim().Trim('"', '\'', '`', '«', '»', '“', '”');
        line = CollapseWhitespace(line);
        line = line.TrimEnd('.', '!', '?', '…', ':');
        line = line.Trim().Trim('"', '\'', '`', '«', '»', '“', '”');
        if (line.Length == 0)
        {
            return null;
        }

        if (line.Length > MaxLength)
        {
            var cut = line[..MaxLength].TrimEnd();
            var space = cut.LastIndexOf(' ');
            line = space >= 8 ? cut[..space] : cut;
        }

        return line.Length == 0 ? null : line;
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var gap = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                gap = true;
                continue;
            }

            if (gap && sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(ch);
            gap = false;
        }

        return sb.ToString();
    }
}
