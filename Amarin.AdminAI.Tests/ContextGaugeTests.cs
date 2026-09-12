using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The context ring is the only thing telling the user the model is about to start forgetting, so
/// the two ways it can lie are the ones worth pinning: reading zero when a number is known, and
/// reading a number that went stale several messages ago.
/// </summary>
public sealed class ContextGaugeTests
{
    private static ChatMessage User(string text) =>
        new() { Role = "user", Content = ChatContent.Text(text) };

    private static VeniceModelInfo Model(int? contextLength = null, int? available = null) =>
        new()
        {
            Id = "test",
            ContextLength = contextLength,
            ModelSpec = available is null ? null : new VeniceModelSpec { AvailableContextTokens = available }
        };

    [Fact]
    public void Without_a_measurement_the_whole_conversation_is_estimated()
    {
        var session = new ChatSession();
        session.ApiMessages.Add(User(new string('a', 400)));

        var usage = ContextGauge.Measure(session, new string('b', 400), Model(contextLength: 1000));

        // 800 characters at four per token, and the system prompt counts: it is really sent.
        Assert.Equal(200, usage.Used);
        Assert.Equal(1000, usage.Max);
        Assert.True(usage.IsEstimate);
        Assert.Equal(0.2, usage.Fraction, 3);
    }

    [Fact]
    public void A_measurement_is_used_verbatim_while_nothing_has_been_added()
    {
        var session = new ChatSession { LastPromptTokens = 5000, LastPromptTokensApiIndex = 1 };
        session.ApiMessages.Add(User("уже учтено"));

        var usage = ContextGauge.Measure(session, "системный промпт", Model(available: 20_000));

        Assert.Equal(5000, usage.Used);
        Assert.False(usage.IsEstimate);
    }

    [Fact]
    public void Only_the_messages_added_since_are_estimated_on_top()
    {
        var session = new ChatSession { LastPromptTokens = 5000, LastPromptTokensApiIndex = 1 };
        session.ApiMessages.Add(User("уже учтено"));
        session.ApiMessages.Add(User(new string('x', 800)));

        var usage = ContextGauge.Measure(session, "системный промпт", Model(available: 20_000));

        // The measured 5000 plus 200 for the new message - the old one is not counted twice, and
        // the system prompt is already inside the measurement.
        Assert.Equal(5200, usage.Used);
        Assert.True(usage.IsEstimate);
    }

    [Fact]
    public void Tool_call_arguments_are_counted_as_the_context_they_are()
    {
        var session = new ChatSession { LastPromptTokens = 100, LastPromptTokensApiIndex = 0 };
        session.ApiMessages.Add(new ChatMessage
        {
            Role = "assistant",
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "1",
                    Function = new FunctionCall { Name = "run", Arguments = new string('j', 396) }
                }
            ]
        });

        var usage = ContextGauge.Measure(session, "", Model(available: 10_000));

        Assert.Equal(200, usage.Used);
    }

    [Fact]
    public void The_spec_limit_wins_over_the_advertised_one()
    {
        var usage = ContextGauge.Measure(new ChatSession(), "", Model(contextLength: 8000, available: 6000));

        Assert.Equal(6000, usage.Max);
    }

    [Fact]
    public void An_unknown_ceiling_leaves_the_ring_empty_rather_than_full()
    {
        var session = new ChatSession { LastPromptTokens = 9999, LastPromptTokensApiIndex = 0 };

        var usage = ContextGauge.Measure(session, "", model: null);

        Assert.Equal(0, usage.Max);
        Assert.Equal(0, usage.Fraction);
        Assert.False(usage.HasScale);
    }

    [Fact]
    public void A_measurement_taken_past_the_end_of_a_trimmed_history_does_not_read_backwards()
    {
        // Rolling a turn back shortens ApiMessages below the index the count was taken at.
        var session = new ChatSession { LastPromptTokens = 5000, LastPromptTokensApiIndex = 12 };
        session.ApiMessages.Add(User("что осталось"));

        var usage = ContextGauge.Measure(session, "", Model(available: 10_000));

        Assert.Equal(5000, usage.Used);
        Assert.False(usage.IsEstimate);
    }

    [Fact]
    public void Fraction_never_leaves_the_dial()
    {
        var session = new ChatSession { LastPromptTokens = 50_000, LastPromptTokensApiIndex = 0 };

        Assert.Equal(1, ContextGauge.Measure(session, "", Model(available: 1000)).Fraction);
    }

    [Fact]
    public void Usage_is_read_off_a_trailing_chunk_that_carries_no_choices()
    {
        // This is the shape Venice actually sends last, and reading it after picking a choice out
        // of the list - which is the obvious way to write it - silently drops the number.
        var chunk = JsonSerializer.Deserialize(
            """{"choices":[],"usage":{"prompt_tokens":4321,"completion_tokens":10,"total_tokens":4331}}""",
            VeniceJsonContext.Default.ChatCompletionChunk)!;

        var accumulator = new ChatStreamAccumulator();
        var added = accumulator.Apply(chunk);

        Assert.False(added);
        Assert.Equal(4321, accumulator.PromptTokens);
        Assert.Equal(4331, accumulator.TotalTokens);
    }

    [Fact]
    public void A_stream_without_usage_reports_nothing_rather_than_zero_tokens()
    {
        var chunk = JsonSerializer.Deserialize(
            """{"choices":[{"delta":{"content":"привет"}}]}""",
            VeniceJsonContext.Default.ChatCompletionChunk)!;

        var accumulator = new ChatStreamAccumulator();
        accumulator.Apply(chunk);

        Assert.Equal(0, accumulator.PromptTokens);
        Assert.Equal("привет", accumulator.Text);
    }

    [Fact]
    public void Stream_options_ride_with_the_stream_and_never_without_it()
    {
        var streamed = JsonSerializer.Serialize(
            new ChatCompletionRequest
            {
                Model = "m",
                Messages = [],
                Stream = true,
                StreamOptions = new StreamOptions()
            },
            VeniceJsonContext.Default.ChatCompletionRequest);

        var plain = JsonSerializer.Serialize(
            new ChatCompletionRequest { Model = "m", Messages = [] },
            VeniceJsonContext.Default.ChatCompletionRequest);

        Assert.Contains("\"stream_options\":{\"include_usage\":true}", streamed, StringComparison.Ordinal);
        Assert.DoesNotContain("stream_options", plain, StringComparison.Ordinal);
    }
    [Fact]
    public void Auto_reads_against_the_smaller_of_the_two_models_it_can_pick()
    {
        // «Авто» — не модель, а просьба выбрать её, и в каталоге его нет: Find возвращал null,
        // потолок получался нулевой, и кольцо показывало прочерк в каждом чате, где выбрано «Авто» —
        // и заметнее всего это было в новых, где другого числа взять ещё негде.
        var session = new ChatSession();
        session.ApiMessages.Add(User(new string('a', 400)));

        var usage = ContextGauge.Measure(
            session, "", [Model(contextLength: 200_000), Model(contextLength: 32_000)]);

        // Меньший из двух: кольцо предупреждает о переполнении и ошибаться обязано в сторону осторожности.
        Assert.Equal(32_000, usage.Max);
        Assert.True(usage.HasScale);
        Assert.True(usage.IsFloor);
    }

    [Fact]
    public void One_known_model_is_not_a_worst_case()
    {
        var usage = ContextGauge.Measure(new ChatSession(), "проба", [Model(contextLength: 32_000), null]);

        Assert.Equal(32_000, usage.Max);
        Assert.False(usage.IsFloor);
    }

}
