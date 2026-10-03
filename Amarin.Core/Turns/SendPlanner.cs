namespace Amarin.Core;

/// <summary>Что сделать с сообщением — или почему его не отправлять.</summary>
internal enum SendVerdict
{
    /// <summary>Отправлять нечего: пусто и без вложений.</summary>
    Nothing,

    /// <summary>
    /// Только цитата. Она говорит, о чём ответ, но не чего хотят: движок молча выбросил бы такой
    /// ход, поэтому человек остаётся в поле — подсказка в нём уже просит ответ.
    /// </summary>
    NeedsText,

    /// <summary>Команда окна (новый чат, выгрузка…) — она ключа не требует и денег не стоит.</summary>
    LocalCommand,

    /// <summary>Нет ключа, которым платить.</summary>
    NoKey,

    /// <summary><c>/agent</c> с вложениями: агент работает по текстовой постановке и их не берёт.</summary>
    AgentNoAttachments,

    /// <summary><c>/agent</c> с цитатами: чата агент не видит, и цитата указывала бы на непрочитанное.</summary>
    AgentNoQuotes,

    /// <summary>В чате уже отвечают: строка встаёт в очередь идущего хода.</summary>
    FollowUp,

    /// <summary>Машину, на которую смотрит чат, удалили: молча выполнить на этом ПК нельзя.</summary>
    TargetGone,

    /// <summary>Одновременно идёт столько ходов, сколько можно. Текст остаётся в поле.</summary>
    LimitReached,

    /// <summary>Команда <c>/agent</c>.</summary>
    Agent,

    /// <summary>Обычный ход.</summary>
    Send
}

/// <summary>Что лежит в поле ввода (или в сообщении, которое отправляют заново).</summary>
internal readonly record struct OutgoingDraft(string Text, int Images = 0, int Files = 0, int Quotes = 0)
{
    public bool HasAttachments => Images > 0 || Files > 0;
}

/// <summary>Решение и то, что для него уже разобрано: текст без краёв и команда.</summary>
internal readonly record struct SendPlan(SendVerdict Verdict, string Text, LocalCommand? Local = null, ChatCommand? Command = null);

/// <summary>
/// Порядок проверок перед ходом — одной чистой функцией.
/// </summary>
/// <remarks>
/// До 1.30.0 одни и те же проверки (ключ, <c>/agent</c> с вложениями или цитатами, лимит ходов)
/// были выписаны в окне трижды — в отправке, правке вопроса и повторе, — и порядок у них
/// расходился. Порядок здесь — правило: команды окна до ключа (они денег не стоят), ключ до
/// разбора команды, всё — до развилки: отказ после неё оставил бы прежнюю ветку спрятанной, а
/// на её месте — вопрос без ответа.
/// </remarks>
internal static class SendPlanner
{
    /// <summary>Отправка из поля ввода.</summary>
    /// <param name="busy">В этом чате идёт ход.</param>
    /// <param name="targetGone">Машину чата удалили из списка.</param>
    /// <param name="hasRoom">
    /// Можно начать ещё один ход. Проверяется здесь, до того как окно очистит поле: до 1.30.0
    /// отказ давал сам запуск хода, и сообщение, отправленное при трёх идущих ответах в других
    /// чатах, пропадало вместе с вложениями — поле уже было пустым.
    /// </param>
    public static SendPlan ForComposer(OutgoingDraft draft, bool hasUsableKey, bool busy, bool targetGone, bool hasRoom)
    {
        var text = draft.Text.Trim();

        // Вложения без текста — тоже сообщение: «посмотри» не нуждается в словах.
        if (text.Length == 0 && !draft.HasAttachments)
        {
            return new SendPlan(draft.Quotes > 0 ? SendVerdict.NeedsText : SendVerdict.Nothing, text);
        }

        if (ChatCommands.TryParseLocal(text) is { } local)
        {
            return new SendPlan(SendVerdict.LocalCommand, text, Local: local);
        }

        var command = ChatCommands.TryParse(text);
        if (Refusal(draft, command, hasUsableKey) is { } refused)
        {
            return new SendPlan(refused, text, Command: command);
        }

        if (busy)
        {
            return new SendPlan(SendVerdict.FollowUp, text, Command: command);
        }

        if (targetGone)
        {
            return new SendPlan(SendVerdict.TargetGone, text, Command: command);
        }

        // Дописанное в идущий ход места не требует — оно не начинает нового.
        if (!hasRoom)
        {
            return new SendPlan(SendVerdict.LimitReached, text, Command: command);
        }

        return new SendPlan(command is not null ? SendVerdict.Agent : SendVerdict.Send, text, Command: command);
    }

    /// <summary>
    /// Отправка заново — исправленный вопрос встаёт новым вариантом: ключ, <c>/agent</c> без
    /// вложений и цитат, место для ещё одного хода.
    /// </summary>
    public static SendPlan ForRerun(OutgoingDraft draft, bool hasUsableKey, bool hasRoom)
    {
        var text = draft.Text.Trim();
        var command = ChatCommands.TryParse(text);
        var verdict = Refusal(draft, command, hasUsableKey) ??
                      (!hasRoom ? SendVerdict.LimitReached : command is not null ? SendVerdict.Agent : SendVerdict.Send);
        return new SendPlan(verdict, text, Command: command);
    }

    private static SendVerdict? Refusal(OutgoingDraft draft, ChatCommand? command, bool hasUsableKey) =>
        !hasUsableKey ? SendVerdict.NoKey
        : command is not null && draft.HasAttachments ? SendVerdict.AgentNoAttachments
        : command is not null && draft.Quotes > 0 ? SendVerdict.AgentNoQuotes
        : null;
}
