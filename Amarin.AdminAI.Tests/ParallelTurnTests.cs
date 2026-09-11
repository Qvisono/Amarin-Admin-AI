using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Движок и клиент Venice — по одному на приложение, и раньше они держали состояние хода прямо
/// в себе: активную модель, выбранное размышление и накопленную стоимость. Пока ход был один,
/// это сходило с рук. Эти тесты запускают два хода внахлёст и проверяют, что они друг друга
/// не перетаптывают.
/// </summary>
public sealed class ParallelTurnTests
{
    private static AgentOptions Options() => new()
    {
        ApiKey = "test",
        BaseUrl = "https://api.venice.ai/api/v1",
        Model = "grok-4-6",
        MaxToolRounds = 2
    };

    // ───────────────────────── клиент ─────────────────────────

    [Fact]
    public async Task Two_turns_on_one_client_keep_separate_bills()
    {
        var gate = new Gate(2);
        var handler = new AsyncHandler(async (_, body) =>
        {
            // Стоимость привязана к модели, чтобы её нельзя было перепутать.
            var usd = ModelOf(body) == "grok-4-6" ? 0.05m : 0.11m;
            await gate.ArriveAndWaitAsync();
            return Sse("готово", ModelOf(body), usd);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = Options();
        var client = new VeniceClient(http, options);

        var first = RunScoped(client, "grok-4-6");
        var second = RunScoped(client, "claude-sonnet-5");
        var costs = await Task.WhenAll(first, second);

        Assert.Equal(0.05m, costs[0].Usd);
        Assert.Equal(0.11m, costs[1].Usd);
    }

    private static async Task<VeniceCost> RunScoped(VeniceClient client, string model)
    {
        var turn = new VeniceTurnContext { RequestedModelId = model, ModelId = model };
        using var scope = VeniceTurnScope.Push(turn);
        await client.StreamChatCompletionAsync(
            model, model,
            [new ChatMessage { Role = "user", Content = ChatContent.Text("привет") }],
            tools: null, toolChoice: null, new VeniceParameters(), onText: null,
            CancellationToken.None, ReasoningChoice.Disabled);
        return turn.Total;
    }

    [Fact]
    public async Task A_fallback_does_not_move_the_applications_default_model()
    {
        var handler = new AsyncHandler((_, body) => Task.FromResult(
            ModelOf(body) == "claude-sonnet-5"
                ? Overloaded()
                : Sse("готово", ModelOf(body), 0m)));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = Options();
        var client = new VeniceClient(http, options);

        var streamed = await client.StreamChatCompletionAsync(
            "claude-sonnet-5", "claude-sonnet-5",
            [new ChatMessage { Role = "user", Content = ChatContent.Text("привет") }],
            tools: null, toolChoice: null, new VeniceParameters(), onText: null,
            CancellationToken.None, ReasoningChoice.Disabled);

        Assert.NotEqual("claude-sonnet-5", streamed.Model);

        // Раньше fallback писал модель в общие настройки, и соседний чат молча съезжал следом.
        Assert.Equal("grok-4-6", client.ActiveModel);
    }

    [Fact]
    public async Task The_requested_model_is_the_root_of_the_fallback_chain()
    {
        var seen = new List<string>();
        var handler = new AsyncHandler((_, body) =>
        {
            lock (seen)
            {
                seen.Add(ModelOf(body));
            }

            return Task.FromResult(Json("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ок\"}}]}"));
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = Options();
        var client = new VeniceClient(http, options);

        // Метод берёт модель параметром: перебор обязан начинаться с неё, а не с поля клиента.
        await client.CreateChatCompletionAsync(
            "kimi-k2-7-code",
            [new ChatMessage { Role = "user", Content = ChatContent.Text("привет") }],
            tools: null, toolChoice: null, new VeniceParameters(), CancellationToken.None);

        Assert.Equal("kimi-k2-7-code", seen[0]);
    }

    [Fact]
    public async Task A_suppressed_scope_keeps_its_spending_out_of_the_parent_turn()
    {
        // Так устроен вложенный агент: клиент у него свой, а стоимость попадает в ход через
        // NestedAgent.Cost. Без Suppress() те же деньги легли бы в ход второй раз.
        var handler = new AsyncHandler((_, body) =>
            Task.FromResult(Sse("готово", ModelOf(body), 0.07m)));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = Options();
        var client = new VeniceClient(http, options);

        var outer = new VeniceTurnContext { RequestedModelId = "grok-4-6", ModelId = "grok-4-6" };
        using (VeniceTurnScope.Push(outer))
        {
            using (VeniceTurnScope.Suppress())
            {
                await client.StreamChatCompletionAsync(
                    "grok-4-6", "grok-4-6",
                    [new ChatMessage { Role = "user", Content = ChatContent.Text("привет") }],
                    tools: null, toolChoice: null, new VeniceParameters(), onText: null,
                    CancellationToken.None, ReasoningChoice.Disabled);
            }
        }

        Assert.False(outer.Total.HasData);
        Assert.Equal(0m, outer.Total.Usd);
    }

    // ───────────────────────── движок ─────────────────────────

    [Fact]
    public async Task Two_chats_stream_at_once_each_on_its_own_model()
    {
        var gate = new Gate(2);
        var handler = new AsyncHandler(async (_, body) =>
        {
            var model = ModelOf(body);
            await gate.ArriveAndWaitAsync();
            return Sse("ответ", model, 0m);
        });

        var (engine, _) = Engine(handler);
        var lite = new ChatSession { Id = "a", SelectedModelId = "grok-4-6" };
        var heavy = new ChatSession { Id = "b", SelectedModelId = "claude-sonnet-5" };

        await Task.WhenAll(
            engine.RunTurnAsync(lite, "привет", new SilentObserver(), CancellationToken.None),
            engine.RunTurnAsync(heavy, "привет", new SilentObserver(), CancellationToken.None));

        // Раньше выигрывал тот, кто последним позвал SetActiveModel, — оба хода уезжали на одну модель.
        Assert.Equal("grok-4-6", lite.Messages[1].ResolvedModelId);
        Assert.Equal("claude-sonnet-5", heavy.Messages[1].ResolvedModelId);
    }

    [Fact]
    public async Task Two_chats_do_not_share_one_reasoning_choice()
    {
        var gate = new Gate(2);
        var bodies = new Dictionary<string, string>();
        var handler = new AsyncHandler(async (_, body) =>
        {
            var model = ModelOf(body);
            lock (bodies)
            {
                bodies[model] = body;
            }

            await gate.ArriveAndWaitAsync();
            return Sse("ответ", model, 0m);
        });

        var (engine, _) = Engine(handler);
        var quiet = new ChatSession { Id = "a", SelectedModelId = "grok-4-6", DisableThinking = true };
        var thinking = new ChatSession { Id = "b", SelectedModelId = "claude-sonnet-5", DisableThinking = false };

        await Task.WhenAll(
            engine.RunTurnAsync(quiet, "привет", new SilentObserver(), CancellationToken.None),
            engine.RunTurnAsync(thinking, "привет", new SilentObserver(), CancellationToken.None));

        Assert.Contains("\"disable_thinking\":true", bodies["grok-4-6"], StringComparison.Ordinal);
        Assert.DoesNotContain("\"disable_thinking\":true", bodies["claude-sonnet-5"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_chats_bill_their_own_turns()
    {
        var gate = new Gate(2);
        var handler = new AsyncHandler(async (_, body) =>
        {
            var model = ModelOf(body);
            var usd = model == "grok-4-6" ? 0.02m : 0.09m;
            await gate.ArriveAndWaitAsync();
            return Sse("ответ", model, usd);
        });

        var (engine, _) = Engine(handler);
        var first = new ChatSession { Id = "a", SelectedModelId = "grok-4-6" };
        var second = new ChatSession { Id = "b", SelectedModelId = "claude-sonnet-5" };

        await Task.WhenAll(
            engine.RunTurnAsync(first, "привет", new SilentObserver(), CancellationToken.None),
            engine.RunTurnAsync(second, "привет", new SilentObserver(), CancellationToken.None));

        // Самый острый из прежних багов: старт второго хода звал ResetRequestCost() и обнулял
        // уже накопленную стоимость первого.
        Assert.Equal(0.02m, first.Messages[1].Cost!.Usd);
        Assert.Equal(0.09m, second.Messages[1].Cost!.Usd);
    }

    [Fact]
    public async Task The_router_does_not_move_the_applications_model()
    {
        var handler = new AsyncHandler((_, body) => Task.FromResult(
            body.Contains("Classify the user request", StringComparison.Ordinal)
                ? Json("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"lite\"}}]}")
                : Sse("ответ", ModelOf(body), 0m)));

        var (engine, client) = Engine(handler);
        var session = new ChatSession { Id = "a", SelectedModelId = "auto" };

        await engine.RunTurnAsync(session, "привет", new SilentObserver(), CancellationToken.None);

        // Прежде SetActiveModel(routerId) на время маршрутизации подменял модель всему приложению.
        Assert.Equal("grok-4-6", client.ActiveModel);
    }

    [Fact]
    public void A_nested_agent_does_not_push_the_model_row_of_the_bill_negative()
    {
        var assistant = new ChatDisplayMessage { Role = "assistant", Id = "m" };
        var agentCost = new VeniceCost { Usd = 0.30m, HasData = true };
        assistant.ToolRounds.Add(new ToolRound
        {
            Calls =
            [
                new ToolCallRecord
                {
                    Id = "c1",
                    Name = "init_agent",
                    Cost = agentCost,
                    NestedAgent = new AgentRunRecord { Cost = agentCost }
                }
            ]
        });

        // Стоимость агента идёт из его собственного клиента и в счёт хода не входит — вычитать
        // её из него значило бы увести строку «Модель» в минус.
        ChatEngine.ApplyCosts(assistant, new VeniceCost { Usd = 0.04m, HasData = true });

        Assert.Equal(0.04m, assistant.ModelCost!.Usd);
        Assert.Equal(0.34m, assistant.Cost!.Usd);
    }

    // ───────────────────────── оснастка ─────────────────────────

    private static (ChatEngine Engine, VeniceClient Client) Engine(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = Options();
        var client = new VeniceClient(http, options);
        return (new ChatEngine(client, options, () => new AppSettings(), new ToolRegistry([])), client);
    }

    private static string ModelOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("model").GetString() ?? "";

    private static HttpResponseMessage Sse(string text, string model, decimal usd)
    {
        var encoded = JsonSerializer.Serialize(text);
        var usdText = usd.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var payload =
            "data: {\"model\":\"" + model + "\",\"choices\":[{\"delta\":{\"content\":" + encoded + "}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]," +
            "\"cost\":{\"usd\":" + usdText + ",\"diem\":0}}\n" +
            "data: [DONE]\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Overloaded() =>
        new(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                "{\"error\":\"Model is overloaded\"}", Encoding.UTF8, "application/json")
        };

    /// <summary>Держит запросы, пока не соберутся все, — иначе «одновременно» не воспроизвести.</summary>
    private sealed class Gate(int count)
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Task ArriveAndWaitAsync()
        {
            if (Interlocked.Increment(ref _arrived) >= count)
            {
                _open.TrySetResult();
            }

            return _open.Task;
        }
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> script)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await script(request, body);
        }
    }

    private sealed class SilentObserver : IChatTurnObserver
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
