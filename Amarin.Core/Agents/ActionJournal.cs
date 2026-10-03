namespace Amarin.Core;

/// <summary>Одно действие модели на этом компьютере.</summary>
/// <param name="ChatId">Чат, в котором это было, — журнал умеет в него перейти.</param>
/// <param name="ChatTitle">Заголовок как на диске: у безымянного чата это непереведённый маркер,
/// показать который умеет только интерфейс.</param>
/// <param name="AgentName">Имя вложенного агента, который это сделал; null — сам чат.</param>
public sealed record JournalEntry(
    string ChatId,
    string ChatTitle,
    DateTime StartedAt,
    TimeSpan Duration,
    string ToolName,
    string ArgumentsJson,
    string ResultPreview,
    string ResultText,
    bool Success,
    ToolCallStatus Status,
    string? AgentName)
{
    /// <summary>True — время взято у сообщения, а не у самого вызова.</summary>
    public bool TimeIsApproximate { get; init; }

    /// <summary>
    /// Вызов из спрятанного варианта ответа: на экране чата его сейчас не видно, но на машине он
    /// был — поэтому в журнале он есть, с пометкой.
    /// </summary>
    public bool InHiddenVariant { get; init; }
}

/// <summary>
/// Все вызовы инструментов, какие модель делала, — плоским списком из хранящих их чатов.
/// </summary>
/// <remarks>
/// Своего файла у этого журнала нет намеренно: вызовы уже лежат в переписках, а вторая копия —
/// ещё одна вещь, которую надо держать в согласии, и ещё одно место, откуда утекал бы удалённый
/// чат. (Журнал аудита — другое: он пишет записи в систему и отказы и удаление чатов переживает.)
/// Плата — «все чаты» значит прочесть каждый файл чата, поэтому <see cref="Collect"/> зовут вне
/// потока интерфейса.
/// </remarks>
internal static class ActionJournal
{
    /// <summary>Actions of one loaded session, newest first.</summary>
    public static IReadOnlyList<JournalEntry> FromSession(ChatSession? session)
    {
        var entries = new List<JournalEntry>();
        if (session is not null)
        {
            Collect(session, entries);
        }

        return Sort(entries);
    }

    /// <summary>
    /// Действия всех чатов на диске, новые первыми. Нечитаемые чаты пропускаются, а не валят всё:
    /// один битый файл не должен прятать остальную историю.
    /// </summary>
    public static IReadOnlyList<JournalEntry> Collect(ChatStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var entries = new List<JournalEntry>();
        foreach (var item in store.List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (store.TryLoad(item.Id) is { } session)
            {
                Collect(session, entries);
            }
        }

        return Sort(entries);
    }

    private static List<JournalEntry> Sort(List<JournalEntry> entries)
    {
        entries.Sort((left, right) => right.StartedAt.CompareTo(left.StartedAt));
        return entries;
    }

    private static void Collect(ChatSession session, List<JournalEntry> entries)
    {
        var title = session.Title;

        // Перегенерация прячет прежний ответ в вариант, а не стирает: то, что он сделал на
        // машине, из журнала пропасть не должно.
        foreach (var (message, hidden) in ChatBranches.AllWithVisibility(session))
        {
            foreach (var round in message.ToolRounds)
            {
                foreach (var call in round.Calls)
                {
                    Collect(session.Id, title, message, call, agentName: null, hidden, entries);
                }
            }
        }
    }

    private static void Collect(
        string chatId,
        string chatTitle,
        ChatDisplayMessage message,
        ToolCallRecord call,
        string? agentName,
        bool hidden,
        List<JournalEntry> entries)
    {
        // У старых вызовов своего времени нет. У их сообщения есть, и оно верно с точностью до
        // хода — для порядка этого хватает.
        var approximate = call.StartedAt == default;
        entries.Add(new JournalEntry(
            chatId,
            chatTitle,
            approximate ? message.CreatedAt : call.StartedAt,
            call.Duration,
            call.Name,
            call.ArgumentsJson,
            call.ResultPreview,
            string.IsNullOrWhiteSpace(call.ResultText) ? call.ResultPreview : call.ResultText,
            call.Success,
            call.Status,
            agentName)
        {
            TimeIsApproximate = approximate,
            InHiddenVariant = hidden
        });

        if (call.NestedAgent is not { } agent)
        {
            return;
        }

        // Машину трогали вызовы самого агента, а init_agent, который его запустил, только
        // передал задачу. В списке оба, вложенные — с пометкой.
        var name = string.IsNullOrWhiteSpace(agent.DisplayName) ? agent.ModelId : agent.DisplayName;
        foreach (var round in agent.ToolRounds)
        {
            foreach (var nested in round.Calls)
            {
                Collect(chatId, chatTitle, message, nested, name, hidden, entries);
            }
        }
    }
}
