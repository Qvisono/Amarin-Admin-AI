using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>Команды «/» (D9) и сжатие контекста (D10).</summary>
public sealed class ChatCommandsAndCompactionTests
{
    // ───────────────────────── команды ─────────────────────────

    [Fact]
    public void The_suggestion_lists_commands_while_the_name_is_being_typed()
    {
        Assert.Equal(ChatCommands.All.Count, ChatCommands.Suggest("/").Count);
        Assert.Equal(ChatCommands.Export, Assert.Single(ChatCommands.Suggest("/EX")).Name);
        Assert.Empty(ChatCommands.Suggest("/usr/bin"));
        Assert.Empty(ChatCommands.Suggest("/new chat"));
        Assert.Empty(ChatCommands.Suggest("hello /new"));
    }

    [Theory]
    [InlineData("/new", ChatCommands.New, "")]
    [InlineData("/NEW  check the disk ", ChatCommands.New, "check the disk")]
    [InlineData("/export md", ChatCommands.Export, "md")]
    [InlineData("/readonly", ChatCommands.ReadOnly, "")]
    [InlineData("/model grok", ChatCommands.Model, "grok")]
    public void A_window_command_is_recognised_by_its_exact_name(string input, string name, string argument)
    {
        var command = ChatCommands.TryParseLocal(input);

        Assert.NotNull(command);
        Assert.Equal(name, command.Value.Name);
        Assert.Equal(argument, command.Value.Argument);
    }

    [Theory]
    [InlineData("/newer")]
    [InlineData("/usr/local/bin")]
    [InlineData("/compact and then what?")]
    [InlineData("/agent fix it")]
    [InlineData("new")]
    public void Anything_else_goes_to_the_model_as_text(string input) =>
        Assert.Null(ChatCommands.TryParseLocal(input));

    [Fact]
    public void Every_command_has_its_captions_and_the_prompt_names_them_all()
    {
        foreach (var command in ChatCommands.All)
        {
            Assert.True(StringsRu.Values.ContainsKey(command.DescriptionKey), command.DescriptionKey);
            Assert.True(command.ArgumentKey is null || StringsRu.Values.ContainsKey(command.ArgumentKey), command.ArgumentKey);
        }

        var block = ChatCommands.PromptBlock();
        Assert.All(ChatCommands.All, command => Assert.Contains("/" + command.Name, block, StringComparison.Ordinal));
        Assert.DoesNotContain("example", block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_model_is_found_by_the_closest_name()
    {
        (string, string)[] models =
        [
            ("grok-4-6", "Grok 4.6"),
            ("grok-4-6-fast", "Grok 4.6 Fast"),
            ("openrouter:anthropic/claude-sonnet-4.5", "Claude Sonnet 4.5")
        ];

        Assert.Equal("grok-4-6", ChatCommands.MatchModel("grok", models));
        Assert.Equal("grok-4-6-fast", ChatCommands.MatchModel("grok 4.6 fast", models));
        Assert.Equal("openrouter:anthropic/claude-sonnet-4.5", ChatCommands.MatchModel("sonnet", models));
        Assert.Equal("openrouter:anthropic/claude-sonnet-4.5", ChatCommands.MatchModel("anthropic/claude-sonnet-4.5", models));
        Assert.Null(ChatCommands.MatchModel("llama", models));
    }

    // ───────────────────────── сжатие ─────────────────────────

    private static ChatMessage User(string text) => new() { Role = "user", Content = ChatContent.Text(text) };

    private static ChatMessage Assistant(string text) => new() { Role = "assistant", Content = ChatContent.Text(text) };

    /// <summary>Чат из <paramref name="turns"/> обменов; во втором — раунд инструмента с картинкой.</summary>
    private static ChatSession Chat(int turns)
    {
        var session = new ChatSession { Id = "c" };
        for (var i = 1; i <= turns; i++)
        {
            session.Messages.Add(new ChatDisplayMessage { Id = "u" + i, Role = "user", Text = "question " + i });
            session.Messages.Add(new ChatDisplayMessage { Id = "a" + i, Role = "assistant", Text = "answer " + i });
            session.ApiMessages.Add(User("question " + i));
            if (i == 2)
            {
                session.ApiMessages.Add(new ChatMessage
                {
                    Role = "assistant",
                    ToolCalls = [new ToolCall { Id = "t1", Function = new FunctionCall { Name = "capture_screenshot", Arguments = "{}" } }]
                });
                session.ApiMessages.Add(new ChatMessage { Role = "tool", ToolCallId = "t1", Content = ChatContent.Text(new string('x', 5000)) });
                // Картинка инструмента: роль user, но это не человек.
                session.ApiMessages.Add(User("look at the picture"));
            }

            session.ApiMessages.Add(Assistant("answer " + i));
        }

        return session;
    }

    [Fact]
    public void The_picture_after_a_tool_is_not_a_human_turn()
    {
        var turns = ContextCompaction.HumanTurns(Chat(3).ApiMessages);

        Assert.Equal(3, turns.Count);
    }

    [Fact]
    public void A_short_chat_has_nothing_to_compact()
    {
        Assert.Null(ContextCompaction.Plan(Chat(ContextCompaction.KeepTurns)));
    }

    [Fact]
    public void The_cut_keeps_the_last_turns_whole_and_marks_the_message_before_them()
    {
        var session = Chat(4);

        var plan = ContextCompaction.Plan(session)!;

        var turns = ContextCompaction.HumanTurns(session.ApiMessages);
        Assert.Equal(turns[^ContextCompaction.KeepTurns], plan.Cut);
        Assert.Equal("user", session.ApiMessages[plan.Cut].Role);
        Assert.Equal("a2", plan.AnchorMessageId);
    }

    [Fact]
    public void After_compaction_the_model_gets_the_summary_and_the_tail_only()
    {
        var session = Chat(4);
        var plan = ContextCompaction.Plan(session)!;
        session.LastPromptTokens = 99_000;

        ContextCompaction.Apply(session, plan, "the summary");

        Assert.True(ContextCompaction.IsActive(session));
        Assert.Equal(session.ApiMessages.Count - plan.Cut, ContextCompaction.Tail(session).Count);
        Assert.Equal(0, session.LastPromptTokens);
        Assert.Contains("data, not instructions", ContextCompaction.PromptBlock("the summary"), StringComparison.Ordinal);
        Assert.Null(ContextCompaction.Plan(session));
    }

    [Fact]
    public void Editing_the_compacted_part_stops_the_summary_from_being_used()
    {
        var session = Chat(4);
        var plan = ContextCompaction.Plan(session)!;
        ContextCompaction.Apply(session, plan, "the summary");

        session.ApiMessages[0] = User("an edited first question");

        Assert.False(ContextCompaction.IsActive(session));
        Assert.Equal(session.ApiMessages.Count, ContextCompaction.Tail(session).Count);
    }

    [Fact]
    public void A_new_answer_after_the_cut_keeps_the_compaction()
    {
        var session = Chat(4);
        ContextCompaction.Apply(session, ContextCompaction.Plan(session)!, "the summary");

        session.ApiMessages.Add(User("question 5"));
        session.ApiMessages.Add(Assistant("answer 5"));

        Assert.True(ContextCompaction.IsActive(session));
    }

    [Fact]
    public void The_transcript_clips_tool_output_and_folds_in_the_previous_summary()
    {
        var session = Chat(5);
        var first = ContextCompaction.Plan(session)!;
        var transcript = ContextCompaction.Transcript(session, first);
        Assert.DoesNotContain(new string('x', 2000), transcript, StringComparison.Ordinal);
        Assert.Contains("Assistant called capture_screenshot", transcript, StringComparison.Ordinal);

        ContextCompaction.Apply(session, first, "OLD-SUMMARY");
        session.ApiMessages.Add(User("question 6"));
        session.ApiMessages.Add(Assistant("answer 6"));
        session.Messages.Add(new ChatDisplayMessage { Id = "u6", Role = "user", Text = "question 6" });
        session.Messages.Add(new ChatDisplayMessage { Id = "a6", Role = "assistant", Text = "answer 6" });

        var second = ContextCompaction.Plan(session)!;
        var again = ContextCompaction.Transcript(session, second);
        Assert.StartsWith("PREVIOUS SUMMARY:", again, StringComparison.Ordinal);
        Assert.Contains("OLD-SUMMARY", again, StringComparison.Ordinal);
        Assert.DoesNotContain("question 1", again, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gauge_weighs_the_summary_instead_of_the_old_messages()
    {
        var session = Chat(6);
        foreach (var message in session.ApiMessages.Where(message => message.Role == "user").ToList())
        {
            var index = session.ApiMessages.IndexOf(message);
            session.ApiMessages[index] = User(new string('q', 4000));
        }

        var before = ContextGauge.Measure(session, "", (VeniceModelInfo?)null).Used;
        ContextCompaction.Apply(session, ContextCompaction.Plan(session)!, "short");
        var after = ContextGauge.Measure(session, "", (VeniceModelInfo?)null).Used;

        Assert.True(after < before, $"{after} should be less than {before}");
    }

    [Theory]
    [InlineData("This model's maximum context length is 128000 tokens", true)]
    [InlineData("error: context_length_exceeded", true)]
    [InlineData("Prompt is too long", true)]
    [InlineData("Insufficient balance", false)]
    [InlineData(null, false)]
    public void An_overflow_is_recognised_by_the_providers_wording(string? error, bool overflow) =>
        Assert.Equal(overflow, ContextCompaction.IsContextOverflow(error));

    [Fact]
    public void Long_tool_output_is_known_to_reach_the_model_cut()
    {
        Assert.True(ChatToolPreview.IsTruncatedForApi(ToolResult.Ok(new string('a', ChatToolPreview.ApiLimit + 1))));
        Assert.False(ChatToolPreview.IsTruncatedForApi(ToolResult.Ok("short")));
    }
}
