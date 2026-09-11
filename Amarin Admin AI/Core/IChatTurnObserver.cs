namespace Amarin.Core;

internal interface IChatTurnObserver
{
    void OnUserAppended(ChatDisplayMessage user);

    void OnAssistantStarted(ChatDisplayMessage assistant);

    void OnAssistantText(ChatDisplayMessage assistant);

    void OnToolsChanged(ChatDisplayMessage assistant);

    void OnAssistantCompleted(ChatDisplayMessage assistant);

    /// <summary>
    /// Ответ закончился не потому, что модель договорила, а потому, что в контекст влилось
    /// дописанное человеком сообщение: дальше пойдёт следующий ответ того же хода.
    /// </summary>
    /// <remarks>
    /// По умолчанию — обычное завершение. Разница нужна ровно одному потребителю, окну: «ответ
    /// готов» посреди хода и всплывающее уведомление об этом были бы неправдой.
    /// </remarks>
    void OnAssistantContinued(ChatDisplayMessage assistant) => OnAssistantCompleted(assistant);

    void OnAssistantCancelled(ChatDisplayMessage assistant);

    void OnError(string message);

    /// <summary>
    /// Something the user typed after this turn started. Taken on a round boundary and folded
    /// into the context, so the model can change course without losing the work already done.
    /// </summary>
    /// <remarks>
    /// Default implementation so that observers which cannot receive anything mid-turn — the
    /// silent ones in tests, above all — need not know this exists.
    /// </remarks>
    bool TryTakeQueuedMessage(out string text)
    {
        text = "";
        return false;
    }
}
