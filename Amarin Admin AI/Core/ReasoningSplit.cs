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

    /// <summary>Первые символы всех маркеров: по ним текст без размышления отбраковывается разом.</summary>
    private static readonly char[] MarkerStarts = ['<', '◁'];

    /// <summary>
    /// Есть ли в тексте хоть один символ, с которого маркер может начаться. Отрицательный ответ
    /// означает, что разбирать нечего, и вызывающий вправе считать весь текст ответом.
    /// </summary>
    /// <remarks>
    /// Вынесено наружу ради <see cref="ChatStreamAccumulator"/>: он следит за этим признаком по
    /// приходящим кускам и тогда не собирает накопленный ответ в строку вовсе.
    /// </remarks>
    internal static bool MayContainMarker(string? text) =>
        !string.IsNullOrEmpty(text) && text.IndexOfAny(MarkerStarts) >= 0;

    /// <summary>
    /// Returns the chain of thought and the answer. Text with no markers comes back untouched
    /// as the answer, which is the overwhelmingly common case and costs one scan.
    /// </summary>
    public static (string Reasoning, string Answer) Split(string text)
    {
        if (!MayContainMarker(text))
        {
            return ("", text ?? "");
        }

        var reasoning = new StringBuilder();
        var answer = new StringBuilder();
        var position = 0;

        while (position < text.Length)
        {
            var (open, close, at, isCloser) = NextMarker(text, position);

            if (at < 0)
            {
                answer.Append(text, position, text.Length - position);
                break;
            }

            // A closer with no opener in front of it. The chat template of a GLM-class model
            // pre-fills the opening tag into the assistant turn, so the reply comes back already
            // inside the thinking block and the first tag in it is the closing one. Everything
            // up to that point is deliberation, however much it reads like an answer.
            if (isCloser)
            {
                Append(reasoning, text.AsSpan(position, at - position));
                position = at + close.Length;
                continue;
            }

            answer.Append(text, position, at - position);
            var bodyStart = at + open.Length;
            var end = text.IndexOf(close, bodyStart, StringComparison.OrdinalIgnoreCase);

            if (end < 0)
            {
                // Still streaming, or the model never closed the tag. Either way the rest is
                // thinking: showing it as the answer is the exact bug this class exists to fix.
                Append(reasoning, text.AsSpan(bodyStart));
                break;
            }

            Append(reasoning, text.AsSpan(bodyStart, end - bodyStart));
            position = end + close.Length;
        }

        return (reasoning.ToString().Trim(), answer.ToString().Trim());
    }

    /// <summary>
    /// Ближайший маркер от <paramref name="from"/> — открывающий или закрывающий, смотря какой
    /// встретился раньше.
    /// </summary>
    /// <remarks>
    /// Один проход по остатку строки. Прежде здесь стояли два метода, и каждый гонял по пять
    /// <c>IndexOf</c> с <c>OrdinalIgnoreCase</c>: десять регистронезависимых проходов по всему
    /// хвосту за итерацию. На стриминге разбор повторяется на каждый чанк, и эта десятка
    /// превращала длинный ответ в квадрат.
    /// </remarks>
    private static (string Open, string Close, int At, bool IsCloser) NextMarker(string text, int from)
    {
        var at = from;
        while (at < text.Length)
        {
            at = text.IndexOfAny(MarkerStarts, at);
            if (at < 0)
            {
                break;
            }

            var tail = text.AsSpan(at);
            foreach (var (open, close) in Markers)
            {
                if (tail.StartsWith(open, StringComparison.OrdinalIgnoreCase))
                {
                    return (open, close, at, false);
                }

                if (tail.StartsWith(close, StringComparison.OrdinalIgnoreCase))
                {
                    return (open, close, at, true);
                }
            }

            at++;
        }

        return ("", "", -1, false);
    }

    private static void Append(StringBuilder target, ReadOnlySpan<char> piece)
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
