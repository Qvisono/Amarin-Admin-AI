using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>Дописанные сообщения оплачены по одному разу, даже когда их исполняет один ход.</summary>
public sealed class QueuedCostTests
{
    [Fact]
    public async Task Ten_queued_answers_each_keep_only_their_own_charge()
    {
        using var rig = new Rig((_, _) => Sse(0.01m));
        var observer = new QueueingObserver(10);

        await rig.Engine.RunTurnAsync(rig.Session, "первый вопрос", observer, CancellationToken.None);

        var answers = Answers(rig.Session);
        Assert.Equal(10, answers.Count);
        Assert.Equal(10, rig.Handler.Requests);
        Assert.Equal(10, rig.Spent.Count);
        Assert.All(answers, answer =>
        {
            AssertCost(answer.Cost, 0.01m);
            AssertCost(answer.ModelCost, 0.01m);
            Assert.Equal(AssistantStatus.Complete, answer.Status);
        });
        Assert.Equal(0.10m, ChatCost.Total(rig.Session));
        Assert.Equal(rig.Spent.Sum(cost => cost.Usd), ChatCost.Total(rig.Session));
        Assert.Equal(10_000, answers.Sum(answer => answer.Cost?.InputTokens ?? 0));
        Assert.Equal(2630, answers.Sum(answer => answer.Cost?.OutputTokens ?? 0));
        observer.AssertCompletedBillsUnchanged();
    }

    [Fact]
    public async Task Saving_and_reloading_preserves_the_individual_queued_bills()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-queued-cost-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var rig = new Rig((_, _) => Sse(0.01m));
            await rig.Engine.RunTurnAsync(rig.Session, "первый вопрос", new QueueingObserver(3), CancellationToken.None);
            var store = new ChatStore(root);
            store.Save(rig.Session);

            var loaded = store.TryLoad(rig.Session.Id);

            Assert.NotNull(loaded);
            Assert.Equal(3, Answers(loaded).Count);
            Assert.All(Answers(loaded), answer =>
            {
                AssertCost(answer.Cost, 0.01m);
                AssertCost(answer.ModelCost, 0.01m);
            });
            Assert.Equal(0.03m, ChatCost.Total(loaded));
            Assert.Equal(rig.Spent.Sum(cost => cost.Usd), ChatCost.Total(loaded));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Auto_routing_is_paid_once_and_belongs_only_to_the_first_answer()
    {
        var routed = 0;
        using var rig = new Rig((_, body) =>
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.GetProperty("model").GetString() == "router-model")
            {
                routed++;
                return Json("""
                    {"choices":[{"message":{"role":"assistant","content":"lite"}}],
                     "usage":{"prompt_tokens":50,"completion_tokens":1,"total_tokens":51},"cost":{"usd":0.003}}
                    """);
            }

            return Sse(0.01m);
        }, model: "auto");
        rig.Settings.LiteModelId = "grok-4-6";
        rig.Settings.RouterModelId = "router-model";
        var observer = new QueueingObserver(3);

        await rig.Engine.RunTurnAsync(rig.Session, "первый вопрос", observer, CancellationToken.None);

        var answers = Answers(rig.Session);
        Assert.Equal(3, answers.Count);
        Assert.Equal(1, routed);
        Assert.Equal(4, rig.Handler.Requests);
        AssertCost(answers[0].RouterCost, 0.003m, input: 0, output: 0);
        var firstModelCost = answers[0].ModelCost;
        Assert.NotNull(firstModelCost);
        Assert.Equal(0.01m, firstModelCost.Usd);
        AssertCost(answers[0].Cost, 0.013m, input: 1050, output: 264);
        Assert.All(answers.Skip(1), answer =>
        {
            Assert.Null(answer.RouterCost);
            AssertCost(answer.Cost, 0.01m);
            AssertCost(answer.ModelCost, 0.01m);
        });
        Assert.Equal(0.033m, ChatCost.Total(rig.Session));
        Assert.Equal(rig.Spent.Sum(cost => cost.Usd), ChatCost.Total(rig.Session));
        observer.AssertCompletedBillsUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reported_price_does_not_inherit_the_previous_answers_estimated_flag(bool reportedFirst)
    {
        using var rig = new Rig(
            (index, _) => Sse((index == 1) == reportedFirst ? 0.02m : null),
            model: "openai:gpt-6-sol", provider: LlmProvider.OpenAI);
        rig.Client.ResolveModelInfo = _ => new VeniceModelInfo
        {
            Id = "openai:gpt-6-sol",
            ModelSpec = new VeniceModelSpec
            {
                Pricing = new VeniceModelPricing
                {
                    Input = new VeniceUnitPrice { Usd = 10m },
                    Output = new VeniceUnitPrice { Usd = 20m }
                }
            }
        };
        var observer = new QueueingObserver(2);

        await rig.Engine.RunTurnAsync(rig.Session, "первый вопрос", observer, CancellationToken.None);

        var answers = Answers(rig.Session);
        Assert.Equal(2, answers.Count);
        for (var index = 0; index < answers.Count; index++)
        {
            var reported = (index == 0) == reportedFirst;
            // При оценке: (1000 × 10 + 263 × 20) / 1 000 000.
            AssertCost(answers[index].Cost, reported ? 0.02m : 0.01526m, estimated: !reported);
            AssertCost(answers[index].ModelCost, reported ? 0.02m : 0.01526m, estimated: !reported);
        }

        Assert.Equal(0.03526m, ChatCost.Total(rig.Session));
        Assert.Equal(rig.Spent.Sum(cost => cost.Usd), ChatCost.Total(rig.Session));
        observer.AssertCompletedBillsUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_answer_with_tokens_only_does_not_inherit_an_earlier_price(bool firstHasPrice)
    {
        using var rig = new Rig((index, _) => Sse(index == 1 && firstHasPrice ? 0.01m : null));
        var observer = new QueueingObserver(2);

        await rig.Engine.RunTurnAsync(rig.Session, "первый вопрос", observer, CancellationToken.None);

        var answers = Answers(rig.Session);
        Assert.Equal(2, answers.Count);
        AssertCost(answers[0].Cost, firstHasPrice ? 0.01m : 0m, hasData: firstHasPrice);
        AssertCost(answers[1].Cost, 0m, hasData: false);
        AssertCost(answers[1].ModelCost, 0m, hasData: false);
        Assert.Equal(2000, rig.Spent.Sum(cost => cost.InputTokens));
        Assert.Equal(526, rig.Spent.Sum(cost => cost.OutputTokens));
        Assert.Equal(rig.Spent.Sum(cost => cost.Usd), ChatCost.Total(rig.Session));
        observer.AssertCompletedBillsUnchanged();
    }

    private static List<ChatDisplayMessage> Answers(ChatSession session) =>
        session.Messages.Where(message => message.Role == "assistant").ToList();

    private static void AssertCost(
        VeniceCost? cost,
        decimal usd,
        long input = 1000,
        long output = 263,
        bool estimated = false,
        bool hasData = true)
    {
        Assert.NotNull(cost);
        Assert.Equal(usd, cost.Usd);
        Assert.Equal(0m, cost.Diem);
        Assert.Equal(input, cost.InputTokens);
        Assert.Equal(output, cost.OutputTokens);
        Assert.Equal(estimated, cost.Estimated);
        Assert.Equal(hasData, cost.HasData);
    }

    private static HttpResponseMessage Sse(decimal? usd)
    {
        var final = JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 1000, completion_tokens = 263, total_tokens = 1263 },
            cost = usd is { } amount ? new { usd = amount } : null
        });
        var payload = "data: {\"choices\":[{\"delta\":{\"content\":\"готово\"}}]}\n"
                      + "data: " + final + "\ndata: [DONE]\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Rig : IDisposable
    {
        private readonly HttpClient _http;

        public ScriptedHandler Handler { get; }
        public List<VeniceCost> Spent { get; } = [];
        public AppSettings Settings { get; } = AppSettings.CreateDefault();
        public VeniceClient Client { get; }
        public ChatEngine Engine { get; }
        public ChatSession Session { get; }

        public Rig(Func<int, string, HttpResponseMessage> script, string model = "grok-4-6", LlmProvider? provider = null)
        {
            Handler = new ScriptedHandler(script);
            _http = new HttpClient(Handler);
            var options = new AgentOptions
            {
                Keys = new ApiKeyProvider("test", provider ?? LlmProvider.Venice),
                BaseUrl = "https://api.venice.ai/api/v1",
                Model = model,
                MaxToolRounds = 2,
                SpendSink = (_, cost, _) => Spent.Add(cost)
            };
            Settings.SynGuardEnabled = false;
            Client = new VeniceClient(_http, options);
            Engine = new ChatEngine(Client, options, () => Settings, new ToolRegistry([]));
            Session = new ChatSession { Id = Guid.NewGuid().ToString("N"), SelectedModelId = model };
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class ScriptedHandler(Func<int, string, HttpResponseMessage> script) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return script(++Requests, body);
        }
    }

    private readonly record struct CostSnapshot(decimal Usd, decimal Diem, bool HasData, bool Estimated, long Input, long Output)
    {
        public static CostSnapshot Of(VeniceCost? cost)
        {
            Assert.NotNull(cost);
            return new CostSnapshot(cost.Usd, cost.Diem, cost.HasData, cost.Estimated, cost.InputTokens, cost.OutputTokens);
        }
    }

    private sealed class QueueingObserver(int answers) : IChatTurnObserver
    {
        private readonly Queue<string> _queued = new();
        private readonly List<(ChatDisplayMessage Message, CostSnapshot Cost, CostSnapshot Model)> _completed = [];

        public void AssertCompletedBillsUnchanged()
        {
            Assert.Equal(answers, _completed.Count);
            foreach (var (message, cost, model) in _completed)
            {
                Assert.Equal(cost, CostSnapshot.Of(message.Cost));
                Assert.Equal(model, CostSnapshot.Of(message.ModelCost));
            }
        }

        public void OnAssistantCompleted(ChatDisplayMessage assistant)
        {
            _completed.Add((assistant, CostSnapshot.Of(assistant.Cost), CostSnapshot.Of(assistant.ModelCost)));
            if (_completed.Count < answers)
            {
                _queued.Enqueue("следующий вопрос");
            }
        }

        public bool TryTakeQueuedMessage(out string text)
        {
            if (_queued.TryDequeue(out var queued))
            {
                text = queued;
                return true;
            }

            text = "";
            return false;
        }

        public void OnUserAppended(ChatDisplayMessage user) { }
        public void OnAssistantStarted(ChatDisplayMessage assistant) { }
        public void OnAssistantText(ChatDisplayMessage assistant) { }
        public void OnToolsChanged(ChatDisplayMessage assistant) { }
        public void OnAssistantCancelled(ChatDisplayMessage assistant) { }
        public void OnError(string message) => throw new InvalidOperationException(message);
    }
}
