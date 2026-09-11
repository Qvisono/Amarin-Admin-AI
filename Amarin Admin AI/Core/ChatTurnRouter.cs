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

    void TurnAssistantCancelled(RunningTurn turn, ChatDisplayMessage assistant);

    void TurnError(RunningTurn turn, string message);
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

    public void OnAssistantCancelled(ChatDisplayMessage assistant)
    {
        turn.Assistant = assistant;
        turn.PendingText = assistant.Text;
        turn.Finished = true;
        save(turn.Session);
        ui.TurnAssistantCancelled(turn, assistant);
    }

    public void OnError(string message) => ui.TurnError(turn, message);
}
