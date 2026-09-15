using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

public sealed class Wave6ModelPickerTests
{
    private const string ModelsJson = """
        {
          "object": "list",
          "type": "text",
          "data": [
            {
              "id": "claude-sonnet-5",
              "type": "text",
              "context_length": 1000000,
              "model_spec": {
                "name": "Claude Sonnet 5",
                "availableContextTokens": 1000000,
                "offline": false,
                "traits": ["code"],
                "capabilities": {
                  "optimizedForCode": true,
                  "supportsFunctionCalling": true,
                  "supportsReasoning": true,
                  "supportsReasoningEffort": true,
                  "reasoningEffortOptions": ["low", "medium", "high"],
                  "defaultReasoningEffort": "medium",
                  "supportsVision": true
                }
              }
            },
            {
              "id": "llama-3.2-3b",
              "model_spec": {
                "name": "Llama 3.2 3B",
                "availableContextTokens": 131072,
                "capabilities": {
                  "optimizedForCode": false,
                  "supportsFunctionCalling": true,
                  "supportsReasoning": false,
                  "supportsVision": false
                }
              }
            },
            {
              "id": "qwen-vision",
              "model_spec": {
                "name": "Qwen Vision",
                "availableContextTokens": 128000,
                "capabilities": {
                  "optimizedForCode": false,
                  "supportsFunctionCalling": true,
                  "supportsReasoning": true,
                  "supportsVision": true
                }
              }
            },
            {
              "id": "kimi-k2-7-code",
              "model_spec": {
                "name": "Kimi K2.7 Code",
                "availableContextTokens": 256000,
                "traits": ["fastest", "code"],
                "capabilities": {
                  "optimizedForCode": false,
                  "supportsFunctionCalling": true,
                  "supportsReasoning": true,
                  "supportsVision": false
                }
              }
            },
            {
              "id": "grok-41-fast",
              "model_spec": {
                "name": "Grok Fast",
                "capabilities": {
                  "optimizedForCode": true,
                  "supportsFunctionCalling": true,
                  "supportsReasoning": true,
                  "supportsVision": false
                }
              }
            }
          ]
        }
        """;

    [Fact]
    public void Models_json_deserializes_and_filters_agentic_reasoning()
    {
        var list = JsonSerializer.Deserialize(ModelsJson, VeniceJsonContext.Default.VeniceModelsListResponse);
        Assert.NotNull(list);
        var agentic = VeniceModelCatalog.FilterAgentic(list.Data);
        Assert.Equal(3, agentic.Count);
        Assert.Contains(agentic, m => m.Id == "claude-sonnet-5");
        Assert.Contains(agentic, m => m.Id == "qwen-vision");
        Assert.Contains(agentic, m => m.Id == "kimi-k2-7-code");
        Assert.DoesNotContain(agentic, m => m.Id == "llama-3.2-3b");
        Assert.DoesNotContain(agentic, m => m.Id == "grok-41-fast");
    }

    [Fact]
    public void All_tab_filters_search_vision_and_code()
    {
        var list = JsonSerializer.Deserialize(ModelsJson, VeniceJsonContext.Default.VeniceModelsListResponse)!;
        var agentic = VeniceModelCatalog.FilterAgentic(list.Data);

        var vision = VeniceModelCatalog.FilterAllTab(agentic, "", vision: true, code: false).Select(m => m.Id).ToArray();
        Assert.Equal(["claude-sonnet-5", "qwen-vision"], vision);

        var code = VeniceModelCatalog.FilterAllTab(agentic, "", vision: false, code: true).Select(m => m.Id).ToArray();
        Assert.Equal(["claude-sonnet-5", "kimi-k2-7-code"], code);

        var both = VeniceModelCatalog.FilterAllTab(agentic, "", vision: true, code: true).Select(m => m.Id).ToArray();
        Assert.Equal(["claude-sonnet-5"], both);

        var search = VeniceModelCatalog.FilterAllTab(agentic, "kimi", vision: false, code: false).Select(m => m.Id).ToArray();
        Assert.Equal(["kimi-k2-7-code"], search);

        var byName = VeniceModelCatalog.FilterAllTab(agentic, "Sonnet", vision: false, code: false).Select(m => m.Id).ToArray();
        Assert.Equal(["claude-sonnet-5"], byName);
    }

    [Fact]
    public void Context_tooltip_and_auto_helpers()
    {
        Assert.Equal("1M", VeniceModelCatalog.FormatContext(1_000_000));
        Assert.Equal("256k", VeniceModelCatalog.FormatContext(256_000));
        Assert.Equal("Авто", VeniceModelCatalog.GetDisplayName("auto"));
        Assert.Equal("GPT-5.6 Luna", VeniceModelCatalog.GetDisplayName("openai-gpt-56-luna"));
        Assert.Equal("Auto", VeniceModelCatalog.GetLogoResourceKey("auto"));
        Assert.Equal("Auto", VeniceModelCatalog.GetLogoResourceKey("AUTO"));
        Assert.Equal(ModelBrand.AutoLogoMargin, ModelBrand.LogoMargin("Auto"));
        Assert.Equal(new Thickness(0), ModelBrand.LogoMargin("Claude"));
        Assert.Equal(new Thickness(0), ModelBrand.LogoMargin("Grok"));
        Assert.True(VeniceModelCatalog.IsAuto("Auto"));
        Assert.False(VeniceModelCatalog.IsAuto("claude-sonnet-5"));
        Assert.False(VeniceModelCatalog.IsAuto(""));

        var list = JsonSerializer.Deserialize(ModelsJson, VeniceJsonContext.Default.VeniceModelsListResponse)!;
        var claude = list.Data.First(m => m.Id == "claude-sonnet-5");
        Assert.Equal("Claude Sonnet 5", VeniceModelCatalog.GetListDisplayName(claude));
        Assert.Contains("1M", VeniceModelCatalog.BuildTooltip(claude), StringComparison.Ordinal);
        Assert.Contains("Vision", VeniceModelCatalog.BuildTooltip(claude), StringComparison.Ordinal);
        Assert.Contains("Code", VeniceModelCatalog.BuildTooltip(claude), StringComparison.Ordinal);
        Assert.True(claude.ModelSpec!.Capabilities!.SupportsReasoningEffort);
        Assert.Equal(["low", "medium", "high"], claude.ModelSpec.Capabilities.ReasoningEffortOptions);
        Assert.Equal("medium", claude.ModelSpec.Capabilities.DefaultReasoningEffort);
    }

    [Theory]
    [InlineData("heavy", "heavy")]
    [InlineData("HEAVY.", "heavy")]
    [InlineData("lite", "lite")]
    [InlineData("unknown", "lite")]
    [InlineData("", "lite")]
    [InlineData(null, "lite")]
    public void Router_parses_one_word_complexity(string? text, string expected)
    {
        Assert.Equal(expected, ChatEngine.ParseRouterComplexity(text));
    }

    [Fact]
    public async Task List_text_models_calls_models_type_text()
    {
        var handler = new ScriptedHandler((_, _) => Json(ModelsJson));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var venice = new VeniceClient(http, Options());
        var models = await venice.ListTextModelsAsync();
        Assert.Equal(5, models.Count);
        Assert.Contains(handler.Paths, p => p.Contains("models?type=text", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Auto_keeps_selected_id_routes_to_heavy_and_sums_router_cost()
    {
        var handler = new ScriptedHandler((request, body) =>
        {
            if (body.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                return Sse("Готово", 0.02m);
            }

            return Json("""
                {"choices":[{"message":{"role":"assistant","content":"heavy"},"finish_reason":"stop"}],"cost":{"usd":0.001,"diem":0}}
                """);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = Options();
        var venice = new VeniceClient(http, options);
        var settings = new AppSettings
        {
            ChatModelId = "auto",
            LiteModelId = "qwen-3-7-plus",
            HeavyModelId = "claude-sonnet-5",
            RouterModelId = "qwen-3-7-plus"
        };
        var engine = new ChatEngine(venice, options, () => settings, new ToolRegistry([]));
        var session = new ChatSession
        {
            Id = "s1",
            SelectedModelId = "auto"
        };
        var observer = new RecordingObserver();

        await engine.RunTurnAsync(session, "почини службу печати", observer, CancellationToken.None);

        Assert.Equal("auto", session.SelectedModelId);
        Assert.NotNull(observer.Assistant);
        Assert.Equal("auto", observer.Assistant.RequestedModelId);
        Assert.Equal("claude-sonnet-5", observer.Assistant.ResolvedModelId);
        Assert.Equal("Готово", observer.Assistant.Text);
        Assert.True(observer.Assistant.Cost?.HasData);
        Assert.Equal(0.021m, observer.Assistant.Cost?.Usd);

        // Итог не изменился — он и есть доказательство, что маршрутизатор не посчитан дважды,
        // — но теперь его цена стоит своей строкой, а не прячется внутри «Модели».
        Assert.Equal(0.001m, observer.Assistant.RouterCost?.Usd);
        Assert.Equal(0.02m, observer.Assistant.ModelCost?.Usd);
        Assert.Contains("auto", observer.Resolved);
        Assert.Contains("claude-sonnet-5", observer.Resolved);
        Assert.True(handler.Bodies.Exists(b => !b.Contains("\"stream\":true", StringComparison.Ordinal)));
        Assert.True(handler.Bodies.Exists(b => b.Contains("\"stream\":true", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Concrete_model_does_not_call_router()
    {
        var handler = new ScriptedHandler((_, body) =>
        {
            Assert.Contains("\"stream\":true", body, StringComparison.Ordinal);
            return Sse("ok", 0.01m);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = Options();
        var venice = new VeniceClient(http, options);
        var settings = new AppSettings { ChatModelId = "grok-4-6" };
        var engine = new ChatEngine(venice, options, () => settings, new ToolRegistry([]));
        var session = new ChatSession { Id = "s2", SelectedModelId = "grok-4-6" };
        var observer = new RecordingObserver();

        await engine.RunTurnAsync(session, "привет", observer, CancellationToken.None);

        Assert.Equal("grok-4-6", session.SelectedModelId);
        Assert.Equal("grok-4-6", observer.Assistant?.RequestedModelId);
        Assert.Single(handler.Bodies);
    }

    private static AgentOptions Options() => new()
    {
        ApiKey = "test",
        BaseUrl = "https://api.venice.ai/api/v1",
        Model = "grok-4-6",
        MaxToolRounds = 2
    };

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage Sse(string text, decimal usd)
    {
        var encoded = JsonSerializer.Serialize(text);
        var usdText = usd.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var payload =
            "data: {\"choices\":[{\"delta\":{\"content\":" + encoded + "}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"cost\":{\"usd\":" + usdText + ",\"diem\":0}}\n" +
            "data: [DONE]\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _script;

        public ScriptedHandler(Func<HttpRequestMessage, string, HttpResponseMessage> script) => _script = script;

        public List<string> Paths { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.ToString() ?? "");
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            return _script(request, body);
        }
    }

    private sealed class RecordingObserver : IChatTurnObserver
    {
        public ChatDisplayMessage? Assistant { get; private set; }

        public List<string> Resolved { get; } = [];

        public void OnUserAppended(ChatDisplayMessage user)
        {
        }

        public void OnAssistantStarted(ChatDisplayMessage assistant)
        {
            Assistant = assistant;
            Resolved.Add(assistant.ResolvedModelId ?? "");
        }

        public void OnAssistantText(ChatDisplayMessage assistant)
        {
            Assistant = assistant;
            Resolved.Add(assistant.ResolvedModelId ?? "");
        }

        public void OnToolsChanged(ChatDisplayMessage assistant)
        {
        }

        public void OnAssistantCompleted(ChatDisplayMessage assistant) => Assistant = assistant;

        public void OnAssistantCancelled(ChatDisplayMessage assistant) => Assistant = assistant;

        public void OnError(string message)
        {
        }
    }
}
