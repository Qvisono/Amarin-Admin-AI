using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// A remark the model makes before reaching for a tool used to be written into
/// <c>assistant.Text</c> and wiped by the next round, which assigns rather than appends. These
/// tests pin that it survives the round that follows it, and that it is kept apart from the
/// engine's own status line, which is overwritten on purpose.
/// </summary>
public sealed class ModelNoteTests
{
    private static AgentOptions Options() => new()
    {
        ApiKey = "test",
        BaseUrl = "https://api.venice.ai/api/v1",
        Model = "grok-4-6",
        MaxToolRounds = 3
    };

    [Fact]
    public async Task The_remark_before_a_tool_call_outlives_the_round_that_follows_it()
    {
        var round = 0;
        var handler = new ScriptedHandler(_ => Interlocked.Increment(ref round) == 1
            ? SseWithToolCall("Хм, посмотрю, что в реестре.", "read_file")
            : Sse("Готово."));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = Options();
        var settings = AppSettings.CreateDefault();
        var engine = new ChatEngine(
            new VeniceClient(http, options), options, () => settings, new ToolRegistry([new StubTool()]));

        var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
        var observer = new SilentObserver();
        await engine.RunTurnAsync(session, "привет", observer, CancellationToken.None);

        var assistant = session.Messages.Last(message => message.Role == "assistant");
        var toolRound = Assert.Single(assistant.ToolRounds);

        Assert.Equal("Хм, посмотрю, что в реестре.", toolRound.ModelNote);

        // The final answer is the second round's, proving the first round's text really was
        // overwritten on the message and only survived because the round kept a copy.
        Assert.Equal("Готово.", assistant.Text);
    }

    [Fact]
    public async Task The_engines_own_status_line_does_not_eat_the_remark()
    {
        var round = 0;
        var handler = new ScriptedHandler(_ => Interlocked.Increment(ref round) == 1
            ? SseWithToolCall("Проверю файл.", "read_file")
            : Sse("Готово."));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = Options();
        var settings = AppSettings.CreateDefault();
        var engine = new ChatEngine(
            new VeniceClient(http, options), options, () => settings, new ToolRegistry([new StubTool()]));

        var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
        await engine.RunTurnAsync(session, "привет", new SilentObserver(), CancellationToken.None);

        var toolRound = Assert.Single(session.Messages.Last(m => m.Role == "assistant").ToolRounds);

        Assert.Equal("Проверю файл.", toolRound.ModelNote);
        Assert.Contains("Инструменты завершены", toolRound.InfoLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Research_displays_the_plan_before_tools_and_keeps_it_on_the_answer()
    {
        var bodies = new List<string>();
        var observer = new SilentObserver();
        var handler = new ScriptedHandler(request =>
        {
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return bodies.Count switch
            {
                1 => Sse("Compare primary sources."),
                2 when observer.VisibleResearchPlan == "Compare primary sources." => SseWithToolCall("", "read_file"),
                3 => Sse("Findings."),
                _ => throw new InvalidOperationException("Unexpected request.")
            };
        });
        using var http = new HttpClient(handler);
        var options = Options();
        var engine = new ChatEngine(new VeniceClient(http, options), options, AppSettings.CreateDefault,
            new ToolRegistry([new StubTool()]));
        var session = new ChatSession { Id = "r", SelectedModelId = "grok-4-6" };

        await engine.RunTurnAsync(session, "/research explain this", observer, CancellationToken.None);

        Assert.Equal(3, bodies.Count);
        Assert.DoesNotContain("\"tools\"", bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("/research explain this", bodies[0], StringComparison.Ordinal);
        Assert.Contains("explain this", bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"tools\"", bodies[1], StringComparison.Ordinal);
        Assert.Contains("independent sources", bodies[1], StringComparison.Ordinal);
        Assert.Contains("содержимое", ChatContent.ReadText(session.ApiMessages.Single(m => m.Role == "tool").Content));
        var answer = session.Messages[^1];
        Assert.Equal("Findings.", answer.Text);
        Assert.Equal("Compare primary sources.", answer.ResearchPlan);
    }

    [Fact]
    public async Task Resuming_research_uses_its_plan_and_the_next_normal_turn_has_none()
    {
        var bodies = new List<string>();
        var handler = new ScriptedHandler(request =>
        {
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return Sse(bodies.Count == 1 ? "Check both alternatives." : "Answer.");
        });
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(handler);
        var options = Options();
        var engine = new ChatEngine(new VeniceClient(http, options), options, AppSettings.CreateDefault, new ToolRegistry([]));
        var session = new ChatSession { Id = "r", SelectedModelId = "grok-4-6" };

        await engine.RunTurnAsync(session, "/research topic", new SilentObserver { PlanShown = cancellation.Cancel }, cancellation.Token);
        var cancelled = session.Messages[^1];
        Assert.Single(bodies);
        Assert.Equal("Check both alternatives.", cancelled.ResearchPlan);
        await engine.ResumeAssistantAsync(session, cancelled, new SilentObserver(), CancellationToken.None);
        await engine.RunTurnAsync(session, "plain follow up", new SilentObserver(), CancellationToken.None);

        Assert.Equal(3, bodies.Count);
        Assert.Contains("Check both alternatives.", bodies[1], StringComparison.Ordinal);
        Assert.DoesNotContain("The user requested deep research", bodies[2], StringComparison.Ordinal);
        Assert.Null(session.Messages[^1].ResearchPlan);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public async Task Chat_mcp_tools_obey_confirmation_and_current_server_availability(
        bool approve, bool disconnectDuringConfirmation, bool expectSuccess)
    {
        const string name = "test_beta7__probe";
        var settings = AppSettings.CreateDefault();
        settings.ApprovalMode = ApprovalMode.AskAll;
        var queue = new ConfirmationQueue(() => settings);
        var connected = true;
        var questioned = false;
        queue.Changed += () =>
        {
            if (queue.TryPeek(out var request))
            {
                questioned = true;
                connected = !disconnectDuringConfirmation;
                queue.Complete(request, approve);
            }
        };
        var bodies = new List<string>();
        using var http = new HttpClient(new ScriptedHandler(request =>
        {
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return bodies.Count == 1 ? SseWithToolCall("", name) : Sse("Done.");
        }));
        var options = Options();
        var engine = new ChatEngine(new VeniceClient(http, options), options, () => settings, new ToolRegistry([]),
            confirmations: queue)
        {
            McpTools = () => connected ? [new StubTool(name)] : []
        };
        var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };

        await engine.RunTurnAsync(session, "Try the connected tool", new SilentObserver(), CancellationToken.None);

        Assert.True(questioned);
        Assert.Contains(name, bodies[0], StringComparison.Ordinal);
        var tool = Assert.Single(Assert.Single(session.Messages[^1].ToolRounds).Calls);
        Assert.Equal(expectSuccess, tool.Success);
        if (disconnectDuringConfirmation)
        {
            Assert.DoesNotContain(engine.ToolsFor([]), definition => definition.Function.Name == name);
        }
    }

    [Fact]
    public void A_silent_model_leaves_the_note_empty_rather_than_blank_padded()
    {
        Assert.Equal("", new ToolRound().ModelNote);
    }

    [Fact]
    public void The_agents_remark_lands_on_its_round_as_well_as_in_the_report()
    {
        var record = new AgentRunRecord { ModelId = "grok-4-6", DisplayName = "Агент" };
        var adapter = new AgentUiAdapter(
            record, new ConfirmationQueue(AppSettings.CreateDefault), () => { }, "Агент");

        adapter.AssistantMessage("Сначала гляну службы.");
        adapter.ToolCall("windows_service", "{}");
        adapter.ToolResult("windows_service", ToolResult.Ok("ok"));

        var round = Assert.Single(record.ToolRounds);

        // Both copies matter: ReportText is what the caller gets back as the tool result, the
        // note is what the expander draws.
        Assert.Equal("Сначала гляну службы.", record.ReportText);
        Assert.Equal("Сначала гляну службы.", round.ModelNote);

        // And the call joined the round the remark opened, rather than starting a second one -
        // the model spoke about the work it was just about to do.
        Assert.Equal("windows_service", Assert.Single(round.Calls).Name);
    }

    [Fact]
    public void The_feed_can_keep_reading_rounds_and_calls_while_the_agent_adds_more()
    {
        // Лента перебирает раунды на потоке окна, пока агент дописывает их на своём. Дописанное в
        // тот же список роняло перебор: «Collection was modified» в ChatMessageViews.
        var record = new AgentRunRecord { ModelId = "grok-4-6", DisplayName = "Агент" };
        var adapter = new AgentUiAdapter(
            record, new ConfirmationQueue(AppSettings.CreateDefault), () => { }, "Агент");
        adapter.ToolCall("registry", "{}");

        var seenCalls = 0;
        foreach (var round in record.ToolRounds)
        {
            foreach (var call in round.Calls)
            {
                // Новый вызов того же раунда и новый раунд — посреди перебора.
                adapter.ToolCall("windows_service", "{}");
                adapter.ToolResult("registry", ToolResult.Ok("done"));
                adapter.ToolResult("windows_service", ToolResult.Ok("done"));
                adapter.Info(EngineLines.ToolsDoneStep(2));
                adapter.ToolCall("filesystem", "{}");
                seenCalls++;
            }
        }

        Assert.Equal(1, seenCalls);
        Assert.Equal(["registry", "windows_service", "filesystem"],
            record.ToolRounds.SelectMany(round => round.Calls).Select(call => call.Name));
    }

    [Fact]
    public void A_tool_call_is_timed_from_start_to_result()
    {
        var record = new AgentRunRecord { ModelId = "grok-4-6", DisplayName = "Агент" };
        var adapter = new AgentUiAdapter(
            record, new ConfirmationQueue(AppSettings.CreateDefault), () => { }, "Агент");

        adapter.ToolCall("registry", "{}");
        adapter.ToolResult("registry", ToolResult.Ok("ok"));

        var call = Assert.Single(Assert.Single(record.ToolRounds).Calls);

        Assert.NotEqual(default, call.StartedAt);
        Assert.True(call.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public void The_prompt_asks_for_the_remark_and_says_where_it_shows_up()
    {
        Assert.Contains("THINKING OUT LOUD", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains("stays visible", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);

        // Everyone who ever pressed "Save" on the previous default keeps a byte-identical copy in
        // their settings; without this entry the new rule would never reach them.
        var settings = AppSettings.CreateDefault();
        settings.TechAiPrompt = LegacyTechPrompts.V12;
        Assert.True(AppSettingsStore.MigrateLegacyChatPrompts(settings));
        Assert.Equal("", settings.TechAiPrompt);

        Assert.DoesNotContain(
            "THINKING OUT LOUD", LegacyTechPrompts.V12, StringComparison.Ordinal);
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

    private sealed class StubTool(string name = "read_file") : ITool
    {
        public string Name => name;

        public string Description => "stub";

        public JsonElement ParametersSchema =>
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        public Task<ToolResult> ExecuteAsync(
            JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Ok("содержимое"));
    }

    private sealed class SilentObserver : IChatTurnObserver
    {
        public string? VisibleResearchPlan { get; private set; }
        public Action? PlanShown { get; init; }

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
            if (VisibleResearchPlan is null && assistant.ResearchPlan is { Length: > 0 } plan)
            {
                VisibleResearchPlan = plan;
                PlanShown?.Invoke();
            }
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
