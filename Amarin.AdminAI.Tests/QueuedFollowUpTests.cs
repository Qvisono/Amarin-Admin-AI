using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// A line typed while the model is still working. It has to land in the transcript in a place the
/// API will accept — on a round boundary, never between an assistant's tool_calls and the tool
/// replies that answer them — and it has to land exactly once.
/// </summary>
public sealed class QueuedFollowUpTests
{
    private static AgentOptions Options() => new()
    {
        ApiKey = "test",
        BaseUrl = "https://api.venice.ai/api/v1",
        Model = "grok-4-6",
        MaxToolRounds = 4
    };

    private static ChatEngine Engine(HttpMessageHandler handler, out HttpClient http)
    {
        var options = Options();
        http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var settings = AppSettings.CreateDefault();
        return new ChatEngine(new VeniceClient(http, options), options, () => settings,
            new ToolRegistry([new StubTool()]));
    }

    [Fact]
    public async Task A_follow_up_lands_on_the_round_boundary_and_only_once()
    {
        var round = 0;
        var observer = new QueueingObserver();

        // Queued once, while the first round's tool was running — the realistic case. Once,
        // because OnToolsChanged fires several times per round (created, running, finished) and a
        // person types the line one time.
        var typed = 0;
        observer.OnToolsChangedCallback = () =>
        {
            if (Interlocked.Increment(ref typed) == 1)
            {
                observer.Enqueue("стой, посмотри лучше диск D");
            }
        };

        var handler = new ScriptedHandler(_ => Interlocked.Increment(ref round) == 1
            ? SseWithToolCall("Смотрю.", "read_file")
            : Sse("Готово."));

        var engine = Engine(handler, out var http);
        using (http)
        {
            var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
            await engine.RunTurnAsync(session, "проверь диск C", observer, CancellationToken.None);

            var roles = session.ApiMessages.Select(m => m.Role).ToList();

            // user → assistant(tool_calls) → tool → user(follow-up) → assistant
            Assert.Equal(["user", "assistant", "tool", "user", "assistant"], roles);

            var texts = session.ApiMessages
                .Select(m => ChatContent.ReadText(m.Content) ?? "")
                .ToList();
            Assert.Equal("стой, посмотри лучше диск D", texts[3]);
            Assert.Single(texts, t => t == "стой, посмотри лучше диск D");
        }
    }

    [Fact]
    public async Task A_follow_up_never_splits_a_tool_call_from_its_reply()
    {
        var observer = new QueueingObserver();
        var typed = 0;
        observer.OnToolsChangedCallback = () =>
        {
            if (Interlocked.Increment(ref typed) == 1)
            {
                observer.Enqueue("и ещё вот это");
            }
        };

        var round = 0;
        var handler = new ScriptedHandler(_ => Interlocked.Increment(ref round) == 1
            ? SseWithToolCall("", "read_file")
            : Sse("Готово."));

        var engine = Engine(handler, out var http);
        using (http)
        {
            var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
            await engine.RunTurnAsync(session, "начни", observer, CancellationToken.None);

            // Every assistant message carrying tool_calls must be followed immediately by tool
            // replies; a user message wedged in there is rejected by the API outright.
            for (var i = 0; i < session.ApiMessages.Count; i++)
            {
                if (session.ApiMessages[i].ToolCalls is { Count: > 0 })
                {
                    Assert.Equal("tool", session.ApiMessages[i + 1].Role);
                }
            }
        }
    }

    [Fact]
    public async Task A_follow_up_typed_after_the_answer_gets_an_answer_of_its_own()
    {
        var observer = new QueueingObserver();
        var answered = 0;

        // Nothing is queued until the first answer has completed - the "typed while it was
        // streaming the final reply" case, which the round loop has already passed by.
        observer.OnAssistantCompletedCallback = () =>
        {
            if (Interlocked.Increment(ref answered) == 1)
            {
                observer.Enqueue("а теперь то же для диска D");
            }
        };

        var handler = new ScriptedHandler(_ => Sse("Готово."));
        var engine = Engine(handler, out var http);
        using (http)
        {
            var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
            await engine.RunTurnAsync(session, "проверь диск C", observer, CancellationToken.None);

            Assert.Equal(2, session.Messages.Count(m => m.Role == "assistant"));
            Assert.Equal(["user", "assistant", "user", "assistant"],
                session.ApiMessages.Select(m => m.Role));
        }
    }

    [Fact]
    public async Task An_observer_that_knows_nothing_about_queues_still_works()
    {
        // The interface member has a default implementation precisely so that every existing
        // observer — the silent ones in these tests above all — keeps compiling and behaving.
        var handler = new ScriptedHandler(_ => Sse("Готово."));
        var engine = Engine(handler, out var http);
        using (http)
        {
            var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
            await engine.RunTurnAsync(session, "привет", new PlainObserver(), CancellationToken.None);

            Assert.Equal(["user", "assistant"], session.ApiMessages.Select(m => m.Role));
        }
    }

    [Fact]
    public void The_turn_hands_out_queued_lines_in_the_order_they_were_typed()
    {
        var turn = new RunningTurn
        {
            Session = new ChatSession { Id = "s" },
            Cancellation = new CancellationTokenSource(),
            Kind = TurnKind.Send,
            StartedAt = DateTime.Now
        };

        Assert.False(turn.HasQueued);
        turn.Enqueue("первое");
        turn.Enqueue("   ");
        turn.Enqueue("второе");

        Assert.True(turn.HasQueued);
        Assert.True(turn.TryTakeQueued(out var first));
        Assert.True(turn.TryTakeQueued(out var second));
        Assert.Equal("первое", first);
        Assert.Equal("второе", second);
        Assert.False(turn.TryTakeQueued(out _));
    }

    // ───────────────────────── helpers ─────────────────────────

    private static HttpResponseMessage Sse(string text)
    {
        var payload =
            "data: {\"model\":\"grok-4-6\",\"choices\":[{\"delta\":{\"content\":" +
            JsonSerializer.Serialize(text) + "}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n" +
            "data: [DONE]\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }

    private static HttpResponseMessage SseWithToolCall(string text, string tool)
    {
        var payload =
            "data: {\"model\":\"grok-4-6\",\"choices\":[{\"delta\":{\"content\":" +
            JsonSerializer.Serialize(text) + "}}]}\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\"," +
            "\"function\":{\"name\":\"" + tool + "\",\"arguments\":\"{}\"}}]}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n" +
            "data: [DONE]\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> script)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(script(request));
    }

    private sealed class StubTool : ITool
    {
        public string Name => "read_file";

        public string Description => "stub";

        public JsonElement ParametersSchema =>
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        public Task<ToolResult> ExecuteAsync(
            JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Ok("содержимое"));
    }

    /// <summary>Stands in for the window: holds a queue and lets a test fill it at a chosen moment.</summary>
    private sealed class QueueingObserver : IChatTurnObserver
    {
        private readonly Queue<string> _queued = new();

        public Action? OnToolsChangedCallback { get; set; }

        public Action? OnAssistantCompletedCallback { get; set; }

        public void Enqueue(string text) => _queued.Enqueue(text);

        public void OnUserAppended(ChatDisplayMessage user)
        {
        }

        public void OnAssistantStarted(ChatDisplayMessage assistant)
        {
        }

        public void OnAssistantText(ChatDisplayMessage assistant)
        {
        }

        public void OnToolsChanged(ChatDisplayMessage assistant) => OnToolsChangedCallback?.Invoke();

        public void OnAssistantCompleted(ChatDisplayMessage assistant) =>
            OnAssistantCompletedCallback?.Invoke();

        public void OnAssistantCancelled(ChatDisplayMessage assistant)
        {
        }

        public void OnError(string message)
        {
        }

        public bool TryTakeQueuedMessage(out string text)
        {
            if (_queued.Count == 0)
            {
                text = "";
                return false;
            }

            text = _queued.Dequeue();
            return true;
        }
    }

    /// <summary>An observer written before follow-ups existed: it does not override the new member.</summary>
    private sealed class PlainObserver : IChatTurnObserver
    {
        public void OnUserAppended(ChatDisplayMessage user)
        {
        }

        public void OnAssistantStarted(ChatDisplayMessage assistant)
        {
        }

        public void OnAssistantText(ChatDisplayMessage assistant)
        {
        }

        public void OnToolsChanged(ChatDisplayMessage assistant)
        {
        }

        public void OnAssistantCompleted(ChatDisplayMessage assistant)
        {
        }

        public void OnAssistantCancelled(ChatDisplayMessage assistant)
        {
        }

        public void OnError(string message)
        {
        }
    }
}
