using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Отказ чата — вызовом i_refuse (1.36.0): ответ выбрасывается, и задачу берёт модель сильнее —
/// тяжёлая, затем обход. Отказ человек видит, только когда моделей сильнее не осталось.
/// </summary>
public sealed class ChatRefusalTests
{
    private const string Lite = "model-lite";
    private const string Heavy = "model-heavy";
    private const string Bypass = "model-bypass";

    [Fact]
    public async Task A_model_picked_by_hand_climbs_to_heavy_then_to_bypass_and_its_refusals_vanish()
    {
        var streamed = new List<string>();
        var (engine, session) = Engine(Refusing(streamed, refuse: [Lite, Heavy]), Settings());
        session.SelectedModelId = Lite;
        var observer = new TextObserver();

        await engine.RunTurnAsync(session, "user request", observer, CancellationToken.None);

        Assert.Equal([Lite, Heavy, Bypass], streamed);
        var assistant = session.Messages.Last(message => message.Role == "assistant");
        Assert.Equal("done", assistant.Text);
        Assert.Equal(Bypass, assistant.ResolvedModelId);
        Assert.Equal(AssistantStatus.Complete, assistant.Status);

        // Отказной ответ не попадает ни в историю модели, ни в пузырь: пузырь очищался перед
        // следующей ступенью.
        var history = JsonSerializer.Serialize(session.ApiMessages);
        Assert.DoesNotContain(Refusals.ToolName, history, StringComparison.Ordinal);
        Assert.DoesNotContain("refusing-text", history, StringComparison.Ordinal);
        Assert.DoesNotContain("refusing-text", assistant.Text, StringComparison.Ordinal);
        Assert.Contains("", observer.Texts);
    }

    [Fact]
    public async Task Under_auto_a_refusing_lite_model_hands_the_turn_to_heavy()
    {
        var streamed = new List<string>();
        var (engine, session) = Engine(Refusing(streamed, refuse: [Lite], router: "lite"), Settings("auto"));

        await engine.RunTurnAsync(session, "user request", new TextObserver(), CancellationToken.None);

        Assert.Equal([Lite, Heavy], streamed);
        Assert.Equal("done", session.Messages.Last(message => message.Role == "assistant").Text);
    }

    [Fact]
    public async Task When_no_stronger_model_is_left_the_user_gets_the_refusal_of_the_last_one()
    {
        var streamed = new List<string>();
        var (engine, session) = Engine(Refusing(streamed, refuse: [Lite, Heavy, Bypass]), Settings());
        session.SelectedModelId = Lite;

        await engine.RunTurnAsync(session, "user request", new TextObserver(), CancellationToken.None);

        Assert.Equal([Lite, Heavy, Bypass], streamed);
        var expected = Refusals.FinalText(Bypass, "the-reasons");
        var assistant = session.Messages.Last(message => message.Role == "assistant");
        Assert.Equal(expected, assistant.Text);
        Assert.Equal(AssistantStatus.Complete, assistant.Status);

        // В истории модели — обычный текст отказа, без вызова, который ждал бы ответа.
        var last = session.ApiMessages[^1];
        Assert.Equal("assistant", last.Role);
        Assert.Null(last.ToolCalls);
        Assert.Equal(expected, ChatContent.ReadText(last.Content));
    }

    [Fact]
    public async Task A_step_with_the_same_model_is_skipped()
    {
        var streamed = new List<string>();
        var settings = Settings();
        settings.HeavyModelId = Lite;
        var (engine, session) = Engine(Refusing(streamed, refuse: [Lite]), settings);
        session.SelectedModelId = Lite;

        await engine.RunTurnAsync(session, "user request", new TextObserver(), CancellationToken.None);

        Assert.Equal([Lite, Bypass], streamed);
    }

    [Theory]
    [InlineData(Lite, Heavy)]
    [InlineData("model-picked-by-hand", Heavy)]
    [InlineData(Heavy, Bypass)]
    [InlineData(Bypass, null)]
    public void The_ladder_goes_heavy_then_bypass(string refused, string? next)
    {
        var (engine, _) = Engine(Refusing([], refuse: []), Settings());

        Assert.Equal(next, engine.NextAfterRefusal(refused)?.ModelId);
    }

    [Fact]
    public void The_refusal_tool_is_offered_to_the_chat_model()
    {
        var (engine, _) = Engine(Refusing([], refuse: []), Settings());

        Assert.Contains(engine.ToolsFor([]), tool => tool.Function.Name == Refusals.ToolName);
    }

    private static AppSettings Settings(string chat = Lite)
    {
        var settings = AppSettings.CreateDefault();
        settings.ChatModelId = chat;
        settings.LiteModelId = Lite;
        settings.HeavyModelId = Heavy;
        settings.BypassModelId = Bypass;
        settings.RouterModelId = Lite;
        return settings;
    }

    private static (ChatEngine Engine, ChatSession Session) Engine(ScriptedHandler handler, AppSettings settings)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = Lite, MaxToolRounds = 4 };
        return (
            new ChatEngine(new VeniceClient(http, options), options, () => settings, new ToolRegistry([])),
            new ChatSession { Id = "s1", SelectedModelId = settings.ChatModelId });
    }

    /// <summary>Модели из <paramref name="refuse"/> отказываются вызовом, остальные отвечают «done».</summary>
    private static ScriptedHandler Refusing(List<string> streamed, string[] refuse, string router = "lite") =>
        new(body =>
        {
            if (!body.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                return Json("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"" + router + "\"}}]}");
            }

            using var json = JsonDocument.Parse(body);
            var model = json.RootElement.GetProperty("model").GetString() ?? "";
            streamed.Add(model);
            return refuse.Contains(model)
                ? Stream(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"refusing-text\"}}]}\n" +
                    "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"" +
                    Refusals.ToolName + "\",\"arguments\":" + JsonSerializer.Serialize("{\"reasons\":\"the-reasons\"}") + "}}]}}]}\n" +
                    "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n" +
                    "data: [DONE]\n")
                : Stream(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"done\"}}]}\n" +
                    "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n" +
                    "data: [DONE]\n");
        });

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Stream(string payload) =>
        new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "text/event-stream") };

    private sealed class ScriptedHandler(Func<string, HttpResponseMessage> script) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return script(body);
        }
    }

    private sealed class TextObserver : IChatTurnObserver
    {
        public List<string> Texts { get; } = [];

        public void OnUserAdded(ChatDisplayMessage message) { }

        public void OnAssistantStarted(ChatDisplayMessage message) { }

        public void OnAssistantText(ChatDisplayMessage message)
        {
            lock (Texts)
            {
                Texts.Add(message.Text ?? "");
            }
        }

        public void OnToolsChanged(ChatDisplayMessage message) { }

        public void OnAssistantCompleted(ChatDisplayMessage message) { }

        public void OnAssistantCancelled(ChatDisplayMessage message) { }

        public void OnUserAppended(ChatDisplayMessage message) { }

        public void OnError(string message) { }
    }
}
