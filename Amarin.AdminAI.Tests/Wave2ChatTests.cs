using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

public sealed class Wave2ChatTests
{
    [Fact]
    public void Stream_accumulator_joins_text_and_tool_call_fragments()
    {
        var acc = new ChatStreamAccumulator();
        acc.Apply(ParseChunk("""{"choices":[{"delta":{"content":"Привет"}}]}"""));
        acc.Apply(ParseChunk("""{"choices":[{"delta":{"content":", мир"}}]}"""));
        acc.Apply(ParseChunk(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","type":"function","function":{"name":"read_","arguments":"{\"p\":"}}]}}]}"""));
        acc.Apply(ParseChunk(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"file","arguments":"1}"}}]}}]}"""));
        acc.Apply(ParseChunk("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}],"cost":{"usd":0.0123,"diem":0}}"""));

        Assert.Equal("Привет, мир", acc.Text);
        Assert.Equal("tool_calls", acc.FinishReason);
        Assert.Equal(0.0123m, acc.Cost.Usd);
        var calls = acc.BuildToolCalls();
        Assert.Single(calls);
        Assert.Equal("c1", calls[0].Id);
        Assert.Equal("read_file", calls[0].Function.Name);
        Assert.Equal("{\"p\":1}", calls[0].Function.Arguments);
    }

    [Fact]
    public void Chat_format_matches_header_spec()
    {
        Assert.Equal("4s", ChatFormat.Duration(TimeSpan.FromSeconds(4)));
        Assert.Equal("3m4s", ChatFormat.Duration(TimeSpan.FromSeconds(184)));
        // Подпись переводится, а тест живёт вне WPF-коллекции и языком не управляет —
        // поэтому здесь проверяется только число: 3,2 секунды это «3», а не «4».
        var working = ChatFormat.Working(TimeSpan.FromSeconds(3.2));
        Assert.Contains("3", working, StringComparison.Ordinal);
        Assert.DoesNotContain("4", working, StringComparison.Ordinal);
        Assert.Equal("$0,0236", ChatFormat.Cost(new VeniceCost { Usd = 0.0236m, HasData = true }));
        Assert.Equal("", ChatFormat.Cost(VeniceCost.Zero));
        Assert.Equal("07:01", ChatFormat.Clock(new DateTime(2026, 8, 30, 7, 1, 0)));
    }

    [Fact]
    public void Stream_flag_is_written_only_when_true()
    {
        var streamed = new ChatCompletionRequest
        {
            Model = "grok-4-6",
            Messages = [new ChatMessage { Role = "user", Content = ChatContent.Text("hi") }],
            Stream = true,
            VeniceParameters = new VeniceParameters { IncludeVeniceSystemPrompt = false }
        };
        var json = System.Text.Encoding.UTF8.GetString(
            JsonSerializer.SerializeToUtf8Bytes(streamed, VeniceJsonContext.Default.ChatCompletionRequest));
        Assert.Contains("\"stream\":true", json, StringComparison.Ordinal);

        var regular = new ChatCompletionRequest
        {
            Model = "grok-4-6",
            Messages = [new ChatMessage { Role = "user", Content = ChatContent.Text("hi") }],
            VeniceParameters = new VeniceParameters { IncludeVeniceSystemPrompt = false }
        };
        var regularJson = System.Text.Encoding.UTF8.GetString(
            JsonSerializer.SerializeToUtf8Bytes(regular, VeniceJsonContext.Default.ChatCompletionRequest));
        Assert.DoesNotContain("\"stream\"", regularJson, StringComparison.Ordinal);
    }

    private static ChatCompletionChunk ParseChunk(string json) =>
        JsonSerializer.Deserialize(json, VeniceJsonContext.Default.ChatCompletionChunk)
        ?? throw new InvalidOperationException("chunk");
}
