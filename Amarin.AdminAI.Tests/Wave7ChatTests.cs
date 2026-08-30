using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

public sealed class Wave7ChatTests
{
    [Theory]
    [InlineData("Проверка сети", "Проверка сети")]
    [InlineData("  \"Проверка сети\"  ", "Проверка сети")]
    [InlineData("«Ночная сводка».\nещё строка", "Ночная сводка")]
    [InlineData("   ", null)]
    public void Title_sanitizer_strips_quotes_and_extra_lines(string? input, string? expected)
    {
        Assert.Equal(expected, ChatTitle.Sanitize(input));
    }

    [Fact]
    public void Title_sanitizer_truncates_long_line()
    {
        Assert.Equal(24, ChatTitle.MaxLength);
        var raw = string.Join(' ', Enumerable.Repeat("слово", 40));
        var title = ChatTitle.Sanitize(raw);
        Assert.NotNull(title);
        Assert.True(title.Length <= 24);
        Assert.DoesNotContain('\n', title);
        Assert.Equal(24, ChatTitle.Sanitize(new string('я', 25))!.Length);
        Assert.Contains("24", ChatTitle.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_title_helper()
    {
        Assert.True(ChatTitle.IsDefault("Новый чат"));
        Assert.True(ChatTitle.IsDefault(""));
        Assert.False(ChatTitle.IsDefault("Проверка сети"));
    }

    [Fact]
    public void User_bubble_width_shrinks_to_text_not_max()
    {
        var shortWidth = ChatMessageViews.MeasureWrappedWidth("чек сеть", 13.5, 19, 530, 1);
        var hiWidth = ChatMessageViews.MeasureWrappedWidth("Привет", 13.5, 19, 530, 1);
        var longWidth = ChatMessageViews.MeasureWrappedWidth(new string('ж', 400), 13.5, 19, 530, 1);
        Assert.True(shortWidth >= 50 && shortWidth < 180, $"check={shortWidth}");
        Assert.True(hiWidth < 160, $"hi={hiWidth}");
        Assert.True(longWidth >= 500 && longWidth <= 530, $"long={longWidth}");
    }

    [Fact]
    public void Markdown_preview_uses_paragraphs_and_bullets()
    {
        var lines = ChatMarkdown.PreviewLines("Привет\n\n- один\n- два");
        Assert.Equal(["Привет", "• один", "• два"], lines);
    }

    [Fact]
    public void Delete_assistant_removes_the_user_turn_it_answered()
    {
        var session = SampleSession();
        Assert.True(ChatSessionEdit.DeleteAssistantTurn(session, "a2"));
        Assert.Equal(2, session.Messages.Count);
        Assert.Equal("u1", session.Messages[0].Id);
        Assert.Equal("a1", session.Messages[1].Id);
        Assert.Equal(4, session.ApiMessages.Count);
        Assert.Equal("assistant", session.ApiMessages[^1].Role);
        Assert.Equal("ок", ChatContent.ReadText(session.ApiMessages[^1].Content));

        Assert.True(ChatSessionEdit.DeleteAssistantTurn(session, "a1"));
        Assert.Empty(session.Messages);
        Assert.Empty(session.ApiMessages);
    }

    [Fact]
    public void Truncate_from_assistant_keeps_preceding_user_and_api()
    {
        var session = SampleSession();
        Assert.True(ChatSessionEdit.TruncateFromMessage(session, "a1"));
        Assert.Single(session.Messages);
        Assert.Equal("u1", session.Messages[0].Id);
        Assert.Single(session.ApiMessages);
        Assert.Equal("user", session.ApiMessages[0].Role);
        Assert.Equal("первый", ChatContent.ReadText(session.ApiMessages[0].Content));
    }

    [Fact]
    public void Replace_user_text_drops_following_turns()
    {
        var session = SampleSession();
        Assert.True(ChatSessionEdit.ReplaceUserText(session, "u1", "  новая формулировка  "));
        Assert.Single(session.Messages);
        Assert.Equal("новая формулировка", session.Messages[0].Text);
        Assert.Single(session.ApiMessages);
        Assert.Equal("новая формулировка", ChatContent.ReadText(session.ApiMessages[0].Content));
    }

    [Fact]
    public async Task Regenerate_does_not_append_a_second_user()
    {
        var handler = new ScriptedHandler((_, body) =>
        {
            Assert.Contains("\"stream\":true", body, StringComparison.Ordinal);
            return Sse("ответ", 0.01m);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            MaxToolRounds = 2
        };
        var venice = new VeniceClient(http, options);
        var engine = new ChatEngine(venice, options, () => new AppSettings(), new ToolRegistry([]));
        var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
        var observer = new RecordingObserver();

        await engine.RunTurnAsync(session, "привет", observer, CancellationToken.None);
        Assert.Equal(2, session.Messages.Count);
        var assistantId = session.Messages[1].Id;

        Assert.True(ChatSessionEdit.TruncateFromMessage(session, assistantId));
        await engine.GenerateAssistantAsync(session, observer, CancellationToken.None);

        Assert.Equal(2, session.Messages.Count);
        Assert.Equal("user", session.Messages[0].Role);
        Assert.Equal("привет", session.Messages[0].Text);
        Assert.Equal(1, session.ApiMessages.Count(m => m.Role == "user"));
        Assert.Equal("ответ", observer.Assistant?.Text);
    }

    private static ChatSession SampleSession()
    {
        var session = new ChatSession { Id = "s" };
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "первый" });
        session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Id = "a1", Text = "ок" });
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u2", Text = "ещё" });
        session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Id = "a2", Text = "ещё ок" });
        session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text("первый") });
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
        session.ApiMessages.Add(new ChatMessage { Role = "assistant", Content = ChatContent.Text("ок") });
        session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text("ещё") });
        session.ApiMessages.Add(new ChatMessage { Role = "assistant", Content = ChatContent.Text("ещё ок") });
        return session;
    }

    private static HttpResponseMessage Sse(string text, decimal usd)
    {
        var encoded = JsonSerializer.Serialize(text);
        var usdText = usd.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var payload =
            "data: {\"choices\":[{\"delta\":{\"content\":" + encoded + "}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"cost\":{\"usd\":" + usdText + ",\"diem\":0}}\n" +
            "data: [DONE]\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _script;

        public ScriptedHandler(Func<HttpRequestMessage, string, HttpResponseMessage> script) => _script = script;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _script(request, body);
        }
    }

    private sealed class RecordingObserver : IChatTurnObserver
    {
        public ChatDisplayMessage? Assistant { get; private set; }

        public void OnUserAppended(ChatDisplayMessage user)
        {
        }

        public void OnAssistantStarted(ChatDisplayMessage assistant) => Assistant = assistant;

        public void OnAssistantText(ChatDisplayMessage assistant) => Assistant = assistant;

        public void OnToolsChanged(ChatDisplayMessage assistant)
        {
        }

        public void OnAssistantCompleted(ChatDisplayMessage assistant) => Assistant = assistant;

        public void OnAssistantCancelled(ChatDisplayMessage assistant) => Assistant = assistant;

        public void OnError(string message)
        {
        }
    }
}
