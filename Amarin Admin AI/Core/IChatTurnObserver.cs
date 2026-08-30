namespace Amarin.Core;

internal interface IChatTurnObserver
{
    void OnUserAppended(ChatDisplayMessage user);

    void OnAssistantStarted(ChatDisplayMessage assistant);

    void OnAssistantText(ChatDisplayMessage assistant);

    void OnToolsChanged(ChatDisplayMessage assistant);

    void OnAssistantCompleted(ChatDisplayMessage assistant);

    void OnAssistantCancelled(ChatDisplayMessage assistant);

    void OnError(string message);
}
