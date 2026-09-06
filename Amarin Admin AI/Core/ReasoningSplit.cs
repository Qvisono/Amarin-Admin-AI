using System.Text;

namespace Amarin.Core;

/// <summary>
/// Separates a model's chain of thought from its actual answer when the two arrive in the same
/// stream.
/// </summary>
/// <remarks>
/// <para>
/// Well-behaved reasoning models put the thinking on <c>reasoning_content</c>, which never
/// reaches the transcript. Several families — GLM above all, Kimi with its own bracket glyphs —
/// wrap it in tags inside <c>content</c> instead, and Venice ignores
/// <c>strip_thinking_response</c> for them (see <see cref="ChatMessageDelta.ReasoningContent"/>).
/// The markup then goes straight through the markdown renderer, which is built with HTML
/// disabled and prints <c>&lt;think&gt;</c> as literal text.
/// </para>
/// <para>
/// The split runs on the accumulated text after every chunk, so it has to cope with a tag that
/// is still open: everything past an unclosed opener counts as thinking, which keeps the answer
/// from flickering into view and back out again as the closing tag arrives.
/// </para>
/// <para>
/// The harder case is the opposite one, and it is what GLM actually does: its chat template
/// pre-fills the opening tag into the assistant turn, so the reply arrives already inside the
/// thinking block and carries only a closing tag. Nothing is wrapped in anything, and a parser
/// looking for a pair sees plain prose. An unmatched closer is therefore read as the end of a
/// block that began at the start of the message.
/// </para>
/// </remarks>
internal static class ReasoningSplit
{
    /// <summary>
    /// Opener/closer pairs, longest first so <c>&lt;thinking&gt;</c> is not read as
    /// <c>&lt;think&gt;</c> followed by stray text. Matching is case-insensitive.
    /// </summary>
    private static readonly (string Open, string Close)[] Markers =
    [
        ("<thinking>", "</thinking>"),
        ("<reasoning>", "</reasoning>"),
        ("<think>", "</think>"),
        ("<reason>", "</reason>"),
        ("◁think▷", "◁/think▷")
    ];

    /// <summary>
    /// Returns the chain of thought and the answer. Text with no markers comes back untouched
    /// as the answer, which is the overwhelmingly common case and costs one scan.
    /// </summary>
    public static (string Reasoning, string Answer) Split(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOfAny(['<', '◁']) < 0)
        {
            return ("", text ?? "");
        }

        var reasoning = new StringBuilder();
        var answer = new StringBuilder();
        var position = 0;

        while (position < text.Length)
        {
            var (open, close, at) = NextOpener(text, position);
            var (orphan, orphanAt) = NextCloser(text, position);

            // A closer with no opener in front of it. The chat template of a GLM-class model
            // pre-fills the opening tag into the assistant turn, so the reply comes back already
            // inside the thinking block and the first tag in it is the closing one. Everything
            // up to that point is deliberation, however much it reads like an answer.
            if (orphanAt >= 0 && (at < 0 || orphanAt < at))
            {
                Append(reasoning, text[position..orphanAt]);
                position = orphanAt + orphan.Length;
                continue;
            }

            if (at < 0)
            {
                answer.Append(text, position, text.Length - position);
                break;
            }

            answer.Append(text, position, at - position);
            var bodyStart = at + open.Length;
            var end = text.IndexOf(close, bodyStart, StringComparison.OrdinalIgnoreCase);

            if (end < 0)
            {
                // Still streaming, or the model never closed the tag. Either way the rest is
                // thinking: showing it as the answer is the exact bug this class exists to fix.
                Append(reasoning, text[bodyStart..]);
                break;
            }

            Append(reasoning, text[bodyStart..end]);
            position = end + close.Length;
        }

        return (reasoning.ToString().Trim(), answer.ToString().Trim());
    }

    private static (string Close, int At) NextCloser(string text, int from)
    {
        var best = (Close: "", At: -1);
        foreach (var (_, close) in Markers)
        {
            var at = text.IndexOf(close, from, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (best.At < 0 || at < best.At))
            {
                best = (close, at);
            }
        }

        return best;
    }

    private static (string Open, string Close, int At) NextOpener(string text, int from)
    {
        var best = (Open: "", Close: "", At: -1);
        foreach (var (open, close) in Markers)
        {
            var at = text.IndexOf(open, from, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (best.At < 0 || at < best.At))
            {
                best = (open, close, at);
            }
        }

        return best;
    }

    private static void Append(StringBuilder target, string piece)
    {
        var trimmed = piece.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        if (target.Length > 0)
        {
            target.Append("\n\n");
        }

        target.Append(trimmed);
    }
}
