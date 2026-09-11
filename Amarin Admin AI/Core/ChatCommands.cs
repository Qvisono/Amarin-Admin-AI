namespace Amarin.Core;

/// <summary>A slash command typed into the chat box, already split into verb and argument.</summary>
internal readonly record struct ChatCommand(string Name, string Argument, string Complexity);

/// <summary>
/// Parses the handful of slash commands the WPF chat box understands. Anything that does not
/// match exactly is not a command and must be sent to the model as ordinary text — users write
/// paths and code starting with "/" all the time.
/// </summary>
internal static class ChatCommands
{
    public const string Agent = "agent";
    public const string Heavy = "heavy";
    public const string Lite = "lite";
    public const string Fast = "fast";

    /// <summary>
    /// Recognises <c>/agent &lt;prompt&gt;</c> (heavy by default), plus <c>/agent lite …</c>,
    /// <c>/agent-lite …</c> and the same pair for <c>fast</c>. Returns null when the input is not a command,
    /// including <c>/agent</c> with no prompt — there is nothing to run, so it stays plain text.
    /// </summary>
    public static ChatCommand? TryParse(string? input)
    {
        var text = input?.TrimStart() ?? "";
        if (text.Length < 2 || text[0] != '/')
        {
            return null;
        }

        var space = text.IndexOfAny([' ', '\t', '\r', '\n']);
        var verb = (space < 0 ? text[1..] : text[1..space]).Trim();
        var rest = (space < 0 ? "" : text[(space + 1)..]).Trim();

        var complexity = Heavy;
        if (verb.Equals($"{Agent}-{Lite}", StringComparison.OrdinalIgnoreCase))
        {
            complexity = Lite;
        }
        else if (verb.Equals($"{Agent}-{Fast}", StringComparison.OrdinalIgnoreCase))
        {
            complexity = Fast;
        }
        else if (verb.Equals($"{Agent}-{Heavy}", StringComparison.OrdinalIgnoreCase))
        {
            complexity = Heavy;
        }
        else if (verb.Equals(Agent, StringComparison.OrdinalIgnoreCase))
        {
            // "/agent lite do the thing" — the modifier is only a modifier when a prompt follows,
            // otherwise "lite" is the prompt itself.
            var (word, tail) = SplitFirstWord(rest);
            if (tail.Length > 0 &&
                (word.Equals(Lite, StringComparison.OrdinalIgnoreCase) ||
                 word.Equals(Fast, StringComparison.OrdinalIgnoreCase) ||
                 word.Equals(Heavy, StringComparison.OrdinalIgnoreCase)))
            {
                complexity = word.ToLowerInvariant();
                rest = tail;
            }
        }
        else
        {
            return null;
        }

        return rest.Length == 0 ? null : new ChatCommand(Agent, rest, complexity);
    }

    private static (string Word, string Tail) SplitFirstWord(string text)
    {
        var space = text.IndexOfAny([' ', '\t', '\r', '\n']);
        return space < 0
            ? (text, "")
            : (text[..space], text[(space + 1)..].Trim());
    }
}
