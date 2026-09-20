namespace Amarin.Core;

/// <summary>
/// Приписывает цену придуманного заголовка к первому ответу переписки.
/// </summary>
/// <remarks>
/// Заголовок сочиняется отдельной, брошенной задачей: она может закончиться и раньше, чем
/// появился первый ответ, и позже, чем он уже дописан, сохранён и нарисован. Оба порядка
/// обязаны кончиться одинаково, поэтому запись идёт через одно место и под одним замком —
/// ход доводит сообщение на фоновом потоке, а заголовок приходит с потока интерфейса.
/// </remarks>
internal static class ChatTitleCost
{
    private static readonly Lock Gate = new();

    /// <summary>
    /// Цена заголовка стала известна. Возвращает сообщение, у которого пришлось поправить уже
    /// закрытый счёт, — его нужно перерисовать и сохранить; <c>null</c> — трогать нечего.
    /// </summary>
    /// <remarks>
    /// Именно сообщение, а не просто «да/нет»: окну надо перерисовать один пузырь, а не весь
    /// чат. Пересборка ленты ради ценника заодно сбрасывала лупу.
    /// </remarks>
    public static ChatDisplayMessage? Book(ChatSession session, VeniceCost? cost)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Модель не сообщила цену: строка «$0 Заголовок чата» соврала бы про арифметику,
        // а отсутствующая цифра — это не бесплатно, это неизвестно.
        if (cost is not { HasData: true })
        {
            return null;
        }

        lock (Gate)
        {
            session.TitleCost = cost;

            var first = FirstAnswer(session);
            if (first is null || first.TitleCost is not null)
            {
                // Ответа ещё нет — его подберёт Attach, когда ход будет закрывать счёт.
                return null;
            }

            first.TitleCost = cost;

            // Cost присваивается только там, где движок закрывает счёт, поэтому null надёжно
            // значит «ещё не закрыт»: сложит он сам, и прибавлять здесь нельзя.
            if (first.Cost is null)
            {
                return null;
            }

            // ModelCost намеренно не трогаем: этих денег в счёте хода не было.
            first.Cost = first.Cost.Add(cost);
            return first;
        }
    }

    /// <summary>
    /// Ход закрывает сообщение: если это первый ответ чата, забираем на него цену заголовка.
    /// Сложит её идущий следом пересчёт счёта.
    /// </summary>
    public static void Attach(ChatSession session, ChatDisplayMessage assistant)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(assistant);

        lock (Gate)
        {
            if (session.TitleCost is not { HasData: true } cost ||
                assistant.TitleCost is not null ||
                !ReferenceEquals(FirstAnswer(session), assistant))
            {
                return;
            }

            assistant.TitleCost = cost;
        }
    }

    private static ChatDisplayMessage? FirstAnswer(ChatSession session) =>
        session.Messages.FirstOrDefault(
            item => item.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase));
}
