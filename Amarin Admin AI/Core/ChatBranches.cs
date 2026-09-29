namespace Amarin.Core;

/// <summary>
/// Варианты ответа: перегенерация и правка вопроса больше не стирают продолжение, а прячут его.
/// </summary>
/// <remarks>
/// <para>
/// Модель — «тайник». Показанный вариант всегда лежит в плоских <see cref="ChatSession.Messages"/>
/// и <see cref="ChatSession.ApiMessages"/>; остальные — на «якоре», первом сообщении показанного
/// варианта (<see cref="ChatDisplayMessage.Variants"/>). Поэтому модель видит только показанную
/// ветку, новое сообщение продолжает её, а всё, что читает переписку, — сборка истории, лента,
/// сводка, заголовок — работает как раньше. Спрятанный вариант может сам нести варианты
/// глубже: они лежат внутри него и возвращаются вместе с ним.
/// </para>
/// <para>
/// Группу держит только показанный якорь. При каждой операции группа переезжает на новый якорь,
/// а у прежнего обнуляется — иначе, спрятав прежний якорь вместе с его группой, список
/// вложился бы сам в себя.
/// </para>
/// <para>
/// Всё — под <see cref="ChatSession.Gate"/>: тем же замком фоновая запись сериализует чат, и
/// снимок посреди перестановки дал бы файл без старого ответа и без нового.
/// </para>
/// </remarks>
internal static class ChatBranches
{
    /// <summary>
    /// Прячет продолжение с места <paramref name="index"/> и ставит туда новый якорь.
    /// </summary>
    /// <param name="anchor">Новое сообщение: пустой ответ при перегенерации, вопрос при правке.</param>
    /// <param name="apiForAnchor">
    /// Запись истории модели под новый вопрос; null — у ответа её нет. Зовётся, когда якорь уже
    /// стоит в ленте: блок цитат называет источник по месту вопроса в переписке.
    /// </param>
    public static void Fork(
        ChatSession session,
        int index,
        ChatDisplayMessage anchor,
        Func<ChatMessage>? apiForAnchor = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(anchor);

        lock (session.Gate)
        {
            if (index < 0 || index >= session.Messages.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var cut = ChatSessionEdit.ApiCut(session, index);
            var old = session.Messages[index];
            var others = old.Variants ?? [];
            var position = old.Variants is null ? 0 : Math.Clamp(old.VariantIndex, 0, others.Count);
            old.Variants = null;
            old.VariantIndex = 0;

            var stashed = Stash(session, index, cut);
            others.Insert(position, stashed);

            anchor.Variants = others;
            anchor.VariantIndex = others.Count;

            session.Messages.Add(anchor);
            if (apiForAnchor is not null)
            {
                session.ApiMessages.Add(apiForAnchor());
            }

            session.SummaryStale = true;
            session.UpdatedAt = DateTime.Now;
        }
    }

    /// <summary>
    /// Показывает вариант <paramref name="target"/> (место среди всех) группы, чей якорь стоит на
    /// месте <paramref name="index"/>. Возвращает новый якорь или null, если переключать нечего.
    /// </summary>
    public static ChatDisplayMessage? Switch(ChatSession session, int index, int target)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (session.Gate)
        {
            if (index < 0 || index >= session.Messages.Count ||
                session.Messages[index] is not { Variants: { Count: > 0 } others } anchor)
            {
                return null;
            }

            var current = Math.Clamp(anchor.VariantIndex, 0, others.Count);
            if (target == current || target < 0 || target > others.Count)
            {
                return null;
            }

            var cut = ChatSessionEdit.ApiCut(session, index);
            anchor.Variants = null;
            anchor.VariantIndex = 0;

            var full = new List<ChatBranch>(others);
            full.Insert(current, Stash(session, index, cut));

            var chosen = full[target];
            full.RemoveAt(target);
            return Restore(session, cut, chosen, full, target);
        }
    }

    /// <summary>
    /// Удаляет показанный вариант группы на месте <paramref name="index"/> и показывает соседний:
    /// предыдущий, а если его нет — следующий. Null — у сообщения нет других вариантов.
    /// </summary>
    public static ChatDisplayMessage? DeleteActive(ChatSession session, int index)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (session.Gate)
        {
            if (index < 0 || index >= session.Messages.Count ||
                session.Messages[index] is not { Variants: { Count: > 0 } others } anchor)
            {
                return null;
            }

            var current = Math.Clamp(anchor.VariantIndex, 0, others.Count);
            var cut = ChatSessionEdit.ApiCut(session, index);
            anchor.Variants = null;
            anchor.VariantIndex = 0;

            var removed = Stash(session, index, cut);
            var target = current > 0 ? current - 1 : 0;
            var chosen = others[target];
            var rest = new List<ChatBranch>(others);
            rest.RemoveAt(target);

            var shown = Restore(session, cut, chosen, rest, target);
            MoveTitleCost(session, removed);
            return shown;
        }
    }

    /// <summary>Сколько ходов человека в показанном варианте, начиная с якоря на месте <paramref name="index"/>.</summary>
    public static int TurnsFrom(ChatSession session, int index) =>
        session.Messages.Skip(index).Count(message => message.Role.Equals("user", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Ближайший к сообщению якорь с вариантами: само сообщение, а если нет — вопрос его хода.
    /// </summary>
    /// <remarks>
    /// Сначала самая внутренняя группа: у ответа — перегенерации, у вопроса его хода — правки.
    /// </remarks>
    public static int FindGroupFor(ChatSession session, ChatDisplayMessage message)
    {
        var index = session.Messages.IndexOf(message);
        if (index < 0)
        {
            return -1;
        }

        if (message.Variants is { Count: > 0 })
        {
            return index;
        }

        for (var i = index - 1; i >= 0; i--)
        {
            if (session.Messages[i].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                return session.Messages[i].Variants is { Count: > 0 } ? i : -1;
            }
        }

        return -1;
    }

    /// <summary>Все сообщения переписки — показанные и спрятанные во всех вариантах, вглубь.</summary>
    public static IEnumerable<ChatDisplayMessage> AllMessages(ChatSession session) =>
        Walk(session.Messages, hidden: false).Select(item => item.Message);

    /// <summary>То же, с пометкой, лежит ли сообщение в спрятанном варианте.</summary>
    public static IEnumerable<(ChatDisplayMessage Message, bool Hidden)> AllWithVisibility(ChatSession session) =>
        Walk(session.Messages, hidden: false);

    /// <summary>Сообщение по идентификатору — где бы оно ни лежало.</summary>
    /// <remarks>
    /// Цена сводки и заголовка приезжают отдельными задачами: если человек успел переключить
    /// вариант, их сообщение уже спрятано, и поиск только по ленте молча потерял бы деньги.
    /// </remarks>
    public static ChatDisplayMessage? FindMessage(ChatSession session, string? id) =>
        string.IsNullOrEmpty(id)
            ? null
            : AllMessages(session).FirstOrDefault(message => string.Equals(message.Id, id, StringComparison.Ordinal));

    private static IEnumerable<(ChatDisplayMessage Message, bool Hidden)> Walk(
        IEnumerable<ChatDisplayMessage> messages,
        bool hidden)
    {
        foreach (var message in messages)
        {
            yield return (message, hidden);
            if (message.Variants is not { } variants)
            {
                continue;
            }

            foreach (var branch in variants)
            {
                foreach (var item in Walk(branch.Messages, hidden: true))
                {
                    yield return item;
                }
            }
        }
    }

    /// <summary>Вырезает продолжение с места <paramref name="index"/> в спрятанный вариант.</summary>
    private static ChatBranch Stash(ChatSession session, int index, int cut)
    {
        cut = Math.Clamp(cut, 0, session.ApiMessages.Count);
        var branch = new ChatBranch
        {
            Messages = session.Messages.GetRange(index, session.Messages.Count - index),
            ApiMessages = session.ApiMessages.GetRange(cut, session.ApiMessages.Count - cut)
        };

        // Замер контекста за разрезом относится к уходящему варианту: забираем его с собой.
        // До разреза — к общему началу, и он остаётся на чате.
        if (session.LastPromptTokens > 0 && session.LastPromptTokensApiIndex > cut)
        {
            branch.LastPromptTokens = session.LastPromptTokens;
            branch.LastPromptTokensApiOffset = session.LastPromptTokensApiIndex - cut;
            session.LastPromptTokens = 0;
            session.LastPromptTokensApiIndex = 0;
        }

        session.Messages.RemoveRange(index, session.Messages.Count - index);
        session.ApiMessages.RemoveRange(cut, session.ApiMessages.Count - cut);
        return branch;
    }

    private static ChatDisplayMessage? Restore(
        ChatSession session,
        int cut,
        ChatBranch chosen,
        List<ChatBranch> others,
        int position)
    {
        session.Messages.AddRange(chosen.Messages);
        session.ApiMessages.AddRange(chosen.ApiMessages);
        if (chosen.LastPromptTokens > 0)
        {
            session.LastPromptTokens = chosen.LastPromptTokens;
            session.LastPromptTokensApiIndex = cut + chosen.LastPromptTokensApiOffset;
        }

        var anchor = chosen.Messages.Count > 0 ? chosen.Messages[0] : null;
        if (anchor is not null)
        {
            anchor.Variants = others.Count > 0 ? others : null;
            anchor.VariantIndex = others.Count > 0 ? position : 0;
        }

        // Пока вариант лежал спрятанным, в общем начале могли удалить ход, на который ссылались
        // его цитаты: блок цитат для модели пересобирается.
        ChatSessionEdit.RewrapQuotes(session);
        session.SummaryStale = true;
        session.UpdatedAt = DateTime.Now;
        return anchor;
    }

    /// <summary>
    /// Цена заголовка живёт на одном ответе. Удалили вариант, который её нёс, — она переезжает на
    /// первый ответ показанного, иначе сумма чата её потеряла бы.
    /// </summary>
    private static void MoveTitleCost(ChatSession session, ChatBranch removed)
    {
        var carrier = Walk(removed.Messages, hidden: true)
            .Select(item => item.Message)
            .FirstOrDefault(message => message.TitleCost is { HasData: true });
        if (carrier?.TitleCost is not { } cost)
        {
            return;
        }

        if (AllMessages(session).Any(message => message.TitleCost is { HasData: true }))
        {
            return;
        }

        var first = session.Messages.FirstOrDefault(
            message => message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase));
        if (first is null)
        {
            return;
        }

        first.TitleCost = cost;
        if (first.Cost is not null)
        {
            first.Cost = first.Cost.Add(cost);
        }
    }
}
