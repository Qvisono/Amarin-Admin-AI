using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

public sealed class PolzaProviderTests
{
    private const string Answer = """
        {"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":1200,"completion_tokens":30,"total_tokens":1230}}
        """;

    private const string Catalog = """
        {"data":[
          {"id":"anthropic/claude-sonnet-5.5","name":"Claude Sonnet 5.5","type":"chat",
           "short_description":"A chat model","created":1790000000,
           "architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},
           "top_provider":{"context_length":1000000,"max_completion_tokens":128000,
             "supported_parameters":["tools","reasoning","reasoning_effort"],
             "capabilities":{"tools":{"status":"supported"},"input_image":{"status":"supported"},
               "reasoning_toggle":{"status":"supported"},
               "reasoning_effort":{"status":"supported","values":["max","LOW","high"]}},
             "pricing":{"prompt_per_million":"300","completion_per_million":1500,
               "input_cache_read_per_million":"30","input_cache_write_per_million":"375","currency":"RUB"}}},
          {"id":"vendor/no-tools","type":"chat","top_provider":{"supported_parameters":["tools"],
             "capabilities":{"tools":{"status":"rejected"}}}},
          {"id":"vendor/no-effort","type":"chat","top_provider":{"supported_parameters":["reasoning","reasoning_effort"],
             "capabilities":{"reasoning_effort":{"status":"ignored"},"reasoning_toggle":{"status":"rejected"}}}},
          {"id":"vendor/image","type":"image"},
          {"id":"vendor/audio","type":"chat","architecture":{"output_modalities":["audio"]}}
        ]}
        """;

    private sealed class Recorder(Func<string, HttpResponseMessage>? script = null) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<Uri?> Addresses { get; } = [];
        public List<string?> Authorization { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            Addresses.Add(request.RequestUri);
            Authorization.Add(request.Headers.Authorization?.ToString());
            return script?.Invoke(body) ?? Json(Answer);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static VeniceClient Client(Recorder recorder, VeniceModelInfo? info = null) =>
        new(new HttpClient(recorder), new AgentOptions
        {
            Keys = new ApiKeyProvider("polza-test-key", LlmProvider.Polza),
            RublesPerUsd = () => 100m
        }) { ResolveModelInfo = _ => info };

    private static ChatCompletionRequest Request(string model, ReasoningChoice? choice = null) => new()
    {
        Model = "polza:" + model,
        Messages =
        [
            new() { Role = "system", Content = ChatContent.Text("Stable instructions") },
            new() { Role = "user", Content = ChatContent.Text("Question") },
            new() { Role = "assistant", Content = ChatContent.Text("Answer") },
            new() { Role = "user", Content = ChatContent.Text("Next question") }
        ],
        CacheKey = "chat-polza",
        ReasoningChoice = choice
    };

    [Fact]
    public void Registration_keeps_model_identity_and_money_currency_separate()
    {
        var spec = ProviderSpec.For(LlmProvider.Polza);
        Assert.Contains(spec, ProviderSpec.All);
        Assert.Equal("Polza.ai", spec.Name);
        Assert.Equal("polza", LlmProvider.Parse("POLZA").Id);
        Assert.Equal(LlmProvider.Polza, ModelRef.Of("polza:anthropic/claude-sonnet-5.5:variant"));
        Assert.Equal("anthropic/claude-sonnet-5.5:variant", ModelRef.Bare("polza:anthropic/claude-sonnet-5.5:variant"));
        Assert.Equal("polza:openai/gpt-5", ModelRef.Qualify(LlmProvider.Polza, "openai/gpt-5"));
        Assert.Equal(ProviderCurrency.Rub, spec.CostCurrency);
        Assert.True(spec.ReportsCost);
        Assert.False(spec.IsLegacy);
        Assert.Empty(spec.EnvironmentVariable);
        Assert.False(ModelPriceBook.Covers(LlmProvider.Polza));
    }

    [Fact]
    public void A_polza_key_round_trips_in_the_provider_file_without_entering_the_legacy_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-polza-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ApiKeyStore(root);
            store.Load();
            store.Add("Venice", "vk-test-legacy");
            store.Add("Polza", "polza-secret-for-test", LlmProvider.Polza);

            var legacy = File.ReadAllText(Path.Combine(root, "keys.json"));
            var providers = File.ReadAllText(Path.Combine(root, ApiKeyStore.ProviderKeysFileName));
            Assert.DoesNotContain("polza", legacy, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("polza", providers, StringComparison.Ordinal);
            Assert.DoesNotContain("polza-secret-for-test", providers, StringComparison.Ordinal);

            var restored = new ApiKeyStore(root);
            restored.Load();
            Assert.Contains(restored.List(), key => key.Provider == LlmProvider.Polza && key.Label == "Polza");
            Assert.True(restored.Contains("polza-secret-for-test"));
            Assert.True(restored.Contains("vk-test-legacy"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_catalog_reads_nested_verified_capabilities_and_converts_all_prices()
    {
        var models = PolzaModels.Parse(Catalog, 100m);
        Assert.Equal(3, models.Count);
        var model = models[0];
        Assert.Equal("polza:anthropic/claude-sonnet-5.5", model.Id);
        Assert.Equal(1000000, model.ContextLength);
        Assert.Equal(128000, model.ModelSpec!.MaxOutputTokens);
        Assert.Equal("A chat model", model.ModelSpec.Description);
        Assert.Equal("anthropic", model.OwnedBy);
        Assert.True(model.ModelSpec.Capabilities!.SupportsVision);
        Assert.True(model.ModelSpec.Capabilities.SupportsFunctionCalling);
        Assert.Equal(["low", "high", "max"], model.ModelSpec.Capabilities.ReasoningEffortOptions!);
        Assert.Equal(3m, model.ModelSpec.Pricing!.Input!.Usd);
        Assert.Equal(15m, model.ModelSpec.Pricing.Output!.Usd);
        Assert.Equal(0.3m, model.ModelSpec.Pricing.CacheInput!.Usd);
        Assert.Equal(3.75m, model.ModelSpec.Pricing.CacheWrite!.Usd);
        Assert.False(models[1].ModelSpec!.Capabilities!.SupportsFunctionCalling);
        Assert.False(models[2].ModelSpec!.Capabilities!.SupportsReasoningEffort);
        Assert.True(models[2].ModelSpec!.Capabilities!.ReasoningMandatory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Without_a_valid_exchange_rate_the_catalog_keeps_models_without_dollar_prices(int? rate)
    {
        var models = PolzaModels.Parse(Catalog, rate);
        Assert.Equal(3, models.Count);
        Assert.Null(models[0].ModelSpec!.Pricing);
        Assert.Equal(1000000, models[0].ContextLength);
    }

    [Fact]
    public void The_legacy_flat_catalog_converts_rubles_per_token_to_dollars_per_million()
    {
        var model = Assert.Single(PolzaModels.Parse("""
            {"data":[{"id":"openai/gpt-5","context_length":128000,"max_completion_tokens":8192,
              "supported_parameters":["tools","reasoning"],
              "pricing":{"prompt":"0.0003","completion":"0.0015","input_cache_read":"0.00003","input_cache_write":"0.000375"}}]}
            """, 100m));
        Assert.Equal("polza:openai/gpt-5", model.Id);
        Assert.Equal(128000, model.ContextLength);
        Assert.Equal(3m, model.ModelSpec!.Pricing!.Input!.Usd);
        Assert.Equal(15m, model.ModelSpec.Pricing.Output!.Usd);
        Assert.Equal(0.3m, model.ModelSpec.Pricing.CacheInput!.Usd);
        Assert.Equal(3.75m, model.ModelSpec.Pricing.CacheWrite!.Usd);
    }

    [Fact]
    public async Task Mandatory_reasoning_metadata_keeps_the_choice_on_a_supported_lowest_level()
    {
        var info = Assert.Single(PolzaModels.Parse("""
            {"data":[{"id":"deepseek/deepseek-v4-pro","type":"chat",
              "top_provider":{"supported_parameters":["tools","reasoning"],
                "reasoning":{"supported_efforts":["max","low","high"],"default_effort":"high","mandatory":true}}}]}
            """, null));
        Assert.True(info.ModelSpec!.Capabilities!.SupportsReasoning);
        Assert.True(info.ModelSpec.Capabilities.SupportsReasoningEffort);
        Assert.True(info.ModelSpec.Capabilities.ReasoningMandatory);
        Assert.Equal("high", info.ModelSpec.Capabilities.DefaultReasoningEffort);
        Assert.Equal(["low", "high", "max"], info.ModelSpec.Capabilities.ReasoningEffortOptions!);

        var recorder = new Recorder();
        await Client(recorder, info).CreateChatCompletionAsync(Request("deepseek/deepseek-v4-pro", new ReasoningChoice(true, null)));
        using var document = JsonDocument.Parse(Assert.Single(recorder.Bodies));
        var reasoning = document.RootElement.GetProperty("reasoning");
        Assert.True(reasoning.GetProperty("enabled").GetBoolean());
        Assert.Equal("low", reasoning.GetProperty("effort").GetString());
    }

    [Fact]
    public async Task Loading_the_catalog_asks_for_chat_models_and_verified_capabilities_at_polza()
    {
        var recorder = new Recorder(_ => Json(Catalog));
        var models = await Client(recorder).ListTextModelsAsync();
        Assert.Equal(3, models.Count);
        Assert.Equal("https://polza.ai/api/v1/models?type=chat&include_providers=true&include_capabilities=true",
            Assert.Single(recorder.Addresses)!.AbsoluteUri);
        Assert.Equal("Bearer polza-test-key", Assert.Single(recorder.Authorization));
    }

    [Theory]
    [InlineData("openai/gpt-5")]
    [InlineData("google/gemini-3-pro")]
    [InlineData("deepseek/deepseek-chat")]
    [InlineData("x-ai/grok-4")]
    [InlineData("qwen/qwen3")]
    public async Task Automatic_caching_keeps_the_body_plain_for_other_vendors(string model)
    {
        var recorder = new Recorder();
        await Client(recorder).CreateChatCompletionAsync(Request(model));
        var body = Assert.Single(recorder.Bodies);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(model, document.RootElement.GetProperty("model").GetString());
        Assert.DoesNotContain("prompt_cache_key", body, StringComparison.Ordinal);
        Assert.DoesNotContain("cache_control", body, StringComparison.Ordinal);
        Assert.DoesNotContain("venice_parameters", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"usage\"", body, StringComparison.Ordinal);
        Assert.Equal("https://polza.ai/api/v1/chat/completions", Assert.Single(recorder.Addresses)!.AbsoluteUri);
    }

    [Fact]
    public async Task Claude_caching_marks_the_stable_prefix_without_a_cache_key_or_extended_ttl()
    {
        var recorder = new Recorder();
        var request = Request("anthropic/claude-sonnet-5.5");
        await Client(recorder).CreateChatCompletionAsync(request);
        var body = Assert.Single(recorder.Bodies);
        Assert.Contains("cache_control", body, StringComparison.Ordinal);
        Assert.Contains("ephemeral", body, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_cache_key", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ttl\"", body, StringComparison.Ordinal);
        Assert.All(request.Messages, message => Assert.Equal(JsonValueKind.String, message.Content!.Value.ValueKind));
    }

    [Theory]
    [InlineData("anthropic/claude-opus-4.7")]
    [InlineData("anthropic/claude-opus-4-7")]
    [InlineData("anthropic/claude-4-7-opus")]
    [InlineData("anthropic/claude-sonnet-5.5")]
    [InlineData("anthropic/claude-sonnet-5-5")]
    public async Task New_claude_versions_use_adaptive_reasoning_with_effort_level(string model)
    {
        var recorder = new Recorder();
        await Client(recorder).CreateChatCompletionAsync(Request(model, new ReasoningChoice(false, "max")));
        using var document = JsonDocument.Parse(Assert.Single(recorder.Bodies));
        var reasoning = document.RootElement.GetProperty("reasoning");
        Assert.Equal("adaptive", reasoning.GetProperty("type").GetString());
        Assert.Equal("max", reasoning.GetProperty("effort_level").GetString());
        Assert.False(reasoning.TryGetProperty("effort", out _));
        Assert.False(document.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Theory]
    [InlineData("anthropic/claude-opus-4.6")]
    [InlineData("openai/gpt-5")]
    public async Task Other_models_use_nested_enabled_and_effort(string model)
    {
        var recorder = new Recorder();
        await Client(recorder).CreateChatCompletionAsync(Request(model, new ReasoningChoice(false, "high")));
        using var document = JsonDocument.Parse(Assert.Single(recorder.Bodies));
        var reasoning = document.RootElement.GetProperty("reasoning");
        Assert.True(reasoning.GetProperty("enabled").GetBoolean());
        Assert.Equal("high", reasoning.GetProperty("effort").GetString());
        Assert.False(reasoning.TryGetProperty("type", out _));
        Assert.False(document.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task Thinking_metadata_takes_precedence_over_the_version_fallback()
    {
        var info = new VeniceModelInfo
        {
            ModelSpec = new VeniceModelSpec { Capabilities = new VeniceModelCapabilities { ThinkingModes = ["enabled", "disabled"] } }
        };
        var recorder = new Recorder();
        await Client(recorder, info).CreateChatCompletionAsync(Request("anthropic/claude-sonnet-5.5", new ReasoningChoice(false, "high")));
        using var document = JsonDocument.Parse(Assert.Single(recorder.Bodies));
        Assert.True(document.RootElement.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
        Assert.False(document.RootElement.GetProperty("reasoning").TryGetProperty("type", out _));
    }

    [Theory]
    [InlineData("anthropic/claude-opus-4.7", "type", "disabled")]
    [InlineData("openai/gpt-5", "enabled", "False")]
    public async Task Turning_thinking_off_uses_the_matching_wire_format(string model, string field, string expected)
    {
        var recorder = new Recorder();
        await Client(recorder).CreateChatCompletionAsync(Request(model, new ReasoningChoice(true, null)));
        using var document = JsonDocument.Parse(Assert.Single(recorder.Bodies));
        Assert.Equal(expected, document.RootElement.GetProperty("reasoning").GetProperty(field).ToString());
    }

    [Fact]
    public async Task A_cache_refusal_removes_only_cache_fields_and_is_remembered_for_the_model()
    {
        var recorder = new Recorder(body => body.Contains("cache_control", StringComparison.Ordinal)
            ? Json("""{"error":{"message":"cache_control is unsupported"}}""", HttpStatusCode.BadRequest)
            : Json(Answer));
        var client = Client(recorder);
        var request = Request("anthropic/claude-sonnet-5.5", new ReasoningChoice(false, "high"));
        await client.CreateChatCompletionAsync(request);
        await client.CreateChatCompletionAsync(request);
        Assert.Equal(3, recorder.Bodies.Count);
        Assert.All(recorder.Bodies.Skip(1), body =>
        {
            Assert.DoesNotContain("cache_control", body, StringComparison.Ordinal);
            Assert.Contains("\"adaptive\"", body, StringComparison.Ordinal);
            Assert.Contains("\"effort_level\":\"high\"", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_reasoning_refusal_retries_without_reasoning_and_keeps_cache_markers()
    {
        var recorder = new Recorder(body => body.Contains("\"reasoning\"", StringComparison.Ordinal)
            ? Json("""{"error":{"message":"reasoning is unsupported"}}""", HttpStatusCode.BadRequest)
            : Json(Answer));
        await Client(recorder).CreateChatCompletionAsync(Request("anthropic/claude-sonnet-5.5", new ReasoningChoice(false, "high")));
        Assert.Equal(2, recorder.Bodies.Count);
        Assert.DoesNotContain("\"reasoning\"", recorder.Bodies[1], StringComparison.Ordinal);
        Assert.Contains("cache_control", recorder.Bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streaming_cache_retry_preserves_tools_reasoning_usage_and_ruble_accounting()
    {
        const string stream = """
            data: {"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}
            data: {"choices":[],"usage":{"prompt_tokens":1200,"completion_tokens":30,"total_tokens":1230,"prompt_tokens_details":{"cached_tokens":1000,"cache_write_tokens":100},"cost_rub":200}}
            data: [DONE]

            """;
        var recorder = new Recorder(body => body.Contains("cache_control", StringComparison.Ordinal)
            ? Json("""{"error":{"message":"cache_control is unsupported"}}""", HttpStatusCode.BadRequest)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
            });
        var client = Client(recorder);
        var request = Request("anthropic/claude-sonnet-5.5");
        List<ToolDefinition> tools = [new()
        {
            Function = new FunctionDefinition
            {
                Name = "read_test",
                Description = "Read a test value",
                Parameters = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })
            }
        }];

        var answer = await client.StreamChatCompletionAsync(request.Model, request.Model, request.Messages,
            tools, "auto", new VeniceParameters(), null, reasoning: new ReasoningChoice(false, "high"),
            fallback: false, cacheKey: request.CacheKey);

        Assert.Equal("ok", answer.Text);
        Assert.Equal(1200, answer.PromptTokens);
        Assert.Equal(2m, answer.Cost.Usd);
        Assert.True(answer.Cost.Estimated);
        Assert.Equal(2m, client.RequestCost.Usd);
        Assert.Equal(2, recorder.Bodies.Count);
        Assert.DoesNotContain("cache_control", recorder.Bodies[1], StringComparison.Ordinal);
        Assert.All(recorder.Bodies, body =>
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            Assert.Equal("adaptive", root.GetProperty("reasoning").GetProperty("type").GetString());
            Assert.Equal("read_test", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        });
    }
}
