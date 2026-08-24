using Amarin.Core;

namespace Amarin.AdminAI.Tests;

public sealed class SessionHistoryCompressorTests
{
    [Fact]
    public void ToSingleLine_collapses_whitespace_without_quadratic_replace()
    {
        var text = "  hello\r\n\nworld\t\t  again  ";
        var line = SessionHistoryCompressor.ToSingleLine(text, 220);
        Assert.Equal("hello world again", line);
    }

    [Fact]
    public void ToSingleLine_truncates_and_collapses_long_space_runs()
    {
        var text = new string(' ', 50_000) + "ok" + new string(' ', 50_000);
        var line = SessionHistoryCompressor.ToSingleLine(text, 10);
        Assert.Equal("ok", line);
    }

    [Fact]
    public void Compress_shortens_old_tool_messages()
    {
        var history = new List<ChatMessage>(12);
        for (var i = 0; i < 12; i++)
        {
            history.Add(new ChatMessage
            {
                Role = "tool",
                Name = "system_info",
                ToolCallId = $"c{i}",
                Content = ChatContent.Text("line1\r\nline2    line3")
            });
        }

        var compressed = SessionHistoryCompressor.Compress(history);
        Assert.Equal(12, compressed.Count);

        var old = ChatContent.ReadText(compressed[0].Content);
        Assert.NotNull(old);
        Assert.DoesNotContain('\n', old);
        Assert.Contains("[OK] system_info:", old, StringComparison.Ordinal);
        Assert.Contains("line1 line2 line3", old, StringComparison.Ordinal);
    }
}
