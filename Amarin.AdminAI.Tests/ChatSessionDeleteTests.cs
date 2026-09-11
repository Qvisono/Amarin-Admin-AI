using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Deleting a turn used to take the whole rest of the conversation with it, because the wire
/// history could only be cut as a prefix. These pin the middle-of-the-list case: the visible
/// messages that survive, and — the part that actually breaks the next request if it is wrong —
/// the tool-call pairings left behind in <see cref="ChatSession.ApiMessages"/>.
/// </summary>
public sealed class ChatSessionDeleteTests
{
    /// <summary>Three turns; the middle one answered through a tool call.</summary>
    private static ChatSession ThreeTurns()
    {
        var session = new ChatSession { Id = "s" };
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "первый" });
        session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Id = "a1", Text = "один" });
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u2", Text = "второй" });
        session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Id = "a2", Text = "два" });
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u3", Text = "третий" });
        session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Id = "a3", Text = "три" });

        session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text("первый") });
        session.ApiMessages.Add(new ChatMessage { Role = "assistant", Content = ChatContent.Text("один") });
        session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text("второй") });
        session.ApiMessages.Add(new ChatMessage
        {
            Role = "assistant",
            ToolCalls =
            [
                new ToolCall { Id = "c1", Function = new FunctionCall { Name = "read_file", Arguments = "{}" } }
            ]
        });
        session.ApiMessages.Add(new ChatMessage
        {
            Role = "tool",
            ToolCallId = "c1",
            Name = "read_file",
            Content = ChatContent.Text("data")
        });
        session.ApiMessages.Add(new ChatMessage { Role = "assistant", Content = ChatContent.Text("два") });
        session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text("третий") });
        session.ApiMessages.Add(new ChatMessage { Role = "assistant", Content = ChatContent.Text("три") });
        return session;
    }

    private static List<string> Texts(ChatSession session) =>
        session.ApiMessages.Select(item => ChatContent.ReadText(item.Content) ?? item.Role).ToList();

    [Fact]
    public void Deleting_a_middle_answer_leaves_the_turns_around_it()
    {
        var session = ThreeTurns();

        Assert.True(ChatSessionEdit.DeleteTurn(session, "a2"));

        Assert.Equal(["u1", "a1", "u3", "a3"], session.Messages.Select(m => m.Id));
        Assert.Equal(["первый", "один", "третий", "три"], Texts(session));
    }

    [Fact]
    public void Deleting_a_middle_turn_takes_its_tool_traffic_with_it()
    {
        // An orphaned tool message — one whose assistant(tool_calls) partner is gone — makes the
        // API reject the entire history, so this is the failure the range delete exists to avoid.
        var session = ThreeTurns();

        ChatSessionEdit.DeleteTurn(session, "a2");

        Assert.DoesNotContain(session.ApiMessages, item => item.Role == "tool");
        Assert.DoesNotContain(session.ApiMessages, item => item.ToolCalls is { Count: > 0 });
    }

    [Fact]
    public void Deleting_from_the_user_side_removes_the_same_turn()
    {
        var session = ThreeTurns();

        Assert.True(ChatSessionEdit.DeleteTurn(session, "u2"));

        Assert.Equal(["u1", "a1", "u3", "a3"], session.Messages.Select(m => m.Id));
        Assert.Equal(["первый", "один", "третий", "три"], Texts(session));
    }

    [Fact]
    public void Deleting_the_last_turn_still_just_trims_the_tail()
    {
        var session = ThreeTurns();

        Assert.True(ChatSessionEdit.DeleteTurn(session, "a3"));

        Assert.Equal(["u1", "a1", "u2", "a2"], session.Messages.Select(m => m.Id));
        Assert.Equal(6, session.ApiMessages.Count);
        Assert.Equal("два", ChatContent.ReadText(session.ApiMessages[^1].Content));
    }

    [Fact]
    public void Deleting_every_turn_empties_both_lists()
    {
        var session = ThreeTurns();

        ChatSessionEdit.DeleteTurn(session, "a1");
        ChatSessionEdit.DeleteTurn(session, "a2");
        ChatSessionEdit.DeleteTurn(session, "a3");

        Assert.Empty(session.Messages);
        Assert.Empty(session.ApiMessages);
    }

    [Fact]
    public void An_unknown_id_changes_nothing()
    {
        var session = ThreeTurns();

        Assert.False(ChatSessionEdit.DeleteTurn(session, "nope"));
        Assert.Equal(6, session.Messages.Count);
        Assert.Equal(8, session.ApiMessages.Count);
    }

    [Fact]
    public void A_wire_history_that_lost_a_turn_falls_back_to_trimming_the_tail()
    {
        // A compressed or hand-edited history has no turn-for-turn mapping left. Dropping the
        // tail loses messages, but it always leaves a history the API will accept.
        var session = ThreeTurns();
        session.ApiMessages.RemoveRange(0, 2);

        Assert.True(ChatSessionEdit.DeleteTurn(session, "a2"));

        Assert.Equal(["u1", "a1", "u3", "a3"], session.Messages.Select(m => m.Id));

        // Whatever survived, every tool result still answers a call that is also still there.
        // Набор строк, а не строк-с-возможным-null: ToolCallId объявлен nullable, и без явного
        // приведения xUnit подбирал перегрузку Contains<string?> — сборка ругалась CS8620.
        var announced = session.ApiMessages
            .SelectMany(item => item.ToolCalls ?? [])
            .Select(call => (string?)call.Id)
            .ToHashSet();
        Assert.All(
            session.ApiMessages.Where(item => item.Role == "tool"),
            item => Assert.Contains(item.ToolCallId, announced));
    }
}
