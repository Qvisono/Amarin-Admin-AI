using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Ветка в новый чат (1.37.0): что копия берёт с собой, а что остаётся у исходного чата.
/// </summary>
public sealed class ChatBranchCopyTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0);

    [Fact]
    public void The_branch_ends_on_the_chosen_answer_in_both_histories()
    {
        var source = TwoTurns();

        var branch = ChatBranchCopy.FromAnswer(source, "a1", "Branch", Now)!;

        Assert.Equal(["u1", "a1"], branch.Messages.Select(message => message.Id));
        Assert.Equal(["q1", "r1"], branch.ApiMessages.Select(message => ChatContent.ReadText(message.Content)));
        Assert.Equal(4, source.Messages.Count);
        Assert.Equal(4, source.ApiMessages.Count);
    }

    [Fact]
    public void The_branch_is_a_new_chat_that_shares_nothing_with_the_source()
    {
        var source = TwoTurns();
        source.ScheduleJobId = "job";
        source.Summary = "summary";
        source.Agents = [new StoredAgentConversation { Id = "a1" }];

        var branch = ChatBranchCopy.FromAnswer(source, "a1", "Branch", Now)!;

        Assert.NotEqual(source.Id, branch.Id);
        Assert.Equal("Branch", branch.Title);
        Assert.Equal(Now, branch.CreatedAt);
        Assert.Null(branch.ScheduleJobId);
        Assert.Null(branch.Summary);
        Assert.Null(branch.Agents);
        Assert.NotSame(source.Messages[1], branch.Messages[1]);
    }

    /// <summary>Деньги уже записаны на исходный чат; копия с ценами удвоила бы его стоимость в списке.</summary>
    [Fact]
    public void The_branch_carries_no_money_no_variants_and_no_task_marks()
    {
        var source = TwoTurns();
        var answer = source.Messages[1];
        answer.Cost = new VeniceCost { Usd = 0.5m, HasData = true };
        answer.ModelCost = new VeniceCost { Usd = 0.5m, HasData = true };
        answer.Variants = [new ChatBranch { Messages = [new ChatDisplayMessage { Role = "assistant", Id = "hidden" }] }];
        answer.ToolRounds = [new ToolRound { Calls = [new ToolCallRecord { Id = "c", Name = "deferred_task", Deferred = new DeferredRef("task-1", "call", DeferredKind.Reminder) }] }];

        var branch = ChatBranchCopy.FromAnswer(source, "a1", "Branch", Now)!;

        var copy = branch.Messages[1];
        Assert.Null(copy.Cost);
        Assert.Null(copy.ModelCost);
        Assert.Null(copy.Variants);
        Assert.Null(copy.ToolRounds[0].Calls[0].Deferred);
        Assert.Equal(0m, ChatCost.Total(branch));
        Assert.Equal(0.5m, ChatCost.Total(source));
    }

    [Fact]
    public void A_compaction_beyond_the_cut_is_dropped_and_one_before_it_kept()
    {
        var beyond = TwoTurns();
        beyond.CompactedThrough = 3;
        beyond.CompactSummary = "summary";
        beyond.CompactFingerprint = "fp";
        beyond.CompactedAfterMessageId = "u2";
        var dropped = ChatBranchCopy.FromAnswer(beyond, "a1", "Branch", Now)!;
        Assert.Equal(0, dropped.CompactedThrough);
        Assert.Null(dropped.CompactSummary);
        Assert.Null(dropped.CompactedAfterMessageId);

        var before = TwoTurns();
        before.CompactedThrough = 2;
        before.CompactSummary = "summary";
        var kept = ChatBranchCopy.FromAnswer(before, "a2", "Branch", Now)!;
        Assert.Equal(2, kept.CompactedThrough);
        Assert.Equal("summary", kept.CompactSummary);
    }

    [Fact]
    public void Only_an_answer_starts_a_branch_and_temporariness_carries_over()
    {
        var source = TwoTurns();
        Assert.Null(ChatBranchCopy.FromAnswer(source, "u1", "Branch", Now));
        Assert.Null(ChatBranchCopy.FromAnswer(source, "missing", "Branch", Now));

        source.IsTemporary = true;
        Assert.True(ChatBranchCopy.FromAnswer(source, "a2", "Branch", Now)!.IsTemporary);
    }

    private static ChatSession TwoTurns() => new()
    {
        Id = "source",
        Title = "Source",
        Messages =
        [
            new ChatDisplayMessage { Role = "user", Id = "u1", Text = "q1" },
            new ChatDisplayMessage { Role = "assistant", Id = "a1", Text = "r1" },
            new ChatDisplayMessage { Role = "user", Id = "u2", Text = "q2" },
            new ChatDisplayMessage { Role = "assistant", Id = "a2", Text = "r2" }
        ],
        ApiMessages =
        [
            new ChatMessage { Role = "user", Content = ChatContent.Text("q1") },
            new ChatMessage { Role = "assistant", Content = ChatContent.Text("r1") },
            new ChatMessage { Role = "user", Content = ChatContent.Text("q2") },
            new ChatMessage { Role = "assistant", Content = ChatContent.Text("r2") }
        ]
    };
}
