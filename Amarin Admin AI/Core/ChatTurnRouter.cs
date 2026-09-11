namespace Amarin.Core;

/// <summary>
/// То же, что <see cref="IChatTurnObserver"/>, но с ходом в аргументе: окну нужно знать, чей
/// это ход, — видимого чата или фонового.
/// </summary>
internal interface IChatTurnUi
{
    void TurnUserAppended(RunningTurn turn, ChatDisplayMessage user);

    void TurnAssistantStarted(RunningTurn turn, ChatDisplayMessage assistant);

    void TurnAssistantText(RunningTurn turn, ChatDisplayMessage assistant);

    void TurnToolsChanged(RunningTurn turn, ChatDisplayMessage assistant);

    void TurnAssistantCompleted(RunningTurn turn, ChatDisplayMessage assistant);

    /// <summary>Ответ закрыт ради дописанного сообщения — ход на этом не кончился.</summary>
    void TurnAssistantContinued(RunningTurn turn, ChatDisplayMessage assistant);

    void TurnAssistantCancelled(RunningTurn turn, ChatDisplayMessage assistant);

    void TurnError(RunningTurn turn, string message);

    /// <summary>Ход забрал сообщение, дописанное во время работы: обещание «учту» исполнено.</summary>
    void TurnQueuedTaken(RunningTurn turn);
}

/// <summary>
/// Наблюдатель одного хода: обновляет его состояние, сохраняет чат и передаёт событие окну,
/// назвав ход по имени.
/// </summary>
/// <remarks>
/// Раньше наблюдателем было само окно, и все обработчики работали с полем «текущий чат». Пока
/// ход был один, это совпадало; с фоновыми ходами ответ сохранялся бы не в тот чат. Решение
/// «мой ли это чат» принимает окно, а не роутер: часть событий нужна ему и для скрытого хода —
/// снять блокировку, показать остаток на счету, погасить индикатор в списке.
/// </remarks>
/// <param name="save">Записать чат немедленно — так завершается ход.</param>
/// <param name="saveSoon">
/// Отложить запись. Ход правит сообщение десятки раз в секунду, и сохранять его на каждую
/// правку значит непрерывно переписывать файл; но и ждать конца ответа нельзя — фоновый чат
/// должен уцелеть при аварии.
/// </param>
internal sealed class ChatTurnRouter(
    RunningTurn turn,
    Action<ChatSession> save,
    Action<ChatSession> saveSoon,
    IChatTurnUi ui)
    : IChatTurnObserver
{
    public void OnUserAppended(ChatDisplayMessage user)
    {
        saveSoon(turn.Session);
        ui.TurnUserAppended(turn, user);
    }

    public void OnAssistantStarted(ChatDisplayMessage assistant)
    {
        turn.Assistant = assistant;
        turn.AssistantId = assistant.Id;
        turn.PendingText = assistant.Text;
        turn.RenderedText = "";

        // A queued follow-up can start a second answer inside one turn, and the flag set by the
        // first one would otherwise keep the live view from ever rebinding to this one.
        turn.Finished = false;
        ui.TurnAssistantStarted(turn, assistant);
    }

    public void OnAssistantText(ChatDisplayMessage assistant)
    {
        turn.Assistant = assistant;
        turn.PendingText = assistant.Text;
        ui.TurnAssistantText(turn, assistant);
    }

    public void OnToolsChanged(ChatDisplayMessage assistant)
    {
        turn.Assistant = assistant;
        saveSoon(turn.Session);
        ui.TurnToolsChanged(turn, assistant);
    }

    public void OnAssistantCompleted(ChatDisplayMessage assistant)
    {
        turn.Assistant = assistant;
        turn.PendingText = assistant.Text;
        turn.Finished = true;
        save(turn.Session);
        ui.TurnAssistantCompleted(turn, assistant);
    }

    public void OnAssistantContinued(ChatDisplayMessage assistant)
    {
        turn.Assistant = assistant;
        turn.PendingText = assistant.Text;

        // Finished намеренно не трогаем: следующий ответ этого же хода начнётся через мгновение.
        save(turn.Session);
        ui.TurnAssistantContinued(turn, assistant);
    }

    public void OnAssistantCancelled(ChatDisplayMessage assistant)
    {
        turn.Assistant = assistant;
        turn.PendingText = assistant.Text;
        turn.Finished = true;
        save(turn.Session);
        ui.TurnAssistantCancelled(turn, assistant);
    }

    public void OnError(string message) => ui.TurnError(turn, message);

    public bool TryTakeQueuedMessage(out string text)
    {
        if (!turn.TryTakeQueued(out text))
        {
            return false;
        }

        ui.TurnQueuedTaken(turn);
        return true;
    }
}
