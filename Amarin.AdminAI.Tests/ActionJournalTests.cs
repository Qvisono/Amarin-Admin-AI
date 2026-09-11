using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The journal answers "what did it do to my machine", so the failures that matter are the silent
/// ones: an agent's calls missing because they hang off a nested record, and old chats sorting to
/// the year zero because their calls predate the timestamp field.
/// </summary>
public sealed class ActionJournalTests
{
    private static ToolCallRecord Call(string name, DateTime startedAt = default, bool success = true) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            ArgumentsJson = """{"path":"C:/"}""",
            ResultPreview = "готово",
            Success = success,
            Status = success ? ToolCallStatus.Done : ToolCallStatus.Failed,
            StartedAt = startedAt
        };

    private static ChatSession Session(params ToolCallRecord[] calls)
    {
        var session = new ChatSession { Id = "chat-1", Title = "Разговор" };
        session.Messages.Add(new ChatDisplayMessage
        {
            Role = "assistant",
            CreatedAt = new DateTime(2026, 3, 1, 10, 0, 0),
            ToolRounds = [new ToolRound { Calls = [.. calls] }]
        });
        return session;
    }

    [Fact]
    public void An_agents_own_calls_are_listed_and_labelled_with_its_name()
    {
        var spawn = Call("init_agent", new DateTime(2026, 3, 1, 10, 0, 1));
        spawn.NestedAgent = new AgentRunRecord
        {
            ModelId = "grok-4-6",
            DisplayName = "Агент grok-4-6",
            ToolRounds =
            [
                new ToolRound { Calls = [Call("run_powershell", new DateTime(2026, 3, 1, 10, 0, 2))] }
            ]
        };

        var entries = ActionJournal.FromSession(Session(spawn));

        Assert.Equal(2, entries.Count);
        var nested = Assert.Single(entries, entry => entry.ToolName == "run_powershell");
        Assert.Equal("Агент grok-4-6", nested.AgentName);

        // The call that only delegated is still listed, but as the chat's own.
        Assert.Null(Assert.Single(entries, entry => entry.ToolName == "init_agent").AgentName);
    }

    [Fact]
    public void Calls_from_before_the_timestamp_existed_borrow_the_messages_time()
    {
        var entry = Assert.Single(ActionJournal.FromSession(Session(Call("registry"))));

        Assert.Equal(new DateTime(2026, 3, 1, 10, 0, 0), entry.StartedAt);
        Assert.True(entry.TimeIsApproximate);
    }

    [Fact]
    public void A_call_with_its_own_clock_keeps_it()
    {
        var at = new DateTime(2026, 3, 1, 11, 22, 33);

        var entry = Assert.Single(ActionJournal.FromSession(Session(Call("registry", at))));

        Assert.Equal(at, entry.StartedAt);
        Assert.False(entry.TimeIsApproximate);
    }

    [Fact]
    public void Newest_first()
    {
        var session = Session(
            Call("first", new DateTime(2026, 3, 1, 10, 0, 0)),
            Call("second", new DateTime(2026, 3, 1, 12, 0, 0)));

        var entries = ActionJournal.FromSession(session);

        Assert.Equal(["second", "first"], entries.Select(entry => entry.ToolName));
    }

    [Fact]
    public void Failures_are_carried_through_rather_than_flattened_into_success()
    {
        var entry = Assert.Single(ActionJournal.FromSession(Session(Call("registry", success: false))));

        Assert.False(entry.Success);
        Assert.Equal(ToolCallStatus.Failed, entry.Status);
    }

    [Fact]
    public void An_empty_session_yields_nothing_rather_than_throwing()
    {
        Assert.Empty(ActionJournal.FromSession(new ChatSession()));
        Assert.Empty(ActionJournal.FromSession(null));
    }

    [Fact]
    public void Every_chat_on_disk_is_read_and_merged_newest_first()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "chats"));
        try
        {
            var store = new ChatStore(root);

            var older = Session(Call("older", new DateTime(2026, 3, 1, 9, 0, 0)));
            older.Id = "chat-older";
            older.UpdatedAt = new DateTime(2026, 3, 1, 9, 0, 0);
            store.Save(older);

            var newer = Session(Call("newer", new DateTime(2026, 3, 1, 13, 0, 0)));
            newer.Id = "chat-newer";
            newer.UpdatedAt = new DateTime(2026, 3, 1, 13, 0, 0);
            store.Save(newer);

            var entries = ActionJournal.Collect(store);

            Assert.Equal(["newer", "older"], entries.Select(entry => entry.ToolName));
            Assert.Equal("chat-newer", entries[0].ChatId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_chat_file_that_cannot_be_parsed_does_not_hide_the_others()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "chats"));
        try
        {
            var store = new ChatStore(root);
            var good = Session(Call("survivor", new DateTime(2026, 3, 1, 9, 0, 0)));
            good.Id = "chat-good";
            store.Save(good);

            // Half-written file plus an index entry pointing at it: what a crash mid-save leaves.
            File.WriteAllText(Path.Combine(root, "chats", "chat-broken.json"), "{ not json");
            var broken = new ChatSession { Id = "chat-broken", Title = "Битый" };
            store.Save(broken);
            File.WriteAllText(Path.Combine(root, "chats", "chat-broken.json"), "{ not json");

            Assert.Equal("survivor", Assert.Single(ActionJournal.Collect(store)).ToolName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
