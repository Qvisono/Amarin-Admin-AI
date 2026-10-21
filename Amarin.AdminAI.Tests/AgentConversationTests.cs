using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Агент не исчезает после отчёта (1.36.0): чат может написать ему (<c>message_agent</c>) и отдать
/// задачу отказавшего агента модели сильнее (<c>agent_refused</c>) — до конца своего ответа.
/// </summary>
/// <remarks>
/// Агент настоящий, ответы модели — по списку, без сети: проверяется, что именно уходит модели при
/// продолжении — прежняя переписка, сообщение чата, без отказного ответа после подъёма.
/// </remarks>
public sealed class AgentConversationTests
{
    private readonly List<string> _bodies = [];

    [Fact]
    public async Task The_report_names_the_agent_and_the_chat_can_write_to_it_with_its_history()
    {
        var agents = new AgentConversations();
        var host = Host(Settings(), [Final("first-report"), Final("second-report")]);

        var first = await InChat(agents, () => host.RunAsync("task-text", null, CancellationToken.None));
        var second = await InChat(agents, () => host.ContinueAsync("a1", "follow-up-text", CancellationToken.None));

        Assert.True(first.Success, first.Output);
        Assert.Equal("Отчёт агента a1: first-report", first.Output);
        Assert.True(second.Success, second.Output);
        Assert.Equal("Отчёт агента a1: second-report", second.Output);

        // Продолжение несёт и задание, и прежний отчёт, и сообщение чата — подписанное как его.
        var sent = _bodies[1];
        Assert.Contains("task-text", sent, StringComparison.Ordinal);
        Assert.Contains("first-report", sent, StringComparison.Ordinal);
        Assert.Contains("follow-up-text", sent, StringComparison.Ordinal);
        Assert.Contains("Message from the chat assistant", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_reaches_the_chat_with_what_it_can_do_and_the_history_stays_paired()
    {
        var agents = new AgentConversations();
        var call = new ToolCallRecord { Id = "c1", Name = InitAgentTool.ToolName };
        var host = Host(Settings(), [Refuse("reasons-one"), Final("done-after-talk")]);

        var refused = await InChat(agents, () => host.RunAsync("task-text", null, CancellationToken.None), call);

        Assert.True(refused.Success, refused.Output);
        Assert.Contains("Агент a1", refused.Output, StringComparison.Ordinal);
        Assert.Contains("отказался", refused.Output, StringComparison.Ordinal);
        Assert.Contains("reasons-one", refused.Output, StringComparison.Ordinal);
        Assert.Contains(MessageAgentTool.ToolName, refused.Output, StringComparison.Ordinal);
        Assert.Contains(AgentRefusedTool.ToolName, refused.Output, StringComparison.Ordinal);
        Assert.Equal(Refusals.FinalText("agent-lite", "reasons-one"), call.NestedAgent!.ReportText);

        // На отказной вызов лёг ответ, иначе продолжить разговор API не дал бы.
        Assert.Equal("tool", Assert.IsType<AgentConversation>(agents.Find("a1")).History[^1].Role);

        var talked = await InChat(agents, () => host.ContinueAsync("a1", "check-again", CancellationToken.None));

        Assert.Equal("Отчёт агента a1: done-after-talk", talked.Output);
        Assert.Contains("Your refusal was passed to the chat assistant.", _bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_refused_climbs_lite_to_heavy_to_bypass_and_drops_the_refusing_reply()
    {
        var agents = new AgentConversations();
        var host = Host(Settings(), [Refuse("reasons-one"), Refuse("reasons-two"), Refuse("reasons-three")]);

        await InChat(agents, () => host.RunAsync("task-text", null, CancellationToken.None));
        var heavy = await InChat(agents, () => host.EscalateAsync("a1", CancellationToken.None));
        var bypass = await InChat(agents, () => host.EscalateAsync("a1", CancellationToken.None));
        var top = await InChat(agents, () => host.EscalateAsync("a1", CancellationToken.None));

        Assert.Contains("\"model\":\"agent-lite\"", _bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"model\":\"agent-heavy\"", _bodies[1], StringComparison.Ordinal);
        Assert.Contains("\"model\":\"agent-bypass\"", _bodies[2], StringComparison.Ordinal);

        // Модель сильнее отказного ответа не видит, а задание — видит.
        Assert.Contains("task-text", _bodies[1], StringComparison.Ordinal);
        Assert.DoesNotContain("reasons-one", _bodies[1], StringComparison.Ordinal);
        Assert.DoesNotContain("reasons-two", _bodies[2], StringComparison.Ordinal);

        Assert.Contains("agent_refused", heavy.Output, StringComparison.Ordinal);
        Assert.Contains("модели сильнее нет", bypass.Output, StringComparison.Ordinal);
        Assert.False(top.Success);
        Assert.Equal(3, _bodies.Count);
    }

    /// <summary>
    /// После разговора с чатом в истории лежит и первый отказ. Модель сильнее не должна видеть ни
    /// одного: с прежним отказом в истории защитник читал «задача не выполняется» среди сделанного
    /// и запрещал её первый же шаг.
    /// </summary>
    [Fact]
    public async Task After_a_talk_the_stronger_model_sees_none_of_the_refusals()
    {
        var agents = new AgentConversations();
        var host = Host(Settings(), [Refuse("reasons-one"), Refuse("reasons-two"), Final("done")]);

        await InChat(agents, () => host.RunAsync("task-text", null, CancellationToken.None));
        await InChat(agents, () => host.ContinueAsync("a1", "check-again", CancellationToken.None));
        var heavy = await InChat(agents, () => host.EscalateAsync("a1", CancellationToken.None));

        Assert.Equal("Отчёт агента a1: done", heavy.Output);
        var sent = _bodies[2];
        Assert.Contains("\"model\":\"agent-heavy\"", sent, StringComparison.Ordinal);

        // Задание и сказанное чатом остаются, отказы и ответы на их вызовы — нет.
        Assert.Contains("task-text", sent, StringComparison.Ordinal);
        Assert.Contains("check-again", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("reasons-one", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("reasons-two", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("Your refusal was passed", sent, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_refusals_keeps_the_work_and_drops_every_refusal_with_its_answers()
    {
        var conversation = new AgentConversation
        {
            Id = "a1",
            Prompt = "task-text",
            Tier = "lite",
            History =
            [
                new() { Role = "user", Content = ChatContent.Text("task-text") },
                new() { Role = "assistant", ToolCalls = [Call("f1", "fetch_image")] },
                new() { Role = "tool", ToolCallId = "f1", Content = ChatContent.Text("image") },
                new() { Role = "assistant", ToolCalls = [Call("r1", Refusals.ToolName), Call("f2", "fetch_image")] },
                new() { Role = "tool", ToolCallId = "r1", Content = ChatContent.Text("refusal passed") },
                new() { Role = "tool", ToolCallId = "f2", Content = ChatContent.Text("not run") },
                new() { Role = "user", Content = ChatContent.Text("check-again") },
                new() { Role = "assistant", Content = ChatContent.Text("I still refuse, in words") }
            ]
        };

        var kept = conversation.WithoutRefusals();

        Assert.Equal(
            ["user:task-text", "assistant:f1", "tool:f1", "user:check-again"],
            kept.Select(message => message.Role + ":" +
                                   (message.ToolCalls?.FirstOrDefault()?.Id ?? message.ToolCallId ?? ChatContent.ReadText(message.Content))));
    }

    [Fact]
    public void The_guard_never_reads_a_refusal_among_the_calls_already_made()
    {
        List<ChatMessage> messages =
        [
            new() { Role = "assistant", ToolCalls = [Call("r1", Refusals.ToolName)] },
            new() { Role = "tool", ToolCallId = "r1", Content = ChatContent.Text("Your refusal was passed to the chat assistant.") },
            new() { Role = "assistant", ToolCalls = [Call("f1", "fetch_image")] },
            new() { Role = "tool", ToolCallId = "f1", Content = ChatContent.Text("image") }
        ];

        var line = Assert.Single(SynGuard.EarlierCalls(messages));

        Assert.StartsWith("fetch_image", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Агента переубедили, и он продолжает с отказом в своей истории: защитник его шага этого
    /// отказа не видит — тем же путём, каким его спрашивает настоящий агент.
    /// </summary>
    [Fact]
    public async Task A_talked_round_agent_goes_to_the_guard_without_its_earlier_refusal()
    {
        var requests = new List<SynGuardRequest>();
        var agent = new Agent(
            new VeniceClient(
                new HttpClient(new SequenceHandler([Calls(("f1", "fetch_image")), Final("ok")], _bodies))
                {
                    BaseAddress = new Uri("https://api.venice.ai/api/v1/")
                },
                new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "m", MaxToolRounds = 3 }),
            new ToolRegistry([new StubTool("fetch_image")]),
            new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "m", MaxToolRounds = 3 },
            new SilentUi(),
            new SessionUndoTracker())
        {
            Settings = AppSettings.CreateDefault,
            Guard = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new SynGuardReport([true], null, WithoutApproval: [true]));
            }
        };

        _ = await agent.ContinueAsync(
            "task-text",
            [
                new() { Role = "user", Content = ChatContent.Text("task-text") },
                new() { Role = "assistant", ToolCalls = [Call("r1", Refusals.ToolName, "{\"reasons\":\"reasons-one\"}")] },
                new() { Role = "tool", ToolCallId = "r1", Content = ChatContent.Text("Your refusal was passed to the chat assistant.") }
            ],
            "please do it",
            CancellationToken.None);

        var request = Assert.Single(requests);
        Assert.Equal("fetch_image", Assert.Single(request.Calls).Name);
        Assert.DoesNotContain(request.Earlier ?? [], line =>
            line.Contains(Refusals.ToolName, StringComparison.Ordinal) || line.Contains("reasons-one", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_answer_does_not_see_the_agents_of_the_previous_one()
    {
        var host = Host(Settings(), [Final("first-report")]);
        await InChat(new AgentConversations(), () => host.RunAsync("task-text", null, CancellationToken.None));

        var result = await InChat(new AgentConversations(), () => host.ContinueAsync("a1", "hello", CancellationToken.None));

        Assert.False(result.Success);
        Assert.Contains("init_agent", result.Output, StringComparison.Ordinal);
        Assert.Single(_bodies);
    }

    [Fact]
    public async Task A_busy_agent_turns_a_second_message_away()
    {
        var agents = new AgentConversations();
        var host = Host(Settings(), [Final("first-report")]);
        await InChat(agents, () => host.RunAsync("task-text", null, CancellationToken.None));
        Assert.True(Assert.IsType<AgentConversation>(agents.Find("a1")).TryEnter());

        var result = await InChat(agents, () => host.ContinueAsync("a1", "hello", CancellationToken.None));

        Assert.False(result.Success);
        Assert.Single(_bodies);
    }

    [Theory]
    [InlineData("fast", "heavy")]
    [InlineData("lite", "heavy")]
    [InlineData("heavy", AgentHost.BypassTier)]
    [InlineData(AgentHost.BypassTier, null)]
    public void The_agent_ladder_is_heavy_then_bypass(string tier, string? next) =>
        Assert.Equal(next, AgentHost.NextTier(tier, Settings()));

    [Fact]
    public void A_step_with_the_same_model_is_skipped()
    {
        var settings = Settings();
        settings.AgentHeavyModelId = settings.AgentLiteModelId;

        Assert.Equal(AgentHost.BypassTier, AgentHost.NextTier("lite", settings));
    }

    [Fact]
    public void The_conversation_tools_go_with_the_agent_switch_and_never_ask()
    {
        var settings = AppSettings.CreateDefault();
        settings.DisabledTools = [InitAgentTool.ToolName];

        foreach (var tool in new[] { MessageAgentTool.ToolName, AgentRefusedTool.ToolName })
        {
            Assert.True(ToolGate.IsDisabled(settings, tool));
            Assert.Equal(ToolEffect.Read, ToolEffects.Classify(tool, default));
            Assert.True(RemoteSupport.Allows(tool));
            Assert.False(RecipeRules.IsRunnable(tool));
            Assert.True(ToolCatalog.IsAgentCall(tool));
        }

        var definitions = ToolGate.WithoutDisabled(
            [Definition(InitAgentTool.ToolName), Definition(MessageAgentTool.ToolName), Definition("read_file")], settings);
        Assert.Equal(["read_file"], definitions.Select(definition => definition.Function.Name));
    }

    private static ToolDefinition Definition(string name) => new()
    {
        Function = new FunctionDefinition
        {
            Name = name,
            Description = "stub",
            Parameters = JsonSchema.Parse("""{"type":"object","properties":{}}""")
        }
    };

    private static AppSettings Settings()
    {
        var settings = AppSettings.CreateDefault();
        settings.AgentFastModelId = "agent-fast";
        settings.AgentLiteModelId = "agent-lite";
        settings.AgentHeavyModelId = "agent-heavy";
        settings.BypassModelId = "agent-bypass";

        // Без защитника и плана: проверяется разговор, а не очередь подтверждений.
        settings.SynGuardEnabled = false;
        return settings;
    }

    private AgentHost Host(AppSettings settings, string[] replies)
    {
        var handler = new SequenceHandler(replies, _bodies);
        return new AgentHost(
            new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "agent-lite", MaxToolRounds = 3 },
            new HttpClient { BaseAddress = new Uri("https://example.invalid/") },
            () => settings,
            new ConfirmationQueue(() => settings),
            new AgentRegistry())
        {
            Route = (_, _, _) => Task.FromResult(new AgentTierDecision("lite", null)),
            Http = _ => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") }
        };
    }

    /// <summary>Вызов из ответа чата: своя строка инструмента, общие агенты ответа.</summary>
    private static async Task<ToolResult> InChat(
        AgentConversations agents,
        Func<Task<ToolResult>> run,
        ToolCallRecord? call = null)
    {
        using (AgentRunScope.Push(new AgentRunContext
        {
            Call = call ?? new ToolCallRecord { Id = Guid.NewGuid().ToString("N"), Name = InitAgentTool.ToolName },
            Assistant = new ChatDisplayMessage { Role = "assistant" },
            Observer = SilentTurnObserver.Instance,
            SessionId = "chat",
            Conversations = agents
        }))
        {
            return await run();
        }
    }

    private static ToolCall Call(string id, string name, string arguments = "{}") =>
        new() { Id = id, Function = new FunctionCall { Name = name, Arguments = arguments } };

    private static string Calls(params (string Id, string Name)[] calls) =>
        """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":""" +
        JsonSerializer.Serialize(calls.Select(call => new
        {
            id = call.Id,
            type = "function",
            function = new { name = call.Name, arguments = "{}" }
        })) +
        """},"finish_reason":"tool_calls"}]}""";

    private static string Final(string text) =>
        """{"choices":[{"message":{"role":"assistant","content":""" + JsonSerializer.Serialize(text) + "}}]}";

    private static string Refuse(string reasons) =>
        """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[""" +
        JsonSerializer.Serialize(new
        {
            id = "r" + reasons,
            type = "function",
            function = new { name = Refusals.ToolName, arguments = JsonSerializer.Serialize(new { reasons }) }
        }) +
        """]},"finish_reason":"tool_calls"}]}""";

    /// <summary>Инструмент-пустышка: проверяется путь к защитнику, а не сам инструмент.</summary>
    private sealed class StubTool(string name) : ITool
    {
        public string Name => name;

        public string Description => "stub";

        public JsonElement ParametersSchema => JsonSchema.Parse("""{"type":"object","properties":{}}""");

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Ok("done"));
    }

    private sealed class SilentUi : IAgentUi
    {
        public void Warn(string message)
        {
        }

        public void Error(string message)
        {
        }

        public void Info(string message)
        {
        }

        public void AssistantMessage(string text)
        {
        }

        public void ToolCall(string name, string argumentsJson)
        {
        }

        public void ToolResult(string name, ToolResult result)
        {
        }

        public Task<T> RunBusyAsync<T>(string message, Func<Task<T>> work, CancellationToken cancellationToken = default) => work();

        public Task<bool> ConfirmDangerousActionAsync(DangerousActionInfo info, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class SequenceHandler(string[] replies, List<string> bodies) : HttpMessageHandler
    {
        private int _next;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (bodies)
            {
                bodies.Add(body);
            }

            var index = Math.Min(Interlocked.Increment(ref _next) - 1, replies.Length - 1);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(replies[index], Encoding.UTF8, "application/json")
            };
        }
    }
}
