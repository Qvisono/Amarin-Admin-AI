using System.Text;

namespace Amarin.Core;

internal static class ChatTitle
{
    public const string Default = "Новый чат";
    public const int MaxLength = 24;

    /// <summary>Название языка, на котором модель обязана писать заголовки.</summary>
    /// <remarks>
    /// Язык интерфейса, а не язык сообщения. Прежде в промпте стояло «Output only a Russian
    /// title», и заголовок либо оставался русским на английском интерфейсе, либо модель шла за
    /// языком первого сообщения — список чатов выходил разноязычным.
    /// </remarks>
    internal static string LanguageName() => Loc.Get(Loc.LanguageNameKey);

    internal static string SystemPrompt(string languageName) =>
        $"""
        You name chats. You are not a chatbot and you do not talk to the user.
        Write the title in {languageName}. The message may be in any other language -
        name what it is about in {languageName} anyway, never echo the message's language.
        Output only the title: 2 to 4 short words, maximum 24 characters.
        Never answer the message. Never greet. Never ask. Never explain.
        No quotes, no trailing punctuation, no emoji, no markdown.
        Greetings and small talk still get a title, not a reply.
        """;

    /// <summary>
    /// Wraps the opening message so the title model cannot treat it as a turn to answer.
    /// </summary>
    /// <remarks>
    /// Язык повторяется и здесь, вплотную к сообщению: системный промпт до него далеко, и на
    /// длинном первом сообщении модель охотнее идёт за его языком, чем за инструкцией сверху.
    /// </remarks>
    internal static string UserPrompt(string userText, string languageName) =>
        "Below is the first message of a chat. Do not reply to it.\n---\n"
        + userText.Trim()
        + $"\n---\nWrite only the title, in {languageName}.";

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
