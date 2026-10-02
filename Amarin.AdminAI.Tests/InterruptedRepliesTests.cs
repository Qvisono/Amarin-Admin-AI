using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Ответ, записанный посреди хода (программу закрыли или она упала), при открытии чата
/// закрывается как отменённый, а не крутит «пишется» вечно.
/// </summary>
public sealed class InterruptedRepliesTests
{
    private static ChatSession Chat(params ChatDisplayMessage[] messages)
    {
        var session = new ChatSession { Id = "c" };
        session.Messages.AddRange(messages);
        return session;
    }

    [Fact]
    public void A_reply_frozen_while_streaming_is_closed_as_cancelled_with_its_tools()
    {
        var call = new ToolCallRecord { Name = "run_powershell", Status = ToolCallStatus.Running };
        var reply = new ChatDisplayMessage
        {
            Role = "assistant",
            Id = "a1",
            Status = AssistantStatus.Streaming,
            ToolRounds = [new ToolRound { Calls = [call], InfoLine = EngineLines.RunningTools }]
        };

        var closed = ChatEngine.CloseInterruptedReplies(Chat(new ChatDisplayMessage { Role = "user", Id = "u1" }, reply));

        Assert.True(closed);
        Assert.Equal(AssistantStatus.Cancelled, reply.Status);
        Assert.Equal(ToolCallStatus.Failed, call.Status);
        Assert.Equal(EngineLines.ToolsStopped, reply.ToolRounds[0].InfoLine);
    }

    [Fact]
    public void Finished_replies_are_left_alone()
    {
        var done = new ChatDisplayMessage { Role = "assistant", Id = "a1", Status = AssistantStatus.Complete };
        var failed = new ChatDisplayMessage { Role = "assistant", Id = "a2", Status = AssistantStatus.Error };

        Assert.False(ChatEngine.CloseInterruptedReplies(Chat(done, failed)));
        Assert.Equal(AssistantStatus.Complete, done.Status);
        Assert.Equal(AssistantStatus.Error, failed.Status);
    }
}
