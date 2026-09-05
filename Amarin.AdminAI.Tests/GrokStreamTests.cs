using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Regression cover for grok-4-6 in the WPF chat: the model streams its chain of thought in
/// <c>reasoning_content</c> and only then fills <c>content</c>, and intermittently stops after
/// the reasoning with nothing else. Chunks below are trimmed copies of real Venice SSE frames.
/// </summary>
public sealed class GrokStreamTests
{
    private static ChatCompletionChunk Chunk(string json) =>
        JsonSerializer.Deserialize(json, VeniceJsonContext.Default.ChatCompletionChunk)!;

    [Fact]
    public void Reasoning_chunks_do_not_leak_into_the_answer()
    {
        var accumulator = new ChatStreamAccumulator();

        accumulator.Apply(Chunk("""{"choices":[{"index":0,"delta":{"reasoning_content":"The user"}}]}"""));
        accumulator.Apply(Chunk("""{"choices":[{"index":0,"delta":{"reasoning_content":" said hi."}}]}"""));
        accumulator.Apply(Chunk("""{"choices":[{"index":0,"delta":{"role":"assistant"}}]}"""));
        accumulator.Apply(Chunk("""{"choices":[{"index":0,"delta":{"content":"Hello","reasoning_content":null}}]}"""));
        accumulator.Apply(Chunk("""{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""));

        Assert.Equal("Hello", accumulator.Text);
        Assert.Equal("The user said hi.", accumulator.ReasoningText);
        Assert.Equal("stop", accumulator.FinishReason);
    }

    [Fact]
    public void Only_content_chunks_count_as_streamed_text()
    {
        var accumulator = new ChatStreamAccumulator();

        var reasoningAdded = accumulator.Apply(
            Chunk("""{"choices":[{"index":0,"delta":{"reasoning_content":"thinking"}}]}"""));
        var contentAdded = accumulator.Apply(
            Chunk("""{"choices":[{"index":0,"delta":{"content":"answer"}}]}"""));

        // Apply returns "did the visible answer grow" — reasoning must not trigger a UI repaint.
        Assert.False(reasoningAdded);
        Assert.True(contentAdded);
    }

    [Fact]
    public void Encrypted_reasoning_blob_is_stripped()
    {
        var accumulator = new ChatStreamAccumulator();

        accumulator.Apply(Chunk("""{"choices":[{"index":0,"delta":{"reasoning_content":"Short thought."}}]}"""));
        accumulator.Apply(Chunk(
            """{"choices":[{"index":0,"delta":{"reasoning_content":"\n\n__ENCRYPTED_REASONING__bHifXy/G7xITWJ4XP+qjQ","reasoning_encrypted":true}}]}"""));

        Assert.Equal("Short thought.", accumulator.ReasoningText);
        Assert.DoesNotContain("__ENCRYPTED_REASONING__", accumulator.ReasoningText, StringComparison.Ordinal);
        Assert.DoesNotContain("bHifXy", accumulator.ReasoningText, StringComparison.Ordinal);
    }

    [Fact]
    public void All_reasoning_and_no_content_leaves_the_answer_empty_but_keeps_the_reasoning()
    {
        // The reproduced failure: grok-4-6 burns the whole budget thinking, then stops.
        var accumulator = new ChatStreamAccumulator();

        accumulator.Apply(Chunk("""{"choices":[{"index":0,"delta":{"reasoning_content":"I should answer"}}]}"""));
        accumulator.Apply(Chunk("""{"choices":[{"index":0,"delta":{"role":"assistant"}}]}"""));
        accumulator.Apply(Chunk("""{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""));

        Assert.Equal("", accumulator.Text);
        Assert.Equal("stop", accumulator.FinishReason);
        Assert.Equal("I should answer", accumulator.ReasoningText);
        Assert.Empty(accumulator.BuildToolCalls());
    }

    [Fact]
    public void Tool_call_split_across_chunks_is_reassembled()
    {
        // Venice sends the id and name once, then argument fragments with neither.
        var accumulator = new ChatStreamAccumulator();

        accumulator.Apply(Chunk(
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"read_file","arguments":""}}]}}]}"""));
        accumulator.Apply(Chunk(
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"type":"function","function":{"arguments":"{\"path\":\"C:\\\\test.txt\"}"}}]}}]}"""));

        var calls = accumulator.BuildToolCalls();
        var call = Assert.Single(calls);
        Assert.Equal("call-1", call.Id);
        Assert.Equal("read_file", call.Function.Name);
        Assert.Equal("""{"path":"C:\\test.txt"}""", call.Function.Arguments);
    }
}
