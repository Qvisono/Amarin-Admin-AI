using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// URL parsing and WebVTT clean-up — the two halves of the transcript tool that do not need
/// yt-dlp on the machine running the tests.
/// </summary>
public sealed class YouTubeTranscriptTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ&t=42s", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("  dQw4w9WgXcQ  ", "dQw4w9WgXcQ")]
    public void Recognises_the_shapes_a_youtube_link_comes_in(string input, string expected) =>
        Assert.Equal(expected, YouTubeTranscriptTool.TryReadVideoId(input));

    [Theory]
    [InlineData("https://vimeo.com/12345")]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("not a url at all")]
    [InlineData("")]
    [InlineData("file:///C:/Windows/System32")]
    [InlineData("https://www.youtube.com/watch?v=tooshort")]
    public void Refuses_anything_that_is_not_a_youtube_video(string input) =>
        Assert.Null(YouTubeTranscriptTool.TryReadVideoId(input));

    [Fact]
    public void Vtt_becomes_plain_prose_without_timestamps_or_markup()
    {
        const string Vtt = """
            WEBVTT
            Kind: captions
            Language: ru

            00:00:00.120 --> 00:00:02.480 align:start position:0%
            <c.colorE5E5E5>Привет</c>, сегодня разберём

            00:00:02.480 --> 00:00:05.000
            Привет, сегодня разберём
            одну интересную тему

            00:00:05.000 --> 00:00:07.240
            <00:00:05.500><c>одну интересную тему</c>
            """;

        var text = YouTubeTranscriptTool.ParseVtt(Vtt);

        Assert.DoesNotContain("-->", text, StringComparison.Ordinal);
        Assert.DoesNotContain("WEBVTT", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<", text, StringComparison.Ordinal);
        Assert.DoesNotContain("align:start", text, StringComparison.Ordinal);
        Assert.Contains("Привет, сегодня разберём", text, StringComparison.Ordinal);
        Assert.Contains("одну интересную тему", text, StringComparison.Ordinal);

        // Rolling captions repeat themselves; the same line must not come out twice.
        var occurrences = text.Split("одну интересную тему").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Empty_subtitles_produce_empty_text() =>
        Assert.Equal("", YouTubeTranscriptTool.ParseVtt("WEBVTT\n\n"));

    [Fact]
    public async Task A_bad_link_is_refused_before_any_process_is_started()
    {
        var result = await YouTubeTranscriptTool.FetchTranscriptAsync("https://vimeo.com/12345");

        Assert.False(result.Success);
        Assert.Equal("", result.Text);
        Assert.Contains("YouTube", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_missing_yt_dlp_message_says_how_to_install_it()
    {
        // yt-dlp is the one external dependency; a bare failure would leave the user stuck.
        Assert.Contains("winget install yt-dlp", YouTubeTranscriptTool.MissingYtDlpMessage, StringComparison.Ordinal);
        Assert.Contains("github.com/yt-dlp", YouTubeTranscriptTool.MissingYtDlpMessage, StringComparison.Ordinal);
    }
}
