namespace Amarin.Core;

/// <summary>Одно совпадение поиска в чате.</summary>
/// <param name="MessageIndex">Номер сообщения в ленте.</param>
/// <param name="Occurrence">Какое по счёту совпадение внутри этого сообщения (с нуля).</param>
/// <param name="InTools">Совпадение в блоке инструментов, а не в тексте.</param>
public sealed record ChatFindHit(int MessageIndex, string MessageId, int Occurrence, bool InTools);

/// <summary>
/// Поиск по открытому чату (D1): текст сообщений и то, что показывают блоки инструментов.
/// </summary>
/// <remarks>
/// Чистая функция над сообщениями, без ленты: лента строится лениво, и на момент поиска
/// большей части сообщений на экране ещё нет. Совпадения в тексте считаются по исходному тексту,
/// а подсвечиваются в отрисованном — где разметка их не разбила, это одно и то же место.
/// </remarks>
public static class ChatFind
{
    /// <summary>Дальше поиск не идёт: столько совпадений человеку всё равно не пролистать.</summary>
    public const int Limit = 2000;

    public static IReadOnlyList<ChatFindHit> Find(IReadOnlyList<ChatDisplayMessage> messages, string? query)
    {
        var needle = query?.Trim() ?? "";
        var hits = new List<ChatFindHit>();
        if (needle.Length == 0)
        {
            return hits;
        }

        for (var index = 0; index < messages.Count && hits.Count < Limit; index++)
        {
            var message = messages[index];
            var count = Count(message.Text, needle);
            for (var occurrence = 0; occurrence < count && hits.Count < Limit; occurrence++)
            {
                hits.Add(new ChatFindHit(index, message.Id, occurrence, InTools: false));
            }

            // Блок инструментов — одно совпадение на сообщение: внутри него подсвечивать нечего,
            // его раскрывают, и человек видит найденное в строках вызовов.
            if (message.ToolRounds.Count > 0 && ToolsContain(message, needle))
            {
                hits.Add(new ChatFindHit(index, message.Id, 0, InTools: true));
            }
        }

        return hits;
    }

    internal static int Count(string? text, string needle)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        var at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.CurrentCultureIgnoreCase)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }

    private static bool ToolsContain(ChatDisplayMessage message, string needle) =>
        message.ToolRounds.Any(round =>
            Contains(round.ModelNote, needle) ||
            round.Calls.Any(call =>
                Contains(call.Name, needle) ||
                Contains(call.ArgumentsJson, needle) ||
                Contains(call.ResultPreview, needle) ||
                (call.NestedAgent is { } agent &&
                 (Contains(agent.ReportText, needle) ||
                  agent.ToolRounds.Any(nested => nested.Calls.Any(inner =>
                      Contains(inner.ArgumentsJson, needle) || Contains(inner.ResultPreview, needle)))))));

    private static bool Contains(string? text, string needle) =>
        !string.IsNullOrEmpty(text) && text.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
}
