using System.Text;

namespace Amarin.Core;

/// <summary>
/// Отделяет размышление модели от ответа, когда оба приходят одним потоком.
/// </summary>
/// <remarks>
/// <para>
/// Аккуратные рассуждающие модели шлют размышление в <c>reasoning_content</c>, и в ленту оно не
/// попадает. Несколько семейств — прежде всего GLM, Kimi со своими скобками — заворачивают его в
/// теги внутри <c>content</c>, а <c>strip_thinking_response</c> для них Venice не учитывает (см.
/// <see cref="ChatMessageDelta.ReasoningContent"/>). Разметка тогда идёт прямо в Markdown, где HTML
/// выключен, и <c>&lt;think&gt;</c> печатается как текст.
/// </para>
/// <para>
/// Разбор идёт по накопленному тексту после каждого кусочка и должен переживать ещё открытый тег:
/// всё после незакрытого открывающего считается размышлением — иначе ответ мелькал бы и исчезал
/// с приходом закрывающего тега.
/// </para>
/// <para>
/// Труднее обратный случай, и GLM делает именно так: шаблон чата заранее ставит открывающий тег в
/// ход ассистента, и ответ приходит уже внутри блока размышления, с одним закрывающим тегом. Ничто
/// ни во что не завёрнуто, и разбор, ищущий пару, увидит обычный текст. Поэтому закрывающий тег без
/// пары читается как конец блока, начатого в начале сообщения.
/// </para>
/// </remarks>
internal static class ReasoningSplit
{
    /// <summary>
    /// Пары открывающих и закрывающих тегов, длинные первыми, — чтобы <c>&lt;thinking&gt;</c> не
    /// читался как <c>&lt;think&gt;</c> с лишним текстом. Регистр не важен.
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
    /// Возвращает размышление и ответ. Текст без меток целиком становится ответом — это подавляющее
    /// большинство случаев, и стоит он один проход.
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

            // Закрывающий тег без открывающего: шаблон чата моделей класса GLM ставит открывающий
            // заранее, и первый тег в ответе — закрывающий. Всё до него — размышление, как бы оно
            // ни походило на ответ.
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
                // Ещё идёт поток или модель тег так и не закрыла. В обоих случаях остаток —
                // размышление: показать его ответом — ровно та ошибка, ради которой класс и есть.
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
