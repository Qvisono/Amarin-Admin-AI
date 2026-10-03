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
    /// Что человек написал после начала хода. Берётся на границе раунда и вплетается в контекст —
    /// модель может сменить курс, не теряя сделанного.
    /// </summary>
    /// <remarks>
    /// С реализацией по умолчанию: наблюдателям, которым посреди хода ничего не приходит (прежде
    /// всего молчаливым в тестах), знать о нём незачем.
    /// </remarks>
    bool TryTakeQueuedMessage(out string text)
    {
        text = "";
        return false;
    }
}

/// <summary>Наблюдатель, которому ничего не нужно: прогон без открытого окна чата.</summary>
internal sealed class SilentTurnObserver : IChatTurnObserver
{
    public static SilentTurnObserver Instance { get; } = new();

    public void OnUserAppended(ChatDisplayMessage user)
    {
    }

    public void OnAssistantStarted(ChatDisplayMessage assistant)
    {
    }

    public void OnAssistantText(ChatDisplayMessage assistant)
    {
    }

    public void OnToolsChanged(ChatDisplayMessage assistant)
    {
    }

    public void OnAssistantCompleted(ChatDisplayMessage assistant)
    {
    }

    public void OnAssistantCancelled(ChatDisplayMessage assistant)
    {
    }

    public void OnError(string message)
    {
    }
}
