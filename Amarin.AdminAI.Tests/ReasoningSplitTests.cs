using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// GLM-class models write their chain of thought into <c>content</c> in tags, and Venice does not
/// strip it for them. The markdown renderer runs with HTML disabled, so anything that gets past
/// this class is printed to the user as literal <c>&lt;think&gt;</c> markup.
/// </summary>
public sealed class ReasoningSplitTests
{
    [Fact]
    public void Ordinary_text_passes_through_untouched()
    {
        var (reasoning, answer) = ReasoningSplit.Split("Просто ответ без тегов.");

        Assert.Equal("", reasoning);
        Assert.Equal("Просто ответ без тегов.", answer);
    }

    [Fact]
    public void A_closed_think_block_is_lifted_out_of_the_answer()
    {
        var (reasoning, answer) = ReasoningSplit.Split(
            "<think>Сначала прикину варианты.</think>Ответ: сорок два.");

        Assert.Equal("Сначала прикину варианты.", reasoning);
        Assert.Equal("Ответ: сорок два.", answer);
    }

    [Fact]
    public void An_unclosed_opener_keeps_the_rest_out_of_the_answer()
    {
        // Mid-stream. Showing the tail as the answer would flash the reasoning into the bubble
        // and then yank it back out when the closing tag arrived.
        var (reasoning, answer) = ReasoningSplit.Split("<think>Ещё думаю, вариант пер");

        Assert.Equal("Ещё думаю, вариант пер", reasoning);
        Assert.Equal("", answer);
    }

    [Theory]
    [InlineData("<thinking>ход мысли</thinking>итог")]
    [InlineData("<reasoning>ход мысли</reasoning>итог")]
    [InlineData("<reason>ход мысли</reason>итог")]
    [InlineData("◁think▷ход мысли◁/think▷итог")]
    [InlineData("<THINK>ход мысли</THINK>итог")]
    public void Every_dialect_of_the_tag_is_recognised(string raw)
    {
        var (reasoning, answer) = ReasoningSplit.Split(raw);

        Assert.Equal("ход мысли", reasoning);
        Assert.Equal("итог", answer);
    }

    [Fact]
    public void A_closing_tag_with_no_opener_ends_a_block_that_started_at_the_top()
    {
        // What GLM actually sends: its chat template pre-fills the opening tag into the
        // assistant turn, so the reply comes back already inside the thinking block and the
        // only tag in it is the closing one.
        var (reasoning, answer) = ReasoningSplit.Split(
            "Пользователь спрашивает про сеть. Проверю маршруты.\n</think>\nСеть в порядке.");

        Assert.Equal("Пользователь спрашивает про сеть. Проверю маршруты.", reasoning);
        Assert.Equal("Сеть в порядке.", answer);
    }

    [Fact]
    public void A_matched_pair_is_still_read_as_a_pair()
    {
        // The orphan rule must not hijack the closer of a block that has its own opener.
        var (reasoning, answer) = ReasoningSplit.Split("Вступление.<think>ход</think>Ответ.");

        Assert.Equal("ход", reasoning);
        Assert.Equal("Вступление.Ответ.", answer);
    }

    [Fact]
    public void Several_blocks_are_joined_and_the_prose_between_them_is_kept()
    {
        var (reasoning, answer) = ReasoningSplit.Split(
            "<think>раз</think>Первое.<think>два</think>Второе.");

        Assert.Equal("раз\n\nдва", reasoning);
        Assert.Equal("Первое.Второе.", answer);
    }

    [Fact]
    public void A_lone_angle_bracket_is_not_mistaken_for_a_tag()
    {
        const string code = "if (a < b) { return a; }";

        Assert.Equal(("", code), ReasoningSplit.Split(code));
    }

    private static ChatCompletionChunk Chunk(string? content = null, string? reasoning = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { delta = new { content, reasoning_content = reasoning } }
            }
        });
        return JsonSerializer.Deserialize<ChatCompletionChunk>(json)!;
    }

    [Fact]
    public void The_accumulator_hides_inline_thinking_from_the_streamed_answer()
    {
        var accumulator = new ChatStreamAccumulator();

        var duringThinking = accumulator.Apply(Chunk(content: "<think>прикидываю"));
        accumulator.Apply(Chunk(content: " дальше</think>"));
        var atTheAnswer = accumulator.Apply(Chunk(content: "Готово."));

        Assert.False(duringThinking, "a chunk of pure thinking must not repaint the bubble");
        Assert.True(atTheAnswer);
        Assert.Equal("Готово.", accumulator.Text);
        Assert.Equal("прикидываю дальше", accumulator.InlineReasoning);
    }

    [Fact]
    public void The_reasoning_channel_is_still_kept_off_the_answer()
    {
        var accumulator = new ChatStreamAccumulator();

        accumulator.Apply(Chunk(reasoning: "внутренний ход__ENCRYPTED_REASONING__blob"));
        accumulator.Apply(Chunk(content: "Ответ."));

        Assert.Equal("Ответ.", accumulator.Text);
        Assert.Equal("внутренний ход", accumulator.ReasoningText);
    }

    [Fact]
    public void A_model_that_answers_straight_away_reports_no_thinking_time()
    {
        var accumulator = new ChatStreamAccumulator();

        accumulator.Apply(Chunk(content: "Сразу отвечаю."));

        Assert.Equal(TimeSpan.Zero, accumulator.ThinkingElapsed);
    }
}
