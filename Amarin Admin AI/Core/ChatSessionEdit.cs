namespace Amarin.Core;

internal static class ChatSessionEdit
{
    public static bool TruncateFromMessage(ChatSession session, string messageId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var index = session.Messages.FindIndex(item => item.Id == messageId);
        if (index < 0)
        {
            return false;
        }

        session.Messages.RemoveRange(index, session.Messages.Count - index);
        TruncateApiToMatchDisplay(session);
        session.UpdatedAt = DateTime.Now;
        return true;
    }

    /// <summary>
    /// Removes one turn — the question and the answer it produced — and leaves everything that
    /// came after it in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A turn cannot be cut out of <see cref="ChatSession.ApiMessages"/> message by message: one
    /// visible answer expands into an interleaved run of <c>assistant(tool_calls)</c> and
    /// <c>tool</c> entries, and breaking a <c>tool_call_id</c> pairing makes the API reject the
    /// whole history. It can be cut out turn by turn, though: the list carries no system prompt
    /// (<c>ChatEngine.BuildApiMessages</c> prepends that at send time), so every <c>user</c>
    /// entry opens a turn and the range up to the next one is exactly one self-contained turn.
    /// </para>
    /// <para>
    /// If the two lists have drifted out of step — a compressed history, a hand-edited file —
    /// there is no safe mapping, and the old behaviour applies instead: keep the prefix and drop
    /// the rest. Losing the tail is bad; sending a history the API refuses is worse.
    /// </para>
    /// </remarks>
    public static bool DeleteTurn(ChatSession session, string messageId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var index = session.Messages.FindIndex(item => item.Id == messageId);
        if (index < 0)
        {
            return false;
        }

        var start = index;
        while (start > 0 && !session.Messages[start].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            start--;
        }

        var end = start + 1;
        while (end < session.Messages.Count &&
               !session.Messages[end].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            end++;
        }

        var turnOrdinal = 0;
        for (var i = 0; i < start; i++)
        {
            if (session.Messages[i].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                turnOrdinal++;
            }
        }

        var turnsBefore = UserCount(session);
        var starts = ApiTurnStarts(session);
        session.Messages.RemoveRange(start, end - start);

        if (starts.Count != turnsBefore || turnOrdinal >= starts.Count)
        {
            TruncateApiToMatchDisplay(session);
        }
        else
        {
            var from = starts[turnOrdinal];
            var to = turnOrdinal + 1 < starts.Count ? starts[turnOrdinal + 1] : session.ApiMessages.Count;
            session.ApiMessages.RemoveRange(from, to - from);
            RewrapQuotes(session);
        }

        session.UpdatedAt = DateTime.Now;
        return true;
    }

    /// <summary>
    /// Пересобирает сообщения, цитировавшие ответ, которого больше нет.
    /// </summary>
    /// <remarks>
    /// Блок цитат называет источник («твой последний ответ», «ответ, который начинается с…»), а
    /// после удаления хода такого ответа в истории уже нет — и модель искала бы его впустую.
    /// Пересобираются только такие сообщения: у остальных содержимое не меняется ни на байт.
    /// Вложения при пересборке не теряются — в хранимой истории они лежат целиком, выбрасывает
    /// их <see cref="ApiContextLimiter"/> лишь из отправляемой копии.
    /// </remarks>
    internal static void RewrapQuotes(ChatSession session)
    {
        var starts = ApiTurnStarts(session);
        var ordinal = 0;
        for (var i = 0; i < session.Messages.Count; i++)
        {
            var message = session.Messages[i];
            if (!message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var turn = ordinal++;
            if (message.Quotes.Count == 0 || turn >= starts.Count)
            {
                continue;
            }

            var orphaned = message.Quotes.Any(quote =>
                ChatQuotes.Classify(session.Messages, i, quote.SourceMessageId, out _) == QuoteSourceKind.Gone);
            if (orphaned)
            {
                session.ApiMessages[starts[turn]] = new ChatMessage
                {
                    Role = "user",
                    Content = ChatContent.ForUser(message, session.Messages, i)
                };
            }
        }
    }

    /// <summary>Index of every turn start in the wire history — one per message of the person.</summary>
    /// <remarks>
    /// Картинки инструментов тоже лежат там с ролью <c>user</c>, но ходом не являются: считать
    /// их значило бы резать историю не там — после скриншота в первом ходе перегенерация
    /// второго теряла и итог первого ответа, и сам второй вопрос.
    /// </remarks>
    private static List<int> ApiTurnStarts(ChatSession session)
    {
        var starts = new List<int>();
        for (var i = 0; i < session.ApiMessages.Count; i++)
        {
            if (ChatContent.IsTurnStart(session.ApiMessages[i]))
            {
                starts.Add(i);
            }
        }

        return starts;
    }

    /// <summary>
    /// Сколько записей истории модели относится к первым <paramref name="displayCount"/>
    /// сообщениям ленты — то есть где история режется, когда лента режется на этом месте.
    /// </summary>
    /// <remarks>
    /// Точный ответ даёт счёт ходов: сообщение человека в ленте — одно начало хода в истории.
    /// Если счёт не сходится (история сжата или файл правили руками), берётся прежнее
    /// приближение по закрытым ответам: лучше обрезать лишнее, чем отправить историю, которую
    /// API отвергнет.
    /// </remarks>
    internal static int ApiCut(ChatSession session, int displayCount)
    {
        ArgumentNullException.ThrowIfNull(session);
        var count = Math.Clamp(displayCount, 0, session.Messages.Count);
        var usersBefore = 0;
        for (var i = 0; i < count; i++)
        {
            if (IsUserMessage(session.Messages[i]))
            {
                usersBefore++;
            }
        }

        if (usersBefore == 0)
        {
            return 0;
        }

        var starts = ApiTurnStarts(session);
        if (starts.Count == UserCount(session))
        {
            // Режут по сообщению человека — история кончается перед его ходом.
            if (count < session.Messages.Count && IsUserMessage(session.Messages[count]))
            {
                return starts[usersBefore];
            }

            // Режут по ответу сразу за вопросом — от хода остаётся только вопрос.
            if (count < session.Messages.Count && IsUserMessage(session.Messages[count - 1]))
            {
                return starts[usersBefore - 1] + 1;
            }

            // Режут по концу ленты — история целиком.
            if (count == session.Messages.Count)
            {
                return session.ApiMessages.Count;
            }
        }

        return KeepFor(session, count);
    }

    private static bool IsUserMessage(ChatDisplayMessage message) =>
        message.Role.Equals("user", StringComparison.OrdinalIgnoreCase);

    public static bool ReplaceUserText(ChatSession session, string userId, string newText)
    {
        ArgumentNullException.ThrowIfNull(session);
        var text = newText.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var index = session.Messages.FindIndex(item =>
            item.Id == userId && item.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        session.Messages[index].Text = text;
        if (index + 1 < session.Messages.Count)
        {
            session.Messages.RemoveRange(index + 1, session.Messages.Count - index - 1);
        }

        TruncateApiToMatchDisplay(session);
        for (var i = session.ApiMessages.Count - 1; i >= 0; i--)
        {
            if (ChatContent.IsTurnStart(session.ApiMessages[i]))
            {
                // Rewriting the text must not silently drop what the user attached. Documents
                // were being dropped here: the rebuild read Images only, so the card stayed in
                // the transcript while the PDF itself vanished from the model's context. The
                // shared builder carries images, documents and quotes alike.
                session.ApiMessages[i] = new ChatMessage
                {
                    Role = "user",
                    Content = ChatContent.ForUser(session.Messages[index], session.Messages, index)
                };
                break;
            }
        }

        session.UpdatedAt = DateTime.Now;
        return true;
    }

    public static ChatDisplayMessage? LastUser(ChatSession session) =>
        session.Messages.LastOrDefault(item => item.Role.Equals("user", StringComparison.OrdinalIgnoreCase));

    /// <summary>Реплика человека перед последней; null, если она в разговоре первая.</summary>
    /// <remarks>
    /// Нужна маршрутизатору «Авто»: продолжение вроде «а теперь почини» само по себе выглядит
    /// пустяком, и без предыдущей строки он уводит настоящий ремонт на дешёвую модель.
    /// </remarks>
    public static ChatDisplayMessage? PreviousUser(ChatSession session)
    {
        var users = session.Messages
            .Where(item => item.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return users.Count >= 2 ? users[^2] : null;
    }

    private static int UserCount(ChatSession session) =>
        session.Messages.Count(IsUserMessage);

    private static bool IsFinalAssistant(ChatMessage message) =>
        message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
        (message.ToolCalls is null || message.ToolCalls.Count == 0);

    private static void TruncateApiToMatchDisplay(ChatSession session)
    {
        var keep = KeepFor(session, session.Messages.Count);
        if (keep < session.ApiMessages.Count)
        {
            session.ApiMessages.RemoveRange(keep, session.ApiMessages.Count - keep);
        }
    }

    /// <summary>
    /// Приближение разреза по закрытым ответам: сколько записей истории оставить под первые
    /// <paramref name="displayCount"/> сообщений ленты.
    /// </summary>
    private static int KeepFor(ChatSession session, int displayCount)
    {
        var usersWanted = 0;
        for (var i = 0; i < displayCount; i++)
        {
            if (IsUserMessage(session.Messages[i]))
            {
                usersWanted++;
            }
        }

        if (usersWanted <= 0)
        {
            return 0;
        }

        var pendingUser = displayCount > 0 && IsUserMessage(session.Messages[displayCount - 1]);
        var turnsToClose = pendingUser ? usersWanted - 1 : usersWanted;
        var usersSeen = 0;
        var turnsClosed = 0;
        var keep = 0;

        for (var i = 0; i < session.ApiMessages.Count; i++)
        {
            var message = session.ApiMessages[i];
            var turnStart = ChatContent.IsTurnStart(message);
            if (turnStart)
            {
                usersSeen++;
                if (usersSeen > usersWanted)
                {
                    break;
                }
            }

            keep = i + 1;

            if (IsFinalAssistant(message))
            {
                turnsClosed++;
            }

            if (pendingUser && usersSeen == usersWanted && turnStart)
            {
                break;
            }

            if (!pendingUser && turnsClosed >= turnsToClose && IsFinalAssistant(message))
            {
                break;
            }
        }

        return keep;
    }
}
