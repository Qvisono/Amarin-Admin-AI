using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Оператор мыши за агентом: когда у агента есть <c>use_computer</c>, на какой модели и с каким
/// пределом работает оператор, куда уходят его слова и деньги, и что видит защитник. Модель
/// отвечает по списку, экран — подделка: ни сети, ни настоящей мыши.
/// </summary>
public sealed partial class ComputerOperatorTests
{
    private static readonly string[] ExampleMarkers =
        ["e.g.", "for example", "for instance", "such as", "например", "пример"];

    private readonly FakeComputer _computer = new();
    private readonly FakeStage _stage = new();
    private readonly List<string> _bodies = [];

    private static string ToolCall(string id, string name, object arguments) =>
        JsonSerializer.Serialize(new
        {
            id,
            type = "function",
            function = new { name, arguments = JsonSerializer.Serialize(arguments) }
        });

    private static string Calls(string call, decimal cost) =>
        """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[""" + call +
        """]},"finish_reason":"tool_calls"}],"usage":{"cost":""" + Money(cost) + "}}";

    private static string Final(string text, decimal cost) =>
        """{"choices":[{"message":{"role":"assistant","content":""" + JsonSerializer.Serialize(text) +
        """}}],"usage":{"cost":""" + Money(cost) + "}}";

    private static string Money(decimal cost) => cost.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static AppSettings Settings(bool computerControl)
    {
        var settings = AppSettings.CreateDefault();
        settings.ComputerControl = computerControl;
        settings.AgentLiteModelId = "agent-model";
        settings.ComputerModelId = "vision-model";

        // Без защитника и без вопросов: проверяется путь оператора, а не очередь подтверждений.
        settings.SynGuardEnabled = false;
        settings.ApprovalMode = ApprovalMode.AlwaysApprove;
        return settings;
    }

    private AgentHost Host(AppSettings settings, string[] replies)
    {
        var options = new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "agent-model",
            MaxToolRounds = 3
        };
        var handler = new SequenceHandler(replies, _bodies);
        var host = new AgentHost(
            options,
            new HttpClient { BaseAddress = new Uri("https://example.invalid/") },
            () => settings,
            new ConfirmationQueue(() => settings),
            computer: () => new ComputerAccess(_computer, _stage, new ComputerLease()))
        {
            Route = (_, _, _) => Task.FromResult(new AgentTierDecision("lite", null)),
            Http = _ => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") }
        };
        return host;
    }

    private static (ToolCallRecord Call, ChatDisplayMessage Assistant) Scope()
    {
        var call = new ToolCallRecord { Id = "c1", Name = "init_agent", ArgumentsJson = "{}" };
        var assistant = new ChatDisplayMessage { Role = "assistant", ToolRounds = [new ToolRound { Calls = [call] }] };
        return (call, assistant);
    }

    private static async Task<ToolResult> RunInChat(AgentHost host, string task, string? humanWords = null)
    {
        var (call, assistant) = Scope();
        using (AgentRunScope.Push(new AgentRunContext
        {
            Call = call,
            Assistant = assistant,
            Observer = SilentTurnObserver.Instance,
            SessionId = "chat",
            HumanWords = humanWords
        }))
        {
            return await host.RunAsync(task, null, CancellationToken.None);
        }
    }

    private int RequestsFor(string model) =>
        _bodies.Count(body => body.Contains($"\"model\":\"{model}\"", StringComparison.Ordinal));

    // ───────────────────────── когда есть ─────────────────────────

    [Fact]
    public async Task Without_the_switch_the_agent_never_sees_use_computer()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        var host = Host(Settings(computerControl: false), [Final("done", 0m)]);

        _ = await host.RunAsync("task", null, CancellationToken.None);

        Assert.DoesNotContain($"\"name\":\"{UseComputerTool.ToolName}\"", _bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("PROGRAM WINDOWS", _bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_the_switch_the_agent_gets_the_tool_and_the_rule()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        var host = Host(Settings(computerControl: true), [Final("done", 0m)]);

        _ = await host.RunAsync("task", null, CancellationToken.None);

        Assert.Contains($"\"name\":\"{UseComputerTool.ToolName}\"", _bodies[0], StringComparison.Ordinal);
        Assert.Contains("PROGRAM WINDOWS (use_computer)", _bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain($"\"name\":\"{ComputerTool.ToolName}\"", _bodies[0], StringComparison.Ordinal);
    }

    // ───────────────────────── как работает ─────────────────────────

    [Fact]
    public async Task The_operator_works_on_its_own_slot_reports_back_and_bills_the_agent()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        var host = Host(Settings(computerControl: true),
        [
            Calls(ToolCall("a1", UseComputerTool.ToolName, new { task = "save the document" }), 0.01m),
            Calls(ToolCall("o1", ComputerTool.ToolName, new { action = "key", keys = "ctrl+s", explanation = "save" }), 0.10m),
            Final("Saved, the window shows the document.", 0.10m),
            Final("The document is saved.", 0.01m)
        ]);
        var (call, assistant) = Scope();

        ToolResult result;
        using (AgentRunScope.Push(new AgentRunContext
        {
            Call = call,
            Assistant = assistant,
            Observer = SilentTurnObserver.Instance,
            SessionId = "chat",
            HumanWords = "save it"
        }))
        {
            result = await host.RunAsync("save the document", null, CancellationToken.None);
        }

        Assert.True(result.Success, result.Output);
        Assert.Contains("keys ctrl+s", _computer.Log);

        // Оператор — на своей модели, со своим промптом и одним инструментом.
        var operatorBody = _bodies[1];
        Assert.Contains("\"model\":\"vision-model\"", operatorBody, StringComparison.Ordinal);
        Assert.Contains("through the computer tool", operatorBody, StringComparison.Ordinal);
        Assert.Contains("What the person wrote", operatorBody, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"name\":\"{UseComputerTool.ToolName}\"", operatorBody, StringComparison.Ordinal);
        Assert.Contains("Operator report: Saved", _bodies[3], StringComparison.Ordinal);

        // Лента: оператор вложен в строку use_computer, а его деньги — в счёт агента.
        var agent = Assert.IsType<AgentRunRecord>(call.NestedAgent);
        var nested = agent.ToolRounds.SelectMany(round => round.Calls).Single(item => item.Name == UseComputerTool.ToolName).NestedAgent;
        Assert.NotNull(nested);
        Assert.Equal(AgentRunStatus.Complete, nested.Status);
        Assert.Equal("vision-model", nested.ModelId);
        Assert.Equal(0.20m, nested.Cost?.Usd);
        Assert.Equal(0.22m, agent.Cost?.Usd);

        // Мышь отдана, плашка закрыта.
        Assert.Single(_stage.Begins);
        Assert.Equal(1, _stage.Ends);
    }

    [Fact]
    public async Task The_operator_has_a_hundred_steps_however_long_the_agent_is_allowed()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));

        // Оператор никогда не заканчивает сам: каждый ответ — ещё одно нажатие.
        var host = Host(Settings(computerControl: true),
        [
            Calls(ToolCall("a1", UseComputerTool.ToolName, new { task = "keep pressing" }), 0m),
            Calls(ToolCall("o1", ComputerTool.ToolName, new { action = "key", keys = "tab", explanation = "next" }), 0m)
        ]);

        _ = await RunInChat(host, "keep pressing");

        // Сто раундов и итоговая просьба написать отчёт.
        Assert.Equal(ComputerOperator.MaxSteps + 1, RequestsFor("vision-model"));
    }

    [Fact]
    public async Task Only_the_latest_screenshots_reach_the_operator_model()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        var shot = Calls(ToolCall("s", ComputerTool.ToolName, new { action = "screenshot", window = "w7" }), 0m);
        var host = Host(Settings(computerControl: true),
        [
            Calls(ToolCall("a1", UseComputerTool.ToolName, new { task = "look" }), 0m),
            shot, shot, shot, shot,
            Final("Looked.", 0m),
            Final("Done.", 0m)
        ]);

        _ = await RunInChat(host, "look");

        var last = _bodies.Last(body => body.Contains("\"model\":\"vision-model\"", StringComparison.Ordinal));
        Assert.Single(ImageUrl().Matches(last));
        Assert.Contains(JsonEncoded(ComputerHistory.ImageStub), last, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_show_assignment_reaches_the_operator_as_one()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        var host = Host(Settings(computerControl: true),
        [
            Calls(ToolCall("a1", UseComputerTool.ToolName, new { task = "where is the option", mode = "show" }), 0m),
            Calls(ToolCall("o1", ComputerTool.ToolName, new { action = "key", keys = "tab", explanation = "next" }), 0m),
            Final("Showed.", 0m),
            Final("Done.", 0m)
        ]);

        _ = await RunInChat(host, "show me");

        Assert.Contains("Show assignment from the agent", _bodies[1], StringComparison.Ordinal);
        Assert.DoesNotContain(_computer.Log, line => line.StartsWith("keys", StringComparison.Ordinal));
    }

    // ───────────────────────── слова человека ─────────────────────────

    [Fact]
    public async Task The_guard_hears_the_persons_own_words_next_to_the_agents_task()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        var settings = Settings(computerControl: true);
        var requests = new List<SynGuardRequest>();
        var agent = new Agent(
            new VeniceClient(new HttpClient(new SequenceHandler(
                [
                    Calls(ToolCall("x", "probe", new { action = "run" }), 0m),
                    Final("ok", 0m)
                ], _bodies)) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") },
                new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "m", MaxToolRounds = 3 }),
            new ToolRegistry([new ProbeTool()]),
            new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "m", MaxToolRounds = 3 },
            new SilentUi(),
            new SessionUndoTracker())
        {
            Settings = () => settings,
            Guard = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new SynGuardReport([true], null, WithoutApproval: [true]));
            }
        };
        var (call, assistant) = Scope();

        using (AgentRunScope.Push(new AgentRunContext
        {
            Call = call,
            Assistant = assistant,
            Observer = SilentTurnObserver.Instance,
            HumanWords = "the person's words"
        }))
        {
            _ = await agent.RunAsync("the agent's restatement", CancellationToken.None);
        }

        var request = Assert.Single(requests);
        Assert.Equal("the person's words", request.HumanWords);
        var state = SynGuard.DecisionState(request);
        Assert.Contains("User's own words (verbatim): the person's words", state, StringComparison.Ordinal);
        Assert.Contains("Task (the assistant's restatement): the agent's restatement", state, StringComparison.Ordinal);
    }

    [Fact]
    public void Words_equal_to_the_task_are_shown_once_and_no_words_keep_the_old_form()
    {
        var calls = new[] { new SynGuardCall("probe", "{}") };

        var chat = SynGuard.DecisionState(new SynGuardRequest("same text", calls, HumanWords: "same text"));
        var none = SynGuard.DecisionState(new SynGuardRequest("task only", calls));
        var message = SynGuard.BuildUserMessage(new SynGuardRequest("derived", calls, HumanWords: "own"));

        Assert.Equal(1, Regex.Count(chat, "same text"));
        Assert.StartsWith("User's own words (verbatim): same text", chat, StringComparison.Ordinal);
        Assert.StartsWith("Task: task only", none, StringComparison.Ordinal);
        Assert.Contains("The owner's own words:", message, StringComparison.Ordinal);
        Assert.Contains("Task the assistant derived from them:", message, StringComparison.Ordinal);
    }

    // ───────────────────────── промпты ─────────────────────────

    public static TheoryData<string, string> ModelTexts => new()
    {
        { nameof(ComputerOperator.SystemPrompt), ComputerOperator.SystemPrompt },
        { nameof(ComputerOperator.AgentRule), ComputerOperator.AgentRule },
        { nameof(ChatEngine.ComputerRules), ChatEngine.ComputerRules },
        { nameof(ScheduleReport.ComputerRule), ScheduleReport.ComputerRule },
        { "use_computer", Describe(new UseComputerTool((_, _) => Task.FromResult(ToolResult.Ok("")))) },
        { "computer", Describe(new ComputerTool(new ComputerSession(new FakeComputer(), null, new ComputerStart(ComputerMode.Do, false, 1, ""), new ComputerLease()))) },
        { nameof(SynGuard.ApprovalInstructions), SynGuard.ApprovalInstructions + SynGuard.WithoutApprovalCriteria + SynGuard.ApprovalRequiredCriteria }
    };

    [Theory]
    [MemberData(nameof(ModelTexts))]
    public void What_the_models_read_states_rules_and_names_no_case(string name, string text)
    {
        foreach (var marker in ExampleMarkers)
        {
            Assert.False(text.Contains(marker, StringComparison.OrdinalIgnoreCase), $"{name}: «{marker}»");
        }

        Assert.False(Cyrillic().IsMatch(text), $"{name}: кириллица");
        Assert.DoesNotContain("—", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_saved_copy_of_the_previous_agent_prompt_is_recognised_and_the_new_one_has_no_blanket_ban()
    {
        Assert.Contains(LegacyAgentPrompts.All, legacy => legacy.Contains("You CANNOT click in Windows GUI", StringComparison.Ordinal));
        Assert.DoesNotContain("You CANNOT click", Agent.BaseSystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(LegacyAgentPrompts.All, legacy => legacy == Agent.BaseSystemPrompt);
    }

    [Fact]
    public void The_chat_learns_about_program_windows_only_when_the_switch_is_on()
    {
        var on = Settings(computerControl: true);
        var off = Settings(computerControl: false);

        Assert.Contains("PROGRAM WINDOWS", Engine(on).CurrentSystemPrompt(new ChatSession()), StringComparison.Ordinal);
        Assert.DoesNotContain("PROGRAM WINDOWS", Engine(off).CurrentSystemPrompt(new ChatSession()), StringComparison.Ordinal);

        static ChatEngine Engine(AppSettings settings)
        {
            var options = new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "m", MaxToolRounds = 3 };
            return new ChatEngine(
                new VeniceClient(new HttpClient { BaseAddress = new Uri("https://api.venice.ai/api/v1/") }, options),
                options,
                () => settings,
                new ToolRegistry([]),
                agents: null);
        }
    }

    [Theory]
    [InlineData("vision-model", "vision-model")]
    [InlineData("", AppSettings.FactoryHeavyModelId)]
    [InlineData("auto", AppSettings.FactoryHeavyModelId)]
    public void An_empty_slot_falls_back_to_the_factory_family(string chosen, string expected)
    {
        var settings = AppSettings.CreateDefault();
        settings.ComputerModelId = chosen;

        Assert.Equal(expected, AgentHost.ComputerModel(settings, LlmProvider.Venice));
    }

    [Fact]
    public void Only_models_that_see_pictures_are_offered_for_the_operator()
    {
        static VeniceModelInfo Model(string id, bool vision) => new()
        {
            Id = id,
            ModelSpec = new VeniceModelSpec { Capabilities = new VeniceModelCapabilities { SupportsVision = vision, SupportsFunctionCalling = true } }
        };

        var catalog = new[] { Model("blind", false), Model("sighted", true) };

        Assert.Equal(["sighted"], VeniceModelCatalog.ForSlot(catalog, ModelSlot.Computer).Select(model => model.Id));
        Assert.Equal(2, VeniceModelCatalog.ForSlot(catalog, ModelSlot.AgentLite).Count);
    }

    private static string Describe(ITool tool) => tool.Description + tool.ParametersSchema.GetRawText();

    private static string JsonEncoded(string text) => JsonSerializer.Serialize(text)[1..^1];

    [GeneratedRegex("\"type\":\"image_url\"")]
    private static partial Regex ImageUrl();

    [GeneratedRegex("[Ѐ-ӿ]")]
    private static partial Regex Cyrillic();

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

    private sealed class ProbeTool : ITool
    {
        public string Name => "probe";

        public string Description => "stub";

        public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Ok("probed"));
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
}
