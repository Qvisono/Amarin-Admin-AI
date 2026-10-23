using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Приватность OpenRouter и скорость модели (1.37.0): что уходит на провод.
/// </summary>
/// <remarks>
/// Оба отличия невидимы глазом — они в теле запроса. Ошибка здесь выглядела бы либо как запрос,
/// ушедший к провайдеру, который хранит данные, либо как «быстрая» модель, которая просто
/// работает с обычной скоростью.
/// </remarks>
public sealed class ProviderPrivacyAndSpeedTests
{
    // ───────────────────────── приватность OpenRouter ─────────────────────────

    [Fact]
    public async Task With_no_retention_every_OpenRouter_request_asks_for_providers_that_keep_no_data()
    {
        var (client, recorder) = Build(LlmProvider.OpenRouter, noRetention: true);

        await client.CreateChatCompletionAsync("openrouter:openai/gpt-5.6-sol", [User("hi")], null, null, new VeniceParameters());

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        var provider = body.RootElement.GetProperty("provider");
        Assert.Equal("deny", provider.GetProperty("data_collection").GetString());
        Assert.True(provider.GetProperty("zdr").GetBoolean());
    }

    [Fact]
    public async Task Without_no_retention_the_body_has_no_provider_field()
    {
        var (client, recorder) = Build(LlmProvider.OpenRouter, noRetention: false);

        await client.CreateChatCompletionAsync("openrouter:openai/gpt-5.6-sol", [User("hi")], null, null, new VeniceParameters());

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.False(body.RootElement.TryGetProperty("provider", out _));
    }

    /// <summary>Очередь OpenRouter хранит заявки до тридцати дней — с фильтром её нет вовсе.</summary>
    [Fact]
    public async Task With_no_retention_a_batch_model_skips_the_queue()
    {
        var (client, recorder) = Build(LlmProvider.OpenRouter, noRetention: true);

        await client.CreateChatCompletionAsync(
            "openrouter:anthropic/claude-sonnet-4.5:batch", [User("hi")], null, null, new VeniceParameters());

        Assert.DoesNotContain(recorder.Urls, url => url.Contains("/batches", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.Equal("anthropic/claude-sonnet-4.5", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("provider").GetProperty("zdr").GetBoolean());
    }

    [Fact]
    public async Task With_no_retention_a_decisions_request_carries_the_filter_too()
    {
        var (client, recorder) = Build(LlmProvider.OpenRouter, noRetention: true, status: HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<VeniceApiException>(() =>
            client.CreateDecisionsAsync("openrouter:microsoft/decision-1", "state", new JsonObject(), CancellationToken.None));

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.Equal("deny", body.RootElement.GetProperty("provider").GetProperty("data_collection").GetString());
    }

    [Fact]
    public async Task OpenRouter_refusing_for_the_data_policy_is_explained_in_plain_words()
    {
        var (client, _) = Build(
            LlmProvider.OpenRouter,
            noRetention: true,
            status: HttpStatusCode.NotFound,
            reply: """{"error":{"message":"No endpoints found matching your data policy (Zero data retention)."}}""");

        var error = await Assert.ThrowsAsync<VeniceApiException>(() =>
            client.CreateChatCompletionAsync("openrouter:openai/gpt-5.6-sol", [User("hi")], null, null, new VeniceParameters()));

        Assert.Equal(Loc.Get("S.Provider.NoPrivateEndpoint"), error.Message);
        Assert.Equal(404, error.StatusCode);
    }

    // ───────────────────────── скорость ─────────────────────────

    [Theory]
    [InlineData("openai:gpt-5.6-sol", "flex,fast")]
    [InlineData("openai:gpt-6-astra", "flex,fast,ultrafast")]
    [InlineData("openai:gpt-6.1-sol", "flex,fast,ultrafast")]
    [InlineData("openai:o4-mini", "flex,fast")]
    [InlineData("openai:gpt-4.1", "")]
    [InlineData("anthropic:claude-opus-5-5", "fast")]
    [InlineData("anthropic:claude-opus-4-8", "fast")]
    [InlineData("anthropic:claude-sonnet-5-5", "")]
    [InlineData("openrouter:openai/gpt-6-astra", "flex,fast,ultrafast")]
    [InlineData("openrouter:anthropic/claude-opus-4.8", "fast")]
    [InlineData("openrouter:google/gemini-3-pro", "flex,fast")]
    [InlineData("openrouter:x-ai/grok-4.7", "fast")]
    [InlineData("openrouter:deepseek/deepseek-v4", "")]
    [InlineData("grok-4-6", "")]
    [InlineData("custom-a1b2c3:gpt-5.6-sol", "")]
    public void Only_models_that_have_a_speed_offer_it(string model, string expected) =>
        Assert.Equal(expected, string.Join(",", ModelSpeeds.For(model)));

    [Theory]
    [InlineData("fast", "priority")]
    [InlineData("flex", "flex")]
    [InlineData("ultrafast", "ultrafast")]
    public async Task OpenAI_gets_the_speed_as_service_tier(string speed, string wire)
    {
        var (client, recorder) = Build(LlmProvider.OpenAI);

        await client.CreateChatCompletionAsync(
            "openai:gpt-6-astra", [User("hi")], null, null, new VeniceParameters(),
            reasoning: new ReasoningChoice(true, null, speed));

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.Equal(wire, body.RootElement.GetProperty("service_tier").GetString());
    }

    [Fact]
    public async Task A_speed_the_model_does_not_have_never_goes_on_the_wire()
    {
        var (client, recorder) = Build(LlmProvider.OpenAI);

        await client.CreateChatCompletionAsync(
            "openai:gpt-5.6-sol", [User("hi")], null, null, new VeniceParameters(),
            reasoning: new ReasoningChoice(true, null, ModelSpeeds.Ultrafast));

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.False(body.RootElement.TryGetProperty("service_tier", out _));
    }

    [Fact]
    public async Task OpenRouter_gets_the_speed_as_service_tier()
    {
        var (client, recorder) = Build(LlmProvider.OpenRouter);

        await client.CreateChatCompletionAsync(
            "openrouter:google/gemini-3-pro", [User("hi")], null, null, new VeniceParameters(),
            reasoning: new ReasoningChoice(true, null, ModelSpeeds.Flex));

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.Equal("flex", body.RootElement.GetProperty("service_tier").GetString());
    }

    /// <summary>Таблица ошиблась — сервер отверг поле: ответ без уровня лучше, чем никакого.</summary>
    [Fact]
    public async Task A_service_tier_the_server_rejects_is_dropped_on_one_retry()
    {
        var calls = 0;
        var (client, recorder) = Build(LlmProvider.OpenAI, script: _ => ++calls == 1
            ? (HttpStatusCode.BadRequest, """{"error":{"message":"Unsupported value: 'service_tier' does not support 'flex' with this model."}}""")
            : (HttpStatusCode.OK, Completion));

        await client.CreateChatCompletionAsync(
            "openai:gpt-5.6-sol", [User("hi")], null, null, new VeniceParameters(),
            reasoning: new ReasoningChoice(true, null, ModelSpeeds.Flex));

        Assert.Equal(2, recorder.Bodies.Count);
        using var retry = JsonDocument.Parse(recorder.Bodies[1]);
        Assert.False(retry.RootElement.TryGetProperty("service_tier", out _));
    }

    /// <summary>
    /// У OpenRouter уровень есть не у каждого провайдера модели: без точки с ним — 404. Повтор без
    /// уровня, а фильтр «без хранения данных» остаётся на месте.
    /// </summary>
    [Fact]
    public async Task OpenRouter_without_an_endpoint_for_the_speed_answers_on_one_retry_without_it()
    {
        var calls = 0;
        var (client, recorder) = Build(LlmProvider.OpenRouter, noRetention: true, script: _ => ++calls == 1
            ? (HttpStatusCode.NotFound, """{"error":{"message":"No endpoints found that support the requested service tier."}}""")
            : (HttpStatusCode.OK, Completion));

        await client.CreateChatCompletionAsync(
            "openrouter:google/gemini-3-pro", [User("hi")], null, null, new VeniceParameters(),
            reasoning: new ReasoningChoice(true, null, ModelSpeeds.Flex));

        Assert.Equal(2, recorder.Bodies.Count);
        using var retry = JsonDocument.Parse(recorder.Bodies[1]);
        Assert.False(retry.RootElement.TryGetProperty("service_tier", out _));
        Assert.True(retry.RootElement.GetProperty("provider").GetProperty("zdr").GetBoolean());
    }

    [Fact]
    public async Task OpenRouter_refusing_another_field_is_not_retried_for_the_speed()
    {
        var (client, recorder) = Build(
            LlmProvider.OpenRouter,
            status: HttpStatusCode.BadRequest,
            reply: """{"error":{"message":"Invalid value for 'tools'."}}""");

        await Assert.ThrowsAsync<VeniceApiException>(() => client.CreateChatCompletionAsync(
            "openrouter:google/gemini-3-pro", [User("hi")], null, null, new VeniceParameters(),
            reasoning: new ReasoningChoice(true, null, ModelSpeeds.Flex)));

        Assert.Single(recorder.Bodies);
    }

    [Fact]
    public void Claude_fast_mode_goes_to_the_official_API_with_its_beta()
    {
        var request = new ChatCompletionRequest
        {
            Model = "anthropic:claude-opus-5-5",
            Messages = [User("hi")],
            ReasoningChoice = new ReasoningChoice(true, null, ModelSpeeds.Fast)
        };

        var official = AnthropicMessages.Build(request, "claude-opus-5-5", null, stream: false, official: true);
        Assert.Equal("fast", official.Json["speed"]?.GetValue<string>());
        Assert.Contains(ModelSpeeds.AnthropicFastBeta, official.Betas, StringComparison.Ordinal);

        // Посредник человека этого поля может не знать, а повтор после отказа идёт без него.
        var proxy = AnthropicMessages.Build(request, "claude-opus-5-5", null, stream: false, official: false);
        Assert.Null(proxy.Json["speed"]);
        Assert.Equal("", proxy.Betas);
        var relaxed = AnthropicMessages.Build(request, "claude-opus-5-5", null, stream: false, official: true, relaxed: true);
        Assert.Null(relaxed.Json["speed"]);
        Assert.True(AnthropicMessages.MayRelax(400, "speed: fast mode is not available for this model"));
    }

    [Fact]
    public void Claude_without_fast_mode_gets_no_speed()
    {
        var request = new ChatCompletionRequest
        {
            Model = "anthropic:claude-sonnet-5-5",
            Messages = [User("hi")],
            ReasoningChoice = new ReasoningChoice(true, null, ModelSpeeds.Fast)
        };

        var body = AnthropicMessages.Build(request, "claude-sonnet-5-5", null, stream: false, official: true);

        Assert.Null(body.Json["speed"]);
        Assert.DoesNotContain(ModelSpeeds.AnthropicFastBeta, body.Betas, StringComparison.Ordinal);
    }

    [Fact]
    public void Claude_reports_the_served_speed_from_usage()
    {
        using var fast = JsonDocument.Parse("""{"usage":{"input_tokens":1,"output_tokens":1,"speed":"fast"}}""");
        using var standard = JsonDocument.Parse("""{"usage":{"input_tokens":1,"output_tokens":1,"speed":"standard"}}""");

        Assert.Equal(ModelSpeeds.Fast, AnthropicMessages.ServedTier(fast.RootElement));
        Assert.Null(AnthropicMessages.ServedTier(standard.RootElement));
    }

    /// <summary>Каталог знает обычную цену; оценка поправляется на уровень, каким ответ обслужили.</summary>
    [Theory]
    [InlineData("flex", 0.5)]
    [InlineData("priority", 2)]
    [InlineData("fast", 2)]
    [InlineData("ultrafast", 6)]
    [InlineData("default", 1)]
    [InlineData(null, 1)]
    public void The_estimate_follows_the_served_tier(string? tier, double factor) =>
        Assert.Equal((decimal)factor, ModelSpeeds.PriceFactor(tier));

    [Fact]
    public void A_slot_keeps_its_speed()
    {
        var slot = new ReasoningSettings();
        slot.Apply(new ReasoningChoice(false, "high", " fast "));

        Assert.Equal("fast", slot.Speed);
        Assert.Equal(new ReasoningChoice(false, "high", "fast"), slot.ToChoice());

        slot.Apply(new ReasoningChoice(false, "high"));
        Assert.Null(slot.Speed);
    }

    // ───────────────────────── обвязка ─────────────────────────

    private const string Completion =
        """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

    private static (VeniceClient Client, Recorder Recorder) Build(
        LlmProvider provider,
        bool noRetention = false,
        HttpStatusCode status = HttpStatusCode.OK,
        string reply = Completion,
        Func<int, (HttpStatusCode, string)>? script = null)
    {
        var recorder = new Recorder(script ?? (_ => (status, reply)));
        var options = new AgentOptions
        {
            Keys = new ApiKeyProvider("test-key", provider),
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            MaxToolRounds = 2,
            NoRetention = () => noRetention
        };

        return (new VeniceClient(new HttpClient(recorder), options), recorder);
    }

    private static ChatMessage User(string text) => new() { Role = "user", Content = ChatContent.Text(text) };

    private sealed class Recorder(Func<int, (HttpStatusCode Status, string Body)> script) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri?.ToString() ?? "");
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var (status, body) = script(Bodies.Count);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
