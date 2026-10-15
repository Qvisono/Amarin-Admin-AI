using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

public sealed class DecisionRoutingTests
{
    private const string DecisionId = "openrouter:typesafe/jev";
    private const string ChatId = "openrouter:vendor/chat";
    private const string CatalogJson = """
        {"data":[
          {"id":"typesafe/jev","architecture":{"input_modalities":["text"],"output_modalities":["decisions"]}},
          {"id":"vendor/chat","architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["tools"]},
          {"id":"vendor/voice","architecture":{"input_modalities":["text"],"output_modalities":["audio"]},"supported_parameters":["tools"]},
          {"id":"vendor/image-decision","architecture":{"input_modalities":["image"],"output_modalities":["decisions"]}},
          {"id":"vendor/plain","architecture":{"input_modalities":["text"],"output_modalities":["text"]}}
        ]}
        """;

    private static VeniceModelInfo DecisionModel => new() { Id = DecisionId, Type = "decisions" };

    private static IReadOnlyList<VeniceModelInfo> Models() => OpenRouterMapper.ToModels(
        JsonSerializer.Deserialize(CatalogJson, VeniceJsonContext.Default.OpenRouterModelsResponse)!);

    [Fact]
    public void Decisions_are_available_only_to_router_and_synguard_even_if_they_advertise_tools()
    {
        var models = Models().Append(new VeniceModelInfo
        {
            Id = "openrouter:vendor/decision-with-tools", Type = "decisions",
            ModelSpec = new VeniceModelSpec
            {
                Capabilities = new VeniceModelCapabilities { SupportsFunctionCalling = true }
            }
        });
        var catalog = VeniceModelCatalog.FilterAgentic(models, includeDecisions: true);
        Assert.Equal(3, catalog.Count);
        Assert.Equal("decisions", catalog.Single(model => model.Id == DecisionId).Type);
        Assert.Equal([ChatId], VeniceModelCatalog.FilterAgentic(models).Select(model => model.Id));
        foreach (var slot in Enum.GetValues<ModelSlot>())
        {
            var available = VeniceModelCatalog.ForSlot(catalog, slot);
            Assert.Equal(slot is ModelSlot.Router or ModelSlot.SynGuard ? 3 : 1, available.Count);
            if (slot is not (ModelSlot.Router or ModelSlot.SynGuard))
            {
                Assert.Equal(ChatId, ModelSlotDefaults.Resolve(LlmProvider.OpenRouter, slot, catalog));
            }
        }
    }

    [Fact]
    public async Task The_shared_cache_keeps_decisions_and_model_lookup_can_resolve_them()
    {
        using var handler = new Handler((_, _) => Json(CatalogJson));
        using var http = new HttpClient(handler);
        var client = Client(http);
        var cache = new VeniceModelListCache(client);
        var result = await cache.LoadAsync(LlmProvider.OpenRouter);
        Assert.Null(result.Error);
        Assert.Contains(result.Models!, model => model.Id == DecisionId);
        Assert.True(VeniceModelCatalog.IsDecisionModel(cache.Find(DecisionId)));
    }

    [Fact]
    public void Catalog_refresh_keeps_the_router_but_repairs_decisions_in_other_slots()
    {
        var settings = new AppSettings { RouterModelId = DecisionId, TitleModelId = DecisionId };
        ModelSlots.WriteKey(settings, ModelSlot.Router, "router-key");
        var catalog = VeniceModelCatalog.FilterAgentic(Models(), includeDecisions: true);
        ModelSlotHealing.Heal(settings, LlmProvider.OpenRouter, catalog, LlmProvider.OpenRouter, _ => true, _ => true);
        Assert.Equal(DecisionId, settings.RouterModelId);
        Assert.Equal("router-key", ModelSlots.ReadKey(settings, ModelSlot.Router));
        Assert.Equal(ChatId, settings.TitleModelId);
    }

    [Fact]
    public async Task Chat_routing_sends_a_choice_request_with_the_assigned_key_and_books_cost_once()
    {
        using var handler = new Handler((request, body) =>
        {
            Assert.Equal("https://openrouter.ai/api/alpha/decisions", request.RequestUri?.AbsoluteUri);
            Assert.Equal("assigned-key", request.Headers.Authorization?.Parameter);
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            Assert.Equal("typesafe/jev", root.GetProperty("model").GetString());
            Assert.Equal("user request", root.GetProperty("state").GetString());
            var question = root.GetProperty("questions").GetProperty("tier");
            Assert.Equal("choice", question.GetProperty("type").GetString());
            Assert.StartsWith(RoutingDecisions.ChatInstructions, question.GetProperty("instructions").GetString());
            Assert.Equal(RoutingDecisions.ChatHeavy, question.GetProperty("criteria").GetProperty("heavy").GetString());
            Assert.Equal(["lite", "heavy"], question.GetProperty("criteria").EnumerateObject().Select(p => p.Name));
            Assert.False(root.TryGetProperty("messages", out _));
            Assert.False(root.TryGetProperty("reasoning", out _));
            return Json(Answer("heavy"));
        });
        using var http = new HttpClient(handler);
        var charges = new List<(string Key, VeniceCost Cost, string Sku)>();
        var client = Client(http, (key, cost, sku) => charges.Add((key, cost, sku)));
        using var charge = VeniceClient.ChargeAs(VeniceSku.Router);
        var response = await Route(client, new ApiCredential(LlmProvider.OpenRouter, "assigned-key"));
        Assert.Equal("heavy", ChatContent.ReadText(response.Choices[0].Message.Content));
        Assert.Equal(0.003m, response.ReportedCost()?.Usd);
        var booked = Assert.Single(charges);
        Assert.Equal("assigned-key", booked.Key);
        Assert.Equal(VeniceSku.Router, booked.Sku);
        Assert.Equal(40, booked.Cost.InputTokens);
        Assert.Equal(2, booked.Cost.OutputTokens);
        Assert.Equal(0.003m, client.RequestCost.Usd);
    }

    [Fact]
    public async Task A_cold_catalog_is_resolved_before_routing_an_imported_model()
    {
        using var handler = new Handler((request, _) => request.Method == HttpMethod.Get
            ? Json(CatalogJson) : Json(Answer("lite")));
        using var http = new HttpClient(handler);
        var client = Client(http);
        client.ResolveModelInfo = _ => null;
        var result = await Route(client);
        Assert.Equal("lite", ChatContent.ReadText(result.Choices[0].Message.Content));
        Assert.Equal(["/api/v1/models", "/api/alpha/decisions"], handler.Paths);
    }

    [Theory]
    [InlineData("fast", "fast")]
    [InlineData("lite", "lite")]
    [InlineData("heavy", "heavy")]
    [InlineData("heavy extra", "lite")]
    [InlineData("unknown", "lite")]
    public async Task Agent_routing_accepts_only_the_declared_tiers_and_keeps_paid_usage(string choice, string expected)
    {
        using var handler = new Handler((_, body) =>
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var question = root.GetProperty("questions").GetProperty("tier");
            Assert.StartsWith(RoutingDecisions.AgentInstructions, question.GetProperty("instructions").GetString());
            Assert.Equal(RoutingDecisions.AgentFast, question.GetProperty("criteria").GetProperty("fast").GetString());
            Assert.Equal(RoutingDecisions.AgentLite, question.GetProperty("criteria").GetProperty("lite").GetString());
            Assert.Equal(RoutingDecisions.AgentHeavy, question.GetProperty("criteria").GetProperty("heavy").GetString());
            Assert.Equal(["fast", "lite", "heavy"], root.GetProperty("questions").GetProperty("tier")
                .GetProperty("criteria").EnumerateObject().Select(p => p.Name));
            Assert.Contains("agent task", root.GetProperty("state").GetString());
            return Json(Answer(choice));
        });
        using var http = new HttpClient(handler);
        var result = await AgentTierRouter.DecideAsync(Client(http), DecisionId, "MODELS", "agent task",
            null, new ReasoningSettings().ToChoice(), CancellationToken.None);
        Assert.Equal(expected, result.Tier);
        Assert.Equal(0.003m, result.Cost?.Usd);
    }

    [Theory]
    [InlineData(400, "{}")]
    [InlineData(429, "{}")]
    [InlineData(200, "invalid JSON")]
    [InlineData(200, "{\"error\":{\"message\":\"unavailable\"}}")]
    public async Task Decision_failures_use_the_existing_agent_fallback(int status, string body)
    {
        using var handler = new Handler((_, _) => new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent(body) });
        using var http = new HttpClient(handler);
        var result = await AgentTierRouter.DecideAsync(Client(http), DecisionId, "MODELS", "task",
            null, new ReasoningSettings().ToChoice(), CancellationToken.None);
        Assert.Equal(AgentTierRouter.FallbackTier, result.Tier);
        Assert.Equal(0m, result.Cost?.Usd);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed_by_decision_routing()
    {
        using var handler = new Handler((_, _) => throw new OperationCanceledException());
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AgentTierRouter.DecideAsync(Client(http),
            DecisionId, "MODELS", "task", null, new ReasoningSettings().ToChoice(), CancellationToken.None));
    }

    [Fact]
    public async Task The_spending_gate_stops_a_decision_before_any_paid_request()
    {
        using var handler = new Handler((_, _) => Json(Answer("heavy")));
        using var http = new HttpClient(handler);
        var client = new VeniceClient(http, new AgentOptions
        {
            Binding = new ApiCredential(LlmProvider.OpenRouter, "limited-key"),
            Model = DecisionId,
            SpendGate = (key, _) =>
            {
                Assert.Equal("limited-key", key.Secret);
                throw new InvalidOperationException("spending blocked");
            }
        }) { ResolveModelInfo = _ => DecisionModel };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Route(client));
        Assert.Empty(handler.Paths);
        Assert.Equal(0m, client.RequestCost.Usd);
    }

    private static VeniceClient Client(HttpClient http, Action<string, VeniceCost, string>? spend = null) =>
        new(http, new AgentOptions
        {
            Binding = new ApiCredential(LlmProvider.OpenRouter, "default-key"), Model = DecisionId, SpendSink = spend
        }) { ResolveModelInfo = id => id == DecisionId ? DecisionModel : null };

    private static Task<ChatCompletionResponse> Route(VeniceClient client, ApiCredential? credential = null) =>
        client.CreateRoutingCompletionAsync(DecisionId, "routing instructions", "user request", ["lite", "heavy"],
            RoutingDecisions.ChatQuestion("MODELS"),
            new VeniceParameters(), CancellationToken.None, new ReasoningSettings().ToChoice(), credential);

    private static string Answer(string choice) => $$$"""
        {"answers":{"tier":{"type":"choice","choice":"{{{choice}}}"}},
         "usage":{"cost":0.003,"input_tokens":40,"output_tokens":2}}
        """;

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return respond(request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        }
    }
}
