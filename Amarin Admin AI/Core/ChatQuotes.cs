using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Amarin.Core;

/// <summary>Откуда взята цитата — относительно сообщения, которое на неё отвечает.</summary>
public enum QuoteSourceKind
{
    /// <summary>Последний законченный ответ перед этим сообщением.</summary>
    Last,

    /// <summary>Законченный ответ, но не последний.</summary>
    Earlier,

    /// <summary>Ответ оборван или упал: в истории, которую видит модель, его нет.</summary>
    Interrupted,

    /// <summary>Ответа больше нет в переписке.</summary>
    Gone
}

/// <summary>
/// Цитаты из прежних ответов: как их прикреплять, как на них ссылаться и в каком виде они
/// уходят модели.
/// </summary>
/// <remarks>
/// <para>
/// Без WPF, как весь <c>Core</c>: правила проверяются тестами напрямую, а окно только рисует.
/// </para>
/// <para>
/// Блок для модели написан по-английски, хотя обвязка вложений русская: системные промпты
/// английские, а русский служебный текст внутри сообщения англоязычного человека сбивал бы
/// модель на русский ответ. Блок объясняет себя сам — системный промпт не трогается, поэтому
/// ни архивировать его прежнюю редакцию, ни платить за правило в чатах без цитат не нужно.
/// </para>
/// </remarks>
public static partial class ChatQuotes
{
    /// <summary>Сколько цитат можно прикрепить к одному сообщению.</summary>
    /// <remarks>
    /// Больше пяти ссылок человек в одном сообщении не удерживает, а модель начинает отвечать
    /// на перечень, а не на вопрос.
    /// </remarks>
    public const int MaxQuotes = 5;

    /// <summary>Длиннее этого фрагмент обрезается серединой.</summary>
    /// <remarks>
    /// Исходный ответ и так лежит в контексте дословно: цитате нужно лишь показать, о каком месте
    /// речь, а не повторить его целиком за деньги каждого следующего запроса.
    /// </remarks>
    public const int MaxFragmentChars = 1500;

    private const int ClipHead = 1100;
    private const int ClipTail = 400;

    /// <summary>Короче этого однострочный фрагмент получает окружение.</summary>
    /// <remarks>
    /// Слово «PowerShell» встречается в ответе десять раз, и без строки вокруг модель гадала бы,
    /// о котором из них спрашивают.
    /// </remarks>
    public const int ShortFragment = 60;

    private const int ContextWindow = 200;
    private const int AnchorMax = 60;

    /// <summary>
    /// Шапка блока. Одна на любое число цитат: модели не приходится различать два формата.
    /// </summary>
    internal const string Header =
        "[The user is replying to quotes from your earlier replies. \"@N\" in their message refers to quote N.]";

    internal const string MessageMarker = "[Their message]";

    /// <summary>
    /// Приводит выделенный текст к виду, в котором его хранят и отправляют.
    /// </summary>
    /// <remarks>
    /// Выделение в документе приносит с собой служебные символы разметки: неразрывные пробелы,
    /// символы нулевой ширины, хвостовые пробелы строк, пустые абзацы подряд. Модели они не
    /// нужны, а сравнение «эта цитата уже прикреплена» без них надёжнее.
    /// </remarks>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            switch (ch)
            {
                case '​' or '‌' or '‍' or '⁠' or '﻿':
                    continue;
                case ' ':
                    sb.Append(' ');
                    continue;
                default:
                    sb.Append(ch);
                    continue;
            }
        }

        var lines = sb.ToString()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var result = new StringBuilder(sb.Length);
        var blank = 0;
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
            {
                blank++;
                continue;
            }

            if (result.Length > 0)
            {
                // Любая пачка пустых строк становится одной: абзац отделён, но не разорван.
                result.Append(blank > 0 ? "\n\n" : "\n");
            }

            blank = 0;
            result.Append(trimmed);
        }

        return result.ToString().Trim();
    }

    /// <summary>Обрезает слишком длинный фрагмент серединой, сохраняя начало и конец.</summary>
    public static string Clip(string text) =>
        text.Length <= MaxFragmentChars ? text : TextClip.Clip(text, ClipHead, ClipTail);

    /// <summary>
    /// Строка вокруг короткого фрагмента или <c>null</c>, если фрагмент однозначен и без неё.
    /// </summary>
    public static string? ContextAround(string? paragraph, string fragment)
    {
        if (string.IsNullOrWhiteSpace(paragraph) ||
            string.IsNullOrWhiteSpace(fragment) ||
            fragment.Length > ShortFragment ||
            fragment.Contains('\n'))
        {
            return null;
        }

        var line = Normalize(paragraph).Replace('\n', ' ');
        var at = line.IndexOf(fragment, StringComparison.Ordinal);
        if (at < 0 || line.Length < fragment.Length + 20)
        {
            return null;
        }

        if (line.Length <= ContextWindow)
        {
            return line;
        }

        var start = Math.Max(0, at - (ContextWindow - fragment.Length) / 2);
        var end = Math.Min(line.Length, start + ContextWindow);
        start = Math.Max(0, end - ContextWindow);

        var window = line[start..end].Trim();
        return (start > 0 ? "…" : "") + window + (end < line.Length ? "…" : "");
    }

    /// <summary>
    /// Начало ответа, по которому модель узнаёт его в своей истории.
    /// </summary>
    /// <remarks>
    /// Берётся сырой текст ответа, с разметкой как есть: именно он лежит в истории дословно
    /// (<c>FinishAssistant</c>), и поиск такой строки ничего модели не стоит. Порядковый номер
    /// здесь не годился бы — см. <see cref="MessageQuote"/>.
    /// </remarks>
    public static string Anchor(string? sourceText)
    {
        var line = (sourceText ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(item => item.Trim())
            .FirstOrDefault(item => item.Length > 0) ?? "";

        if (line.Length <= AnchorMax)
        {
            return line;
        }

        var cut = line.LastIndexOf(' ', AnchorMax);
        return (cut >= AnchorMax / 2 ? line[..cut] : line[..AnchorMax]).TrimEnd() + "…";
    }

    /// <summary>Номер для следующей цитаты: на единицу больше самого большого из прикреплённых.</summary>
    public static int NextNumber(IReadOnlyList<MessageQuote> pending) =>
        pending.Count == 0 ? 1 : pending.Max(quote => quote.Number) + 1;

    /// <summary>Тот же фрагмент того же ответа уже прикреплён.</summary>
    public static bool IsDuplicate(IReadOnlyList<MessageQuote> pending, string sourceId, string text) =>
        pending.Any(quote =>
            string.Equals(quote.SourceMessageId, sourceId, StringComparison.Ordinal) &&
            string.Equals(quote.Text, text, StringComparison.Ordinal));

    /// <summary>
    /// Где в тексте стоят ссылки <c>@N</c>.
    /// </summary>
    /// <remarks>
    /// Ссылка не может быть частью слова, адреса или пути: <c>user@1host</c>, <c>a@1</c>,
    /// <c>\\srv\@1</c> — не ссылки. Ноль и числа больше двух знаков — тоже.
    /// </remarks>
    public static IReadOnlyList<(int Start, int Length, int Number)> References(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('@'))
        {
            return [];
        }

        var found = new List<(int, int, int)>();
        foreach (Match match in ReferencePattern().Matches(text))
        {
            found.Add((match.Index, match.Length,
                int.Parse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture)));
        }

        return found;
    }

    /// <summary>
    /// Набирается ли прямо у каретки ссылка: <c>@</c> и, возможно, цифры после неё.
    /// </summary>
    /// <param name="start">Где стоит <c>@</c>.</param>
    /// <param name="digits">Уже набранные цифры — по ним сужается подсказка.</param>
    public static bool TryGetTypingReference(string? text, int caret, out int start, out string digits)
    {
        start = -1;
        digits = "";
        if (string.IsNullOrEmpty(text) || caret <= 0 || caret > text.Length)
        {
            return false;
        }

        var at = caret - 1;
        while (at >= 0 && caret - at <= 2 && char.IsAsciiDigit(text[at]))
        {
            at--;
        }

        if (at < 0 || text[at] != '@')
        {
            return false;
        }

        if (at > 0 && !OpensReference(text[at - 1]))
        {
            return false;
        }

        // Каретка посреди слова («@1abc») — это уже не ссылка, а чужое слово.
        if (caret < text.Length && (char.IsLetterOrDigit(text[caret]) || text[caret] == '_'))
        {
            return false;
        }

        start = at;
        digits = text[(at + 1)..caret];
        return true;
    }

    private static bool OpensReference(char previous) =>
        char.IsWhiteSpace(previous) || previous is '(' or '[' or '{' or '"' or '\'' or '«' or '„' or '“';

    /// <summary>
    /// Кем приходится источник цитаты сообщению, стоящему в переписке под номером
    /// <paramref name="quotingIndex"/>.
    /// </summary>
    /// <param name="quotingIndex">
    /// Индекс отвечающего сообщения в <paramref name="messages"/>. Для ещё не отправленного —
    /// длина списка: оно встанет в конец.
    /// </param>
    public static QuoteSourceKind Classify(
        IReadOnlyList<ChatDisplayMessage> messages,
        int quotingIndex,
        string sourceId,
        out ChatDisplayMessage? source)
    {
        source = null;
        var limit = Math.Clamp(quotingIndex, 0, messages.Count);
        ChatDisplayMessage? lastAssistant = null;
        for (var i = 0; i < limit; i++)
        {
            var message = messages[i];
            if (!IsAssistant(message))
            {
                continue;
            }

            lastAssistant = message;
            if (string.Equals(message.Id, sourceId, StringComparison.Ordinal))
            {
                source = message;
            }
        }

        if (source is null)
        {
            return QuoteSourceKind.Gone;
        }

        if (source.Status is AssistantStatus.Cancelled or AssistantStatus.Error)
        {
            return QuoteSourceKind.Interrupted;
        }

        return ReferenceEquals(source, lastAssistant) && source.Status == AssistantStatus.Complete
            ? QuoteSourceKind.Last
            : QuoteSourceKind.Earlier;
    }

    /// <summary>
    /// Текст сообщения человека в том виде, в каком его читает модель: блок цитат, затем само
    /// сообщение.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Цитаты стоят до сообщения, чтобы к моменту, когда модель встретит «@2», она уже знала,
    /// что это. Каждая строка фрагмента начинается с «&gt; »: так граница цитаты однозначна
    /// даже у кода с собственными ``` внутри.
    /// </para>
    /// <para>
    /// Без цитат возвращается <paramref name="body"/> как есть — байт в байт, чтобы чаты без
    /// этой функции уходили модели ровно так же, как до неё.
    /// </para>
    /// </remarks>
    public static string Wrap(
        string body,
        IReadOnlyList<MessageQuote>? quotes,
        IReadOnlyList<ChatDisplayMessage> messages,
        int quotingIndex)
    {
        if (quotes is not { Count: > 0 })
        {
            return body;
        }

        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        foreach (var quote in quotes)
        {
            var kind = Classify(messages, quotingIndex, quote.SourceMessageId, out var source);
            sb.Append('@').Append(quote.Number.ToString(CultureInfo.InvariantCulture))
                .Append(", from ")
                .Append(DescribeSource(kind, source));

            if (!string.IsNullOrWhiteSpace(quote.Context))
            {
                sb.Append(" (in the passage \"").Append(quote.Context).Append("\")");
            }

            sb.Append(":\n");
            foreach (var line in quote.Text.Split('\n'))
            {
                sb.Append(line.Length == 0 ? ">" : "> " + line).Append('\n');
            }
        }

        sb.Append(MessageMarker).Append('\n').Append(body);
        return sb.ToString();
    }

    private static string DescribeSource(QuoteSourceKind kind, ChatDisplayMessage? source)
    {
        switch (kind)
        {
            case QuoteSourceKind.Last:
                return "your last reply";
            case QuoteSourceKind.Interrupted:
                return "an interrupted reply of yours (not in the history above)";
            case QuoteSourceKind.Gone:
                return "a reply of yours that has since been deleted";
        }

        var anchor = Anchor(source?.Text);
        return anchor.Length == 0
            ? "one of your earlier replies"
            : "your reply that starts \"" + anchor + "\"";
    }

    private static bool IsAssistant(ChatDisplayMessage message) =>
        message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?<![\p{L}\p{N}_@./\\])@([1-9][0-9]?)(?![\p{L}\p{N}_])")]
    private static partial Regex ReferencePattern();
}
