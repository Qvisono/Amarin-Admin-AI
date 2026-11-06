using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>Разделение счёта очереди на границе инструментов, при сбое и после продолжения.</summary>
public sealed class QueuedCostRecoveryTests
{
    private const string Model = "grok-4-6";

    [Fact]
    public async Task A_paid_tool_stays_on_the_answer_before_the_queued_message()
    {
        var spent = new ConcurrentBag<VeniceCost>();
        var options = Options(spent);
        using var paidHttp = new HttpClient(new Handler((_, _) => BilledJson(0.10m)));
        var paidClient = new VeniceClient(paidHttp, options);
        var tool = new StubTool("read_file", async token =>
        {
            await paidClient.CreateChatCompletionAsync(
                Model, [new ChatMessage { Role = "user", Content = ChatContent.Text("служебный запрос") }],
                null, null, new VeniceParameters(), token);
            return ToolResult.Ok("прочитано");
        });
        using var http = new HttpClient(new Handler((index, _) =>
            index == 1 ? BilledSse(0.02m, "read_file") : BilledSse(0.03m)));
        var engine = Engine(http, options, tool);
        var observer = new QueueingObserver();
        var queued = 0;
        observer.ToolsChanged = _ =>
        {
            if (Interlocked.Increment(ref queued) == 1)
            {
                observer.Enqueue("следующий вопрос");
            }
        };
        var session = Session();

        await engine.RunTurnAsync(session, "прочитай", observer, CancellationToken.None);

        var answers = Answers(session);
        Assert.Equal(2, answers.Count);
        Assert.Equal(0.02m, answers[0].ModelCost?.Usd);
        Assert.Equal(0.12m, answers[0].Cost?.Usd);
        Assert.Equal(0.10m, Assert.Single(Assert.Single(answers[0].ToolRounds).Calls).Cost?.Usd);
        Assert.Equal(0.03m, answers[1].ModelCost?.Usd);
        Assert.Equal(0.03m, answers[1].Cost?.Usd);
        Assert.Empty(answers[1].ToolRounds);
        Assert.Equal(0.15m, ChatCost.Total(session));
        Assert.Equal(0.15m, spent.Sum(cost => cost.Usd));
    }

    [Fact]
    public async Task An_agent_and_its_guard_belong_only_to_the_answer_that_started_them()
    {
        var spent = new ConcurrentBag<VeniceCost>();
        var options = Options(spent);
        using var agentHttp = new HttpClient(new Handler((_, _) => BilledJson(0.30m)));
        var agentClient = new VeniceClient(agentHttp, options);
        var tool = new StubTool("init_agent", async token =>
        {
            var scope = AgentRunScope.Current ?? throw new InvalidOperationException("Нет области инструмента.");
            // Как в AgentHost: агент платит отдельно, а защита приписывается родительскому ответу.
            using var isolated = VeniceTurnScope.Suppress();
            var response = await agentClient.CreateChatCompletionAsync(
                Model, [new ChatMessage { Role = "user", Content = ChatContent.Text("задание агента") }],
                null, null, new VeniceParameters(), token);
            scope.Call.NestedAgent = new AgentRunRecord
            {
                Cost = response.ReportedCost(),
                Status = AgentRunStatus.Complete
            };
            AgentRunScope.ChargeGuard(Usd(0.004m));
            return ToolResult.Ok("отчёт агента");
        });
        using var http = new HttpClient(new Handler((index, _) =>
            index == 1 ? BilledSse(0.02m, "init_agent") : BilledSse(0.03m)));
        var engine = Engine(http, options, tool);
        var observer = new QueueingObserver();
        var queued = 0;
        observer.ToolsChanged = _ =>
        {
            if (Interlocked.Increment(ref queued) == 1)
            {
                observer.Enqueue("следующий вопрос");
            }
        };
        var session = Session();

        await engine.RunTurnAsync(session, "позови агента", observer, CancellationToken.None);

        var answers = Answers(session);
        Assert.Equal(2, answers.Count);
        Assert.Equal(0.02m, answers[0].ModelCost?.Usd);
        Assert.Equal(0.324m, answers[0].Cost?.Usd);
        Assert.Equal(0.30m, Assert.Single(Assert.Single(answers[0].ToolRounds).Calls).NestedAgent?.Cost?.Usd);
        Assert.Equal(0.004m, answers[0].GuardCost?.Usd);
        Assert.Equal(0.03m, answers[1].ModelCost?.Usd);
        Assert.Equal(0.03m, answers[1].Cost?.Usd);
        Assert.Null(answers[1].GuardCost);
        Assert.Empty(answers[1].ToolRounds);
        Assert.Equal(0.354m, ChatCost.Total(session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_queued_answer_does_not_inherit_the_completed_answers_bill(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var spent = new ConcurrentBag<VeniceCost>();
        using var http = new HttpClient(new Handler((index, token) =>
        {
            if (index == 1)
            {
                return BilledSse(0.02m);
            }

            if (cancel)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }

            throw new InvalidOperationException("Тестовый сбой следующего запроса.");
        }));
        var engine = Engine(http, Options(spent));
        var observer = new QueueingObserver();
        var completed = 0;
        observer.Completed = _ =>
        {
            if (Interlocked.Increment(ref completed) == 1)
            {
                observer.Enqueue("ещё вопрос");
            }
        };
        var session = Session();

        await engine.RunTurnAsync(session, "первый вопрос", observer, cancellation.Token);

        var answers = Answers(session);
        Assert.Equal(2, answers.Count);
        Assert.Equal(AssistantStatus.Complete, answers[0].Status);
        Assert.Equal(0.02m, answers[0].Cost?.Usd);
        Assert.Equal(263, answers[0].ModelCost?.OutputTokens);
        Assert.Equal(cancel ? AssistantStatus.Cancelled : AssistantStatus.Error, answers[1].Status);
        Assert.Equal(0m, answers[1].Cost?.Usd ?? 0m);
        Assert.Equal(0, answers[1].ModelCost?.OutputTokens ?? 0);
        Assert.Equal(0.02m, ChatCost.Total(session));
        Assert.Equal(0.02m, spent.Sum(cost => cost.Usd));
    }

    [Fact]
    public async Task Resuming_keeps_the_old_bill_and_starts_a_fresh_bill_for_the_next_queued_answer()
    {
        var spent = new ConcurrentBag<VeniceCost>();
        using var http = new HttpClient(new Handler((index, _) =>
            BilledSse(index == 1 ? 0.02m : 0.03m)));
        var engine = Engine(http, Options(spent));
        var session = Session();
        var interrupted = new ChatDisplayMessage
        {
            Role = "assistant",
            Id = "interrupted",
            RequestedModelId = Model,
            ResolvedModelId = Model,
            Status = AssistantStatus.Cancelled,
            Duration = TimeSpan.FromSeconds(42),
            ModelCost = new VeniceCost { Usd = 0.05m, HasData = true, InputTokens = 100, OutputTokens = 10 },
            Cost = Usd(0.15m),
            ToolRounds =
            [
                new ToolRound
                {
                    Calls =
                    [
                        new ToolCallRecord
                        {
                            Id = "paid-tool", Name = "generate_image", Status = ToolCallStatus.Done,
                            Success = true, Cost = Usd(0.10m)
                        }
                    ]
                }
            ]
        };
        session.Messages.Add(interrupted);
        session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text("продолжи") });
        var observer = new QueueingObserver();
        var completed = 0;
        observer.Completed = _ =>
        {
            if (Interlocked.Increment(ref completed) == 1)
            {
                observer.Enqueue("новый вопрос после продолжения");
            }
        };

        await engine.ResumeAssistantAsync(session, interrupted, observer, CancellationToken.None);

        var answers = Answers(session);
        Assert.Equal(2, answers.Count);
        Assert.Same(interrupted, answers[0]);
        Assert.Equal(0.07m, interrupted.ModelCost?.Usd);
        Assert.Equal(0.17m, interrupted.Cost?.Usd);
        Assert.Equal(273, interrupted.ModelCost?.OutputTokens);
        Assert.True(interrupted.Duration >= TimeSpan.FromSeconds(42));
        Assert.Equal(0.03m, answers[1].ModelCost?.Usd);
        Assert.Equal(0.03m, answers[1].Cost?.Usd);
        Assert.Equal(263, answers[1].ModelCost?.OutputTokens);
        Assert.True(answers[1].Duration < TimeSpan.FromSeconds(42));
        Assert.Equal(0.20m, ChatCost.Total(session));
        Assert.Equal(0.05m, spent.Sum(cost => cost.Usd));
    }

    [Fact]
    public async Task Cancelling_after_a_free_completion_does_not_add_the_existing_extras_twice()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"готово\"},\"finish_reason\":\"stop\"}]}\ndata: [DONE]\n",
                Encoding.UTF8, "text/event-stream")
        }));
        var engine = Engine(http, Options(new ConcurrentBag<VeniceCost>()));
        var session = Session();
        var interrupted = new ChatDisplayMessage
        {
            Role = "assistant", Id = "free-model", RequestedModelId = Model, ResolvedModelId = Model,
            Status = AssistantStatus.Cancelled, ModelCost = VeniceCost.Zero,
            TitleCost = Usd(0.001m), GuardCost = Usd(0.004m), Cost = Usd(0.305m),
            ToolRounds =
            [
                new ToolRound
                {
                    Calls =
                    [
                        new ToolCallRecord
                        {
                            Id = "agent", Name = "init_agent", Status = ToolCallStatus.Done, Success = true,
                            Cost = Usd(0.30m), NestedAgent = new AgentRunRecord { Cost = Usd(0.30m), Status = AgentRunStatus.Complete }
                        }
                    ]
                }
            ]
        };
        session.Messages.Add(interrupted);
        session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text("продолжи") });
        var observer = new QueueingObserver();
        observer.Completed = _ =>
        {
            observer.Enqueue("ещё вопрос");
            cancellation.Cancel();
        };

        await engine.ResumeAssistantAsync(session, interrupted, observer, cancellation.Token);

        Assert.Single(Answers(session));
        Assert.Equal(AssistantStatus.Cancelled, interrupted.Status);
        Assert.Equal(0m, interrupted.ModelCost?.Usd);
        Assert.Equal(0.305m, interrupted.Cost?.Usd);
        Assert.Equal(0.305m, ChatCost.Total(session));
    }

    private static AgentOptions Options(ConcurrentBag<VeniceCost> spent) => new()
    {
        ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = Model, MaxToolRounds = 4,
        SpendSink = (_, cost, _) => spent.Add(cost)
    };

    private static ChatEngine Engine(HttpClient http, AgentOptions options, params ITool[] tools)
    {
        var settings = AppSettings.CreateDefault();
        return new ChatEngine(new VeniceClient(http, options), options, () => settings, new ToolRegistry(tools));
    }

    private static ChatSession Session() => new() { Id = "queued-recovery", SelectedModelId = Model };

    private static List<ChatDisplayMessage> Answers(ChatSession session) =>
        session.Messages.Where(message => message.Role == "assistant").ToList();

    private static VeniceCost Usd(decimal amount) => new() { Usd = amount, HasData = true };

    private static HttpResponseMessage BilledJson(decimal usd) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content = "готово" } } },
            cost = new { usd, diem = 0 },
            usage = new { prompt_tokens = 1000, completion_tokens = 263, total_tokens = 1263 }
        }), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage BilledSse(decimal usd, string? tool = null)
    {
        var first = tool is null
            ? JsonSerializer.Serialize(new { model = Model, choices = new[] { new { delta = new { content = "готово" } } } })
            : JsonSerializer.Serialize(new
            {
                model = Model,
                choices = new[]
                {
                    new
                    {
                        delta = new
                        {
                            tool_calls = new[]
                            {
                                new { index = 0, id = "call-1", type = "function", function = new { name = tool, arguments = "{}" } }
                            }
                        }
                    }
                }
            });
        var final = JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { }, finish_reason = tool is null ? "stop" : "tool_calls" } },
            cost = new { usd, diem = 0 },
            usage = new { prompt_tokens = 1000, completion_tokens = 263, total_tokens = 1263 }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"data: {first}\ndata: {final}\ndata: [DONE]\n", Encoding.UTF8, "text/event-stream")
        };
    }

    private sealed class Handler(Func<int, CancellationToken, HttpResponseMessage> script) : HttpMessageHandler
    {
        private int _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(script(Interlocked.Increment(ref _requests), cancellationToken));
    }

    private sealed class StubTool(string name, Func<CancellationToken, Task<ToolResult>> execute) : ITool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        public string Name => name;
        public string Description => "Подставной инструмент для проверки счёта.";
        public JsonElement ParametersSchema => Schema;

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) => execute(cancellationToken);
    }

    private sealed class QueueingObserver : IChatTurnObserver
    {
        private readonly ConcurrentQueue<string> _queued = new();

        public Action<ChatDisplayMessage>? ToolsChanged { get; set; }
        public Action<ChatDisplayMessage>? Completed { get; set; }

        public void Enqueue(string text) => _queued.Enqueue(text);
        public bool TryTakeQueuedMessage(out string text) => _queued.TryDequeue(out text!);
        public void OnUserAppended(ChatDisplayMessage user) { }
        public void OnAssistantStarted(ChatDisplayMessage assistant) { }
        public void OnAssistantText(ChatDisplayMessage assistant) { }
        public void OnToolsChanged(ChatDisplayMessage assistant) => ToolsChanged?.Invoke(assistant);
        public void OnAssistantCompleted(ChatDisplayMessage assistant) => Completed?.Invoke(assistant);
        public void OnAssistantCancelled(ChatDisplayMessage assistant) { }
        public void OnError(string message) { }
    }
}
