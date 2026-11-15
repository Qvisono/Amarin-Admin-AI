using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Пересадка настоящего агента переносит сделанное и не разрывает пары вызовов и ответов.
/// HTTP и инструменты подставные: проверяется отправленный контекст, без сети и доступа к ПК.
/// </summary>
public sealed class AgentSwitchHistoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Switching_during_a_request_preserves_the_work_notes_and_cache_key(bool continuation)
    {
        var registry = new AgentRegistry();
        var agents = new AgentConversations();
        var seeding = continuation;
        var liteRequests = 0;
        var toolRuns = 0;
        var foundData = "already-found-data-" + new string('x', 600) + "-retained-tail";
        using var handler = new Handler((body, token) =>
        {
            if (seeding)
            {
                return Task.FromResult(Json(Final("earlier-report")));
            }

            if (Model(body) == "agent-fast")
            {
                return Task.FromResult(Json(Final("finished-after-switch")));
            }

            if (++liteRequests <= 6)
            {
                return Task.FromResult(Json(Calls(Call("read-" + liteRequests, "read_file"))));
            }

            Assert.Equal(7, liteRequests);
            // Уточнение уже прочитано агентом: пустую очередь перенести недостаточно.
            Assert.Contains("human-note", Text(body), StringComparison.Ordinal);
            Switch(registry);
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The interrupted request must be cancelled.");
        });
        var host = Host(registry, handler,
            new StubTool("read_file", (_, _) =>
            {
                toolRuns++;
                if (toolRuns == 1)
                {
                    Assert.Single(registry.ListFor("chat")).Tell("human-note");
                }

                return Task.FromResult(ToolResult.Ok(foundData));
            }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        if (continuation)
        {
            var seed = await InChat(agents, () => host.RunAsync("original-task", null, timeout.Token));
            Assert.True(seed.Success, seed.Output);
            seeding = false;
        }

        var result = await InChat(agents, () => continuation
            ? host.ContinueAsync("a1", "follow-up-request", timeout.Token)
            : host.RunAsync("original-task", null, timeout.Token));

        Assert.True(result.Success, result.Output);
        Assert.Contains("finished-after-switch", result.Output, StringComparison.Ordinal);
        Assert.Equal(6, toolRuns);
        var sent = Assert.Single(handler.Requests, body => Model(body) == "agent-fast");
        Assert.Contains("original-task", Text(sent), StringComparison.Ordinal);
        Assert.Contains("already-found-data", Text(sent), StringComparison.Ordinal);
        // Пересадка не сжимает уже прочитанный префикс, даже когда история длиннее десяти сообщений.
        Assert.Contains(foundData, ToolReply(sent, "read-1"), StringComparison.Ordinal);
        Assert.Contains("human-note", Text(sent), StringComparison.Ordinal);
        Assert.All(handler.Requests, body =>
            Assert.Equal("chat:a1", body.GetProperty("prompt_cache_key").GetString()));
        AssertPaired(sent);

        if (continuation)
        {
            Assert.Contains("earlier-report", Text(sent), StringComparison.Ordinal);
            Assert.Single(Messages(sent), message =>
                Content(message).Contains("follow-up-request", StringComparison.Ordinal));
        }

        var kept = Assert.IsType<AgentConversation>(agents.Find("a1"));
        Assert.Equal("fast", kept.Tier);
        Assert.Contains(kept.History, message =>
            ChatContent.ReadText(message.Content)?.Contains("already-found-data", StringComparison.Ordinal) == true);
        Assert.Empty(registry.ListFor("chat"));
    }

    [Fact]
    public async Task Switching_during_sequential_tools_keeps_completed_results_and_closes_pending_calls()
    {
        var registry = new AgentRegistry();
        var agents = new AgentConversations();
        var readItems = new List<string>();
        using var handler = new Handler((body, _) => Task.FromResult(Json(Model(body) == "agent-lite"
            ? Calls(
                Call("done-call", "read_file", "{\"item\":\"first\"}"),
                Call("interrupted-call", "browser", "{\"action\":\"read\"}"),
                Call("not-started-call", "read_file", "{\"item\":\"later\"}"))
            : Final("finished-after-switch"))));
        var host = Host(registry, handler,
            new StubTool("read_file", (arguments, _) =>
            {
                readItems.Add(arguments.GetProperty("item").GetString() ?? "");
                return Task.FromResult(ToolResult.WithImage("completed-before-switch", "c3R1Yi1pbWFnZQ=="));
            }),
            // browser не запускается параллельно, но здесь это только подставной инструмент.
            new StubTool("browser", (_, token) =>
            {
                Switch(registry);
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("The interrupted tool must be cancelled.");
            }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await InChat(agents, () => host.RunAsync("original-task", null, timeout.Token));

        Assert.True(result.Success, result.Output);
        Assert.Equal(["first"], readItems);
        var sent = Assert.Single(handler.Requests, body => Model(body) == "agent-fast");
        Assert.Contains("completed-before-switch", ToolReply(sent, "done-call"), StringComparison.Ordinal);
        AssertInterrupted(ToolReply(sent, "interrupted-call"));
        AssertInterrupted(ToolReply(sent, "not-started-call"));
        AssertPaired(sent);
        Assert.Contains("c3R1Yi1pbWFnZQ==", sent.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("user", Messages(sent)[^1].GetProperty("role").GetString());
        Assert.Empty(registry.ListFor("chat"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Switching_during_parallel_tools_keeps_the_result_of_a_completed_sibling(bool interruptedFirst)
    {
        var registry = new AgentRegistry();
        var agents = new AgentConversations();
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = Call("done-call", "read_file", "{\"item\":\"first\"}");
        var interrupted = Call("interrupted-call", "read_file", "{\"item\":\"second\"}");
        ToolCall[] calls = interruptedFirst ? [interrupted, done] : [done, interrupted];
        using var handler = new Handler((body, _) => Task.FromResult(Json(Model(body) == "agent-lite"
            ? Calls(calls)
            : Final("finished-after-switch"))));
        var host = Host(registry, handler, new StubTool("read_file", async (arguments, token) =>
        {
            if (arguments.GetProperty("item").GetString() == "first")
            {
                completed.SetResult(true);
                return ToolResult.Ok("parallel-result-before-switch");
            }

            // Первый вызов уже исполняется и вернёт результат даже после отмены соседнего.
            await completed.Task.WaitAsync(token);
            Switch(registry);
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The interrupted tool must be cancelled.");
        }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await InChat(agents, () => host.RunAsync("original-task", null, timeout.Token));

        Assert.True(result.Success, result.Output);
        var sent = Assert.Single(handler.Requests, body => Model(body) == "agent-fast");
        Assert.Contains("parallel-result-before-switch", ToolReply(sent, "done-call"), StringComparison.Ordinal);
        AssertInterrupted(ToolReply(sent, "interrupted-call"));
        AssertPaired(sent);
        Assert.Empty(registry.ListFor("chat"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Thinking_signatures_are_carried_only_when_the_model_stays_the_same(bool sameModel)
    {
        var registry = new AgentRegistry();
        var agents = new AgentConversations();
        var settings = AppSettings.CreateDefault();
        settings.AgentLiteModelId = "anthropic:source-model";
        settings.AgentFastModelId = sameModel ? settings.AgentLiteModelId : "anthropic:target-model";
        settings.SynGuardEnabled = false;
        var requests = 0;
        using var handler = new Handler((_, token) =>
        {
            switch (++requests)
            {
                case 1:
                    return Task.FromResult(Json("""
                        {"type":"message","role":"assistant","content":[
                          {"type":"thinking","thinking":"earlier-thought","signature":"source-signature"},
                          {"type":"tool_use","id":"read-1","name":"read_file","input":{}}
                        ],"stop_reason":"tool_use"}
                        """));
                case 2:
                    Switch(registry);
                    token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("The interrupted request must be cancelled.");
                default:
                    return Task.FromResult(Json("""
                        {"type":"message","role":"assistant","content":[
                          {"type":"text","text":"finished-after-switch"}
                        ],"stop_reason":"end_turn"}
                        """));
            }
        });
        var host = new AgentHost(
            new AgentOptions
            {
                Keys = new ApiKeyProvider("sk-test", LlmProvider.Anthropic),
                BaseUrl = "https://api.anthropic.com/v1",
                Model = settings.AgentLiteModelId,
                MaxToolRounds = 3
            },
            new HttpClient { BaseAddress = new Uri("https://example.invalid/") },
            () => settings,
            new ConfirmationQueue(() => settings),
            registry,
            extraTools: () => [new StubTool("read_file", (_, _) => Task.FromResult(ToolResult.Ok("already-found-data")))])
        {
            Route = (_, _, _) => Task.FromResult(new AgentTierDecision("lite", null)),
            Http = _ => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.anthropic.com/v1/") }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await InChat(agents, () => host.RunAsync("original-task", null, timeout.Token));

        Assert.True(result.Success, result.Output);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("source-signature", handler.Requests[1].GetRawText(), StringComparison.Ordinal);
        var sent = handler.Requests[2];
        Assert.Equal(sameModel ? "source-model" : "target-model", Model(sent));
        Assert.Equal(sameModel, sent.GetRawText().Contains("source-signature", StringComparison.Ordinal));
        Assert.Contains("original-task", sent.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("already-found-data", sent.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resuming_keeps_the_follow_up_task_and_consumed_human_notes_for_the_guard()
    {
        var requests = new List<SynGuardRequest>();
        var settings = AppSettings.CreateDefault();
        var options = new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "agent-fast",
            MaxToolRounds = 3
        };
        var modelRequests = 0;
        using var handler = new Handler((_, _) => Task.FromResult(Json(++modelRequests == 1
            ? Calls(Call("fetch-1", "fetch_image"))
            : Final("finished-after-switch"))));
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var agent = new Agent(
            new VeniceClient(http, options),
            new ToolRegistry([new StubTool("fetch_image", (_, _) => Task.FromResult(ToolResult.Ok("fetched-data")))]),
            options,
            new AgentUiAdapter(new AgentRunRecord(), new ConfirmationQueue(() => settings), () => { }, "test-agent"),
            new SessionUndoTracker())
        {
            Settings = () => settings,
            Guard = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new SynGuardReport([true], null, WithoutApproval: [true]));
            }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await agent.ResumeAsync(
            "original-task",
            [
                new() { Role = "user", Content = ChatContent.Text("original-task") },
                new() { Role = "assistant", Content = ChatContent.Text("earlier-report") },
                new() { Role = "user", Content = ChatContent.Text(Agent.ChatAssistantPrefix + "\nfollow-up-request") },
                new() { Role = "user", Content = ChatContent.Text("human-note") }
            ],
            "follow-up-request",
            ["human-note"],
            timeout.Token);

        Assert.Null(result.Error);
        Assert.Equal("finished-after-switch", result.AssistantText);
        var judged = Assert.Single(requests);
        Assert.Contains("original-task", judged.Task, StringComparison.Ordinal);
        Assert.Contains("follow-up-request", judged.Task, StringComparison.Ordinal);
        Assert.Contains("human-note", judged.HumanWords ?? "", StringComparison.Ordinal);
        Assert.All(handler.Requests, body => Assert.Single(Messages(body), message =>
            Content(message).Contains("follow-up-request", StringComparison.Ordinal)));
    }

    private static AgentHost Host(AgentRegistry registry, Handler handler, params ITool[] tools)
    {
        var settings = AppSettings.CreateDefault();
        settings.AgentLiteModelId = "agent-lite";
        settings.AgentFastModelId = "agent-fast";
        settings.SynGuardEnabled = false;
        return new AgentHost(
            new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "agent-lite", MaxToolRounds = 8 },
            new HttpClient { BaseAddress = new Uri("https://example.invalid/") },
            () => settings,
            new ConfirmationQueue(() => settings),
            registry,
            extraTools: () => tools)
        {
            Route = (_, _, _) => Task.FromResult(new AgentTierDecision("lite", null)),
            Http = _ => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") }
        };
    }

    private static async Task<ToolResult> InChat(AgentConversations agents, Func<Task<ToolResult>> run)
    {
        using var scope = AgentRunScope.Push(new AgentRunContext
        {
            Call = new ToolCallRecord { Id = Guid.NewGuid().ToString("N"), Name = InitAgentTool.ToolName },
            Assistant = new ChatDisplayMessage { Role = "assistant" },
            Observer = SilentTurnObserver.Instance,
            SessionId = "chat",
            Conversations = agents
        });
        return await run();
    }

    private static void Switch(AgentRegistry registry) =>
        Assert.Single(registry.ListFor("chat")).Request(new AgentInterrupt(AgentInterruptKind.Switch, "fast"));

    private static string Model(JsonElement body) => body.GetProperty("model").GetString() ?? "";

    private static JsonElement[] Messages(JsonElement body) => body.GetProperty("messages").EnumerateArray().ToArray();

    private static string Content(JsonElement message) =>
        message.TryGetProperty("content", out var content) ? ChatContent.ReadText(content) ?? "" : "";

    private static string Text(JsonElement body) => string.Join("\n", Messages(body).Select(Content));

    private static string ToolReply(JsonElement body, string id) => Content(Assert.Single(Messages(body), message =>
        message.GetProperty("role").GetString() == "tool" && message.GetProperty("tool_call_id").GetString() == id));

    private static void AssertInterrupted(string reply)
    {
        Assert.Contains("interrupt", reply, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown", reply, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inspect", reply, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertPaired(JsonElement body)
    {
        var messages = Messages(body);
        var calls = messages
            .Where(message => message.TryGetProperty("tool_calls", out var markedCalls) && markedCalls.ValueKind == JsonValueKind.Array)
            .SelectMany(message => message.GetProperty("tool_calls").EnumerateArray())
            .Select(call => call.GetProperty("id").GetString())
            .ToArray();
        var replies = messages.Where(message => message.GetProperty("role").GetString() == "tool")
            .Select(message => message.GetProperty("tool_call_id").GetString())
            .ToArray();
        Assert.NotEmpty(calls);
        Assert.Equal(calls, replies);

        for (var i = 0; i < messages.Length; i++)
        {
            if (!messages[i].TryGetProperty("tool_calls", out var pending) || pending.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var index = i;
            foreach (var call in pending.EnumerateArray())
            {
                // Даже картинка завершённого инструмента не вклинивается между вызовами и ответами.
                var reply = messages[++index];
                Assert.Equal("tool", reply.GetProperty("role").GetString());
                Assert.Equal(call.GetProperty("id").GetString(), reply.GetProperty("tool_call_id").GetString());
            }
        }
    }

    private static ToolCall Call(string id, string name, string arguments = "{}") =>
        new() { Id = id, Function = new FunctionCall { Name = name, Arguments = arguments } };

    private static string Calls(params ToolCall[] calls) => JsonSerializer.Serialize(new
    {
        choices = new[]
        {
            new
            {
                message = new ChatMessage { Role = "assistant", ToolCalls = [.. calls] },
                finish_reason = "tool_calls"
            }
        }
    });

    private static string Final(string text) =>
        """{"choices":[{"message":{"role":"assistant","content":""" + JsonSerializer.Serialize(text) + "}}]}";

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };

    private sealed class StubTool(
        string name,
        Func<JsonElement, CancellationToken, Task<ToolResult>> run) : ITool
    {
        public string Name => name;

        public string Description => "stub";

        public JsonElement ParametersSchema => JsonSchema.Parse("""{"type":"object","properties":{}}""");

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
            run(arguments, cancellationToken);
    }

    private sealed class Handler(
        Func<JsonElement, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(text);
            var body = document.RootElement.Clone();
            Requests.Add(body);
            return await respond(body, cancellationToken);
        }
    }
}
