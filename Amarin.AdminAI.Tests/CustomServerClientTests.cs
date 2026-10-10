using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Свой сервер человека: проверка адреса, протоколы, запросы, остаток и токены.
/// </summary>
/// <remarks>
/// Образец — ai.starimg.ru: <c>/models</c> без <c>/v1</c> отвечает 404, под <c>/v1</c> есть и
/// <c>/chat/completions</c>, и <c>/messages</c>, ключ принимается заголовком Bearer.
/// </remarks>
public sealed class CustomServerClientTests
{
    private const string Secret = "sk-star-0123456789abcdef";

    private sealed class Recorder(Func<HttpRequestMessage, string, HttpResponseMessage> script) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(request);
            Bodies.Add(body);
            return script(request, body);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Как отвечает ai.starimg.ru без ключа и с ключом.</summary>
    private static HttpResponseMessage StarServer(HttpRequestMessage request, string body)
    {
        var path = request.RequestUri!.AbsolutePath;
        return path switch
        {
            "/v1/models" => Json("""{"object":"list","data":[{"id":"claude-sonnet-5-5","object":"model"},{"id":"gpt-6-sol","object":"model"}]}"""),
            "/v1/chat/completions" when body == "{}" => Json("""{"error":{"message":"model is required"}}""", HttpStatusCode.BadRequest),
            "/v1/messages" when body == "{}" => Json("""{"type":"error","error":{"message":"model: Field required"}}""", HttpStatusCode.BadRequest),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404 page not found") }
        };
    }

    private static (VeniceClient Client, Recorder Recorder) Build(
        Func<HttpRequestMessage, string, HttpResponseMessage> script,
        ApiCredential? key = null)
    {
        var recorder = new Recorder(script);
        var keys = new ApiKeyProvider();
        if (key is { } credential)
        {
            keys.Use(credential, null, [new KeyHandle("k1", credential.Provider, credential.Secret, credential.Endpoint)]);
        }

        var options = new AgentOptions { Keys = keys, BaseUrl = "https://api.venice.ai/api/v1", Model = "grok-4-6" };
        return (new VeniceClient(new HttpClient(recorder), options), recorder);
    }

    [Fact]
    public async Task The_probe_finds_the_v1_root_and_both_protocols()
    {
        var (client, recorder) = Build(StarServer);

        var probe = await client.ProbeServerAsync("https://ai.starimg.ru", Secret, CancellationToken.None);

        Assert.Equal(ServerProbeFailure.None, probe.Failure);
        Assert.Equal(new ProviderEndpoint("ai.starimg.ru", "https://ai.starimg.ru/v1", true, true), probe.Endpoint);

        // Ни один запрос не запускал модель: тела проб пустые.
        Assert.All(recorder.Bodies, body => Assert.True(body is "" or "{}"));
        Assert.Equal("Bearer", recorder.Requests[0].Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task A_rejected_key_is_named_as_such()
    {
        var (client, _) = Build((_, _) => Json("""{"error":{"message":"invalid key"}}""", HttpStatusCode.Unauthorized));

        var probe = await client.ProbeServerAsync("ai.starimg.ru/v1", Secret, CancellationToken.None);

        Assert.Equal(ServerProbeFailure.KeyRejected, probe.Failure);
    }

    [Fact]
    public async Task An_address_that_is_not_an_api_is_named_as_such()
    {
        var (client, _) = Build((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));

        var probe = await client.ProbeServerAsync("https://example.org", Secret, CancellationToken.None);

        Assert.Equal(ServerProbeFailure.NotAnApi, probe.Failure);
    }

    [Fact]
    public async Task An_insecure_address_is_never_tried()
    {
        var (client, recorder) = Build(StarServer);

        var probe = await client.ProbeServerAsync("http://ai.starimg.ru", Secret, CancellationToken.None);

        Assert.Equal(ServerProbeFailure.BadAddress, probe.Failure);
        Assert.Empty(recorder.Requests);
    }

    /// <summary>
    /// Claude у сервера с обоими протоколами идёт родным: там подписанное размышление. Ключ —
    /// и Bearer, и x-api-key: каким его берёт посредник, заранее не узнать.
    /// </summary>
    [Fact]
    public async Task Claude_on_a_dual_server_goes_through_messages_with_both_headers()
    {
        var server = LlmProvider.Custom("a1b2c3");
        var endpoint = new ProviderEndpoint("ai.starimg.ru", "https://ai.starimg.ru/v1", true, true);
        var (client, recorder) = Build(
            (_, _) => Json("""{"type":"message","stop_reason":"end_turn","content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":3,"output_tokens":1}}"""),
            new ApiCredential(server, Secret, endpoint));

        await client.CreateChatCompletionAsync(
            ModelRef.Qualify(server, "claude-sonnet-5-5"),
            [new ChatMessage { Role = "user", Content = ChatContent.Text("hi") }],
            null, null, new VeniceParameters());

        var request = recorder.Requests[0];
        Assert.Equal("https://ai.starimg.ru/v1/messages", request.RequestUri!.ToString());
        Assert.Equal(Secret, request.Headers.Authorization!.Parameter);
        Assert.Equal(Secret, request.Headers.GetValues("x-api-key").Single());

        // Посреднику — ни бета-полей, ни кэша: он мог бы их не пропустить.
        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.Equal("claude-sonnet-5-5", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("cache_control", out _));
    }

    [Fact]
    public async Task Other_models_on_a_dual_server_go_through_chat_completions()
    {
        var server = LlmProvider.Custom("a1b2c3");
        var endpoint = new ProviderEndpoint("ai.starimg.ru", "https://ai.starimg.ru/v1", true, true);
        var (client, recorder) = Build(
            (_, _) => Json("""{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}"""),
            new ApiCredential(server, Secret, endpoint));

        await client.CreateChatCompletionAsync(
            ModelRef.Qualify(server, "gpt-6-sol"),
            [new ChatMessage { Role = "user", Content = ChatContent.Text("hi") }],
            null, null, new VeniceParameters());

        Assert.Equal("https://ai.starimg.ru/v1/chat/completions", recorder.Requests[0].RequestUri!.ToString());
        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.Equal("gpt-6-sol", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task A_server_model_list_is_its_own_column()
    {
        var server = LlmProvider.Custom("a1b2c3");
        var endpoint = new ProviderEndpoint("ai.starimg.ru", "https://ai.starimg.ru/v1", true, true);
        var (client, _) = Build(StarServer, new ApiCredential(server, Secret, endpoint));

        var models = await client.ListTextModelsAsync(new ApiCredential(server, Secret, endpoint));

        Assert.Equal(["custom-a1b2c3:claude-sonnet-5-5", "custom-a1b2c3:gpt-6-sol"], models.Select(model => model.Id));
        Assert.All(models, model => Assert.Equal(server, ModelRef.Of(model.Id)));
    }

    /// <summary>Шлюз без адреса остатка — «не сообщает», а не «ключ неверен».</summary>
    [Fact]
    public async Task A_gateway_without_billing_reports_no_balance()
    {
        var server = LlmProvider.Custom("a1b2c3");
        var endpoint = new ProviderEndpoint("ai.starimg.ru", "https://ai.starimg.ru/v1", true, false);
        var (client, _) = Build(StarServer, new ApiCredential(server, Secret, endpoint));

        await Assert.ThrowsAsync<BalanceNotReportedException>(
            () => client.GetRateLimitsAsync(new ApiCredential(server, Secret, endpoint)));
    }

    [Fact]
    public void Keys_without_a_dollar_balance_are_neither_polled_nor_unknown()
    {
        var book = new BalanceBook();
        var keys = new List<ApiKeyEntry>
        {
            new("o1", "OpenAI", "sk-proj-1", ApiKeySource.Stored, true, LlmProvider.OpenAI),
            new("s1", "Star", Secret, ApiKeySource.Stored, false, LlmProvider.Custom("a1b2c3"),
                new ProviderEndpoint("Star", "https://ai.starimg.ru/v1", true, true), TokenLimit: 10_000_000),
            new("s2", "Star2", "sk-star-2", ApiKeySource.Stored, false, LlmProvider.Custom("a1b2c3"),
                new ProviderEndpoint("Star", "https://ai.starimg.ru/v1", true, true))
        };

        Assert.Equal(["s2"], book.Stale(keys).Select(key => key.Id));
        Assert.Equal(1, book.Total(keys).Unknown);

        book.MarkSilent(keys[2].Credential);

        Assert.Empty(book.Stale(keys));
        Assert.Equal(0, book.Total(keys).Unknown);
    }

    [Fact]
    public void Tokens_are_counted_per_key_even_without_money()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-tokens-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ledger = new SpendLedger(root);
            ledger.Record(Secret, new VeniceCost { InputTokens = 1_000, OutputTokens = 200 }, "custom-a1b2c3:claude-sonnet-5-5");
            ledger.Record(Secret, new VeniceCost { Usd = 0.01m, HasData = true, InputTokens = 50, OutputTokens = 5 }, "custom-a1b2c3:gpt-6-sol");

            Assert.Equal(1_255, ledger.TokensUsed(Secret));
            Assert.Equal(0, ledger.TokensUsed("another-key"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
