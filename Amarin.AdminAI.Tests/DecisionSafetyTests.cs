using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

public sealed class DecisionSafetyTests
{
    private const string Model = "openrouter:microsoft/microsoft-decision-1";

    [Theory]
    [InlineData(1, true)]
    [InlineData(0.8, true)]
    [InlineData(0.5, false)]
    [InlineData(0, false)]
    public async Task Noul_true_allows_safety_and_normal_approval_independently(double value, bool expected)
    {
        var calls = 0;
        var charges = new List<VeniceCost>();
        using var http = new HttpClient(new Handler((request, body) =>
        {
            calls++;
            Assert.Equal("/api/alpha/decisions", request.RequestUri?.AbsolutePath);
            Assert.Equal("guard-key", request.Headers.Authorization?.Parameter);
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            Assert.Equal("microsoft/microsoft-decision-1", root.GetProperty("model").GetString());
            var questions = root.GetProperty("questions");
            Assert.Equal(2, questions.EnumerateObject().Count());
            Assert.Equal("noul", questions.GetProperty("safe_to_run").GetProperty("type").GetString());
            Assert.Contains("deleting or encrypting data beyond what the request asks for", questions.GetProperty("safe_to_run")
                .GetProperty("criteria").GetProperty("false").GetString());
            Assert.Contains("Task:", root.GetProperty("state").GetString());
            Assert.Contains("Context:", root.GetProperty("state").GetString());
            Assert.Contains("END_OF_SCRIPT", root.GetProperty("state").GetString());
            return Json(JsonSerializer.Serialize(new
            {
                answers = new
                {
                    safe_to_run = new { type = "noul", noul = value },
                    safe_without_approval = new { type = "noul", noul = 1 - value }
                },
                usage = new { cost = 0.002m, input_tokens = 50, output_tokens = 0 }
            }));
        }));
        var client = Client(http, (_, cost, sku) => { Assert.Equal(VeniceSku.Guard, sku); charges.Add(cost); });
        var checker = new SynGuardChecker(client, Model, new ReasoningChoice(true, null), true, true);
        var report = await checker.CheckAsync(new SynGuardRequest("Inspect the computer",
            [new SynGuardCall("run_powershell", new string('x', 5000) + "END_OF_SCRIPT")], "The agent is inspecting the computer."), CancellationToken.None);
        Assert.Equal(expected, report.IsSafe(0));
        Assert.Equal(value < 0.5, Assert.Single(report.WithoutApproval!));
        Assert.Equal(!expected, report.IsBlocked(0));
        Assert.Equal(SynGuardOutcome.Checked, report.Outcome);
        Assert.Equal(1, calls);
        Assert.Equal(0.002m, Assert.Single(charges).Usd);
        Assert.Equal(0.002m, report.Cost?.Usd);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"answers\":{\"safe_to_run\":{\"type\":\"noul\",\"noul\":2}}}")]
    [InlineData("{\"answers\":{\"safe_to_run\":{\"type\":\"choice\",\"choice\":\"true\"}}}")]
    [InlineData("not JSON")]
    public async Task Invalid_answers_never_allow_execution_or_skip_confirmation(string response)
    {
        using var http = new HttpClient(new Handler((_, _) => Json(response)));
        var checker = new SynGuardChecker(Client(http), Model, new ReasoningChoice(true, null), true, true);
        var report = await checker.CheckAsync(Request(), CancellationToken.None);
        Assert.False(report.IsSafe(0));
        Assert.False(Assert.Single(report.WithoutApproval!));

        // Не разрешает и не запрещает молча: без ответа вызов решает человек.
        Assert.False(report.IsBlocked(0));
        Assert.True(report.IsUnchecked(0));
        Assert.NotEqual(SynGuardOutcome.Checked, report.Outcome);
    }

    /// <summary>
    /// Проверку не на чем провести (нет ключа OpenRouter, сеть): в beta.1 это запрещало всю запись
    /// без вопроса, и агент у человека с одним ключом Venice не мог сделать ничего.
    /// </summary>
    [Fact]
    public async Task A_failed_decision_request_sends_the_call_to_the_human()
    {
        using var http = new HttpClient(new Handler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("no key") }));
        var report = await new SynGuardChecker(Client(http), Model, new ReasoningChoice(true, null), true, true)
            .CheckAsync(Request(), CancellationToken.None);
        Assert.Equal(SynGuardOutcome.Failed, report.Outcome);
        Assert.False(report.IsSafe(0));
        Assert.False(report.IsBlocked(0));
        Assert.True(report.IsUnchecked(0));
    }

    [Fact]
    public async Task Normal_mode_still_reviews_when_synguard_is_disabled_and_network_failure_requires_confirmation()
    {
        using var http = new HttpClient(new Handler((request, body) =>
        {
            using var json = JsonDocument.Parse(body);
            var questions = json.RootElement.GetProperty("questions");
            Assert.False(questions.TryGetProperty("safe_to_run", out _));
            Assert.True(questions.TryGetProperty("safe_without_approval", out _));
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") };
        }));
        var report = await new SynGuardChecker(Client(http), Model, new ReasoningChoice(true, null), false, true)
            .CheckAsync(Request(), CancellationToken.None);
        Assert.True(report.IsSafe(0));
        Assert.False(report.IsBlocked(0));
        Assert.False(Assert.Single(report.WithoutApproval!));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Normal_review_controls_the_question_and_cannot_be_confused_with_safety(bool withoutApproval, bool asks)
    {
        var settings = new AppSettings { ApprovalMode = ApprovalMode.Normal };
        var check = ToolGate.Check("unknown_action", JsonSchema.Parse("{}"), settings);
        check = ToolGate.ApplyNormalReview(check, settings, withoutApproval);
        var questions = 0;
        var decision = await ToolGate.DecideAsync(check, (_, _) =>
        {
            questions++;
            return Task.FromResult(new ConfirmationAnswer(false, ApprovalSource.Human));
        }, false, CancellationToken.None);
        Assert.Equal(asks ? 1 : 0, questions);
        Assert.Equal(!asks, decision.Allowed);
        if (asks)
        {
            Assert.True(check.Question!.AlwaysAsk);
        }
    }

    /// <summary>
    /// «Можно без вопроса» снимает только вопрос, который правила задают по усмотрению. Отложенную
    /// команду и возврат программ выполнят потом без человека — согласие берётся сейчас, — а что
    /// делает инструмент MCP, модель защиты знать не может. В beta.2 модель снимала и их.
    /// </summary>
    [Theory]
    [InlineData("deferred_task", """{"action":"run_command","title":"t","command":"New-Item -ItemType Directory -Path C:/Temp/x"}""")]
    [InlineData("deferred_task", """{"action":"restore_programs","title":"t"}""")]
    [InlineData("server__do_thing", """{"x":1}""")]
    public async Task Model_approval_never_lifts_a_question_the_rules_always_ask(string tool, string arguments)
    {
        var settings = new AppSettings { ApprovalMode = ApprovalMode.Normal };
        var check = ToolGate.Check(tool, JsonSchema.Parse(arguments), settings);
        Assert.NotNull(check.Question);

        var questions = 0;
        var decision = await ToolGate.DecideAsync(ToolGate.ApplyNormalReview(check, settings, withoutApproval: true), (_, _) =>
        {
            questions++;
            return Task.FromResult(new ConfirmationAnswer(false, ApprovalSource.Human));
        }, false, CancellationToken.None);

        Assert.Equal(1, questions);
        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Decision_approval_cannot_override_readonly_or_disabled_tools_or_ask_all()
    {
        foreach (var settings in new[]
        {
            new AppSettings { ApprovalMode = ApprovalMode.ReadOnly },
            new AppSettings { DisabledTools = ["unknown_action"] },
            new AppSettings { ApprovalMode = ApprovalMode.AskAll }
        })
        {
            var check = ToolGate.Check("unknown_action", JsonSchema.Parse("{}"), settings);
            Assert.Equal(check, ToolGate.ApplyNormalReview(check, settings, true));
        }
    }

    [Fact]
    public async Task Multiple_calls_keep_their_own_answers_and_missing_answers_do_not_grant_permission()
    {
        using var http = new HttpClient(new Handler((_, body) =>
        {
            using var json = JsonDocument.Parse(body);
            var questions = json.RootElement.GetProperty("questions");
            Assert.Equal(4, questions.EnumerateObject().Count());
            Assert.Contains("call 2", questions.GetProperty("safe_to_run_2").GetProperty("instructions").GetString());
            return Json("""
                {"answers":{
                    "safe_to_run_1":{"type":"noul","noul":1},
                    "safe_without_approval_1":{"type":"noul","noul":0},
                    "safe_without_approval_2":{"type":"noul","noul":1}
                }}
                """);
        }));
        var report = await new SynGuardChecker(Client(http), Model, new ReasoningChoice(true, null), true, true)
            .CheckAsync(new SynGuardRequest("Inspect and save a report",
                [new SynGuardCall("run_powershell", "{}"), new SynGuardCall("write_file", "{}")]), CancellationToken.None);
        Assert.Equal([true, false], report.Safe);
        Assert.Equal([false, true], report.WithoutApproval);
        Assert.Equal(SynGuardOutcome.Unparsed, report.Outcome);
        Assert.False(report.IsBlocked(1));
        Assert.True(report.IsUnchecked(1));
        Assert.False(report.IsSafe(2));
    }

    private static SynGuardRequest Request() => new("Inspect the computer", [new SynGuardCall("run_powershell", "{}")]);

    private static VeniceClient Client(HttpClient http, Action<string, VeniceCost, string>? spend = null) =>
        new(http, new AgentOptions { Binding = new ApiCredential(LlmProvider.OpenRouter, "guard-key"), Model = Model, SpendSink = spend })
        { ResolveModelInfo = _ => new VeniceModelInfo { Id = Model, Type = "decisions" } };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> script) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            script(request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
    }
}
