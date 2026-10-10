using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Запросы к прямым провайдерам: адрес, способ предъявить ключ, тело, повтор и цена.
/// </summary>
/// <remarks>
/// До 1.34.0 любой провайдер, кроме Venice, получал тело запроса OpenRouter. Ошибка здесь не
/// видна глазом: сервер отвечает 400 на незнакомое поле, а ключ мог уйти не туда.
/// </remarks>
public sealed class DirectProviderClientTests
{
    private sealed class Recorder(Func<HttpRequestMessage, string, HttpResponseMessage> script) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(request);
            Bodies.Add(body);
            return script(request, body);
        }
    }

    private static (VeniceClient Client, Recorder Recorder, List<VeniceCost> Spent) Build(
        LlmProvider provider,
        string secret,
        Func<HttpRequestMessage, string, HttpResponseMessage> script,
        VeniceModelInfo? info = null)
    {
        var recorder = new Recorder(script);
        var spent = new List<VeniceCost>();
        var options = new AgentOptions
        {
            Keys = new ApiKeyProvider(secret, provider),
            SpendSink = (_, cost, _) => spent.Add(cost),
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6"
        };

        var client = new VeniceClient(new HttpClient(recorder), options) { ResolveModelInfo = _ => info };
        return (client, recorder, spent);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string OpenAiAnswer = """
        {"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":1000,"completion_tokens":500,"total_tokens":1500}}
        """;

    private static ChatMessage User(string text) => new() { Role = "user", Content = ChatContent.Text(text) };

    private static VeniceModelInfo Priced(string id, decimal input, decimal output, List<string>? efforts = null) => new()
    {
        Id = id,
        ModelSpec = new VeniceModelSpec
        {
            Pricing = new VeniceModelPricing
            {
                Input = new VeniceUnitPrice { Usd = input },
                Output = new VeniceUnitPrice { Usd = output }
            },
            Capabilities = new VeniceModelCapabilities
            {
                SupportsFunctionCalling = true,
                SupportsReasoningEffort = true,
                ReasoningEffortOptions = efforts
            }
        }
    };

    [Fact]
    public async Task OpenAI_gets_a_plain_body_at_its_own_address()
    {
        var (client, recorder, _) = Build(LlmProvider.OpenAI, "sk-proj-key", (_, _) => Json(OpenAiAnswer));

        await client.CreateChatCompletionAsync(
            "openai:gpt-6-sol", [User("hi")], tools: null, toolChoice: null, new VeniceParameters(),
            reasoning: new ReasoningChoice(false, "high"));

        var request = recorder.Requests[0];
        Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("sk-proj-key", request.Headers.Authorization.Parameter);

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        var root = body.RootElement;
        Assert.Equal("gpt-6-sol", root.GetProperty("model").GetString());
        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("venice_parameters", out _));
        Assert.False(root.TryGetProperty("usage", out _));
        Assert.False(root.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public async Task DeepSeek_is_told_about_thinking_with_its_switch()
    {
        var (client, recorder, _) = Build(LlmProvider.DeepSeek, "sk-ds", (_, _) => Json(OpenAiAnswer));

        await client.CreateChatCompletionAsync(
            "deepseek:deepseek-flash", [User("hi")], null, null, new VeniceParameters(),
            reasoning: ReasoningChoice.Disabled);

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("https://api.deepseek.com/chat/completions", recorder.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Qwen_is_told_about_thinking_with_its_flag()
    {
        var (client, recorder, _) = Build(LlmProvider.Qwen, "sk-qw", (_, _) => Json(OpenAiAnswer));

        await client.CreateChatCompletionAsync(
            "qwen:qwen3.8-max", [User("hi")], null, null, new VeniceParameters(),
            reasoning: new ReasoningChoice(false, null));

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.True(body.RootElement.GetProperty("enable_thinking").GetBoolean());
    }

    /// <summary>
    /// Сервер отверг необязательное поле — один повтор без них, а не отказ всего хода: поле
    /// размышления у моделей одного провайдера устроено по-разному.
    /// </summary>
    [Fact]
    public async Task A_refused_optional_field_is_dropped_once()
    {
        var (client, recorder, _) = Build(LlmProvider.Moonshot, "sk-moon", (_, body) =>
            body.Contains("reasoning_effort", StringComparison.Ordinal)
                ? Json("""{"error":{"message":"Unrecognized request argument supplied: reasoning_effort"}}""", HttpStatusCode.BadRequest)
                : Json(OpenAiAnswer));

        var answer = await client.CreateChatCompletionAsync(
            "moonshot:kimi-k2.7-code", [User("hi")], null, null, new VeniceParameters(),
            reasoning: new ReasoningChoice(false, "medium"));

        Assert.Equal("ok", ChatContent.ReadText(answer.Choices[0].Message.Content));
        Assert.Equal(2, recorder.Bodies.Count);
        Assert.DoesNotContain("reasoning_effort", recorder.Bodies[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Провайдер цены не называет — программа считает её сама по каталогу, и разбивка под
    /// сообщением читает её тем же <see cref="ChatCompletionResponse.ReportedCost"/>.
    /// </summary>
    [Fact]
    public async Task A_silent_provider_is_priced_from_the_catalogue()
    {
        var (client, _, spent) = Build(
            LlmProvider.OpenAI,
            "sk-proj-key",
            (_, _) => Json(OpenAiAnswer),
            Priced("openai:gpt-6-sol", input: 2m, output: 10m));

        var answer = await client.CreateChatCompletionAsync("openai:gpt-6-sol", [User("hi")], null, null, new VeniceParameters());

        // 1000 × $2/М + 500 × $10/М = $0.007
        Assert.Equal(0.007m, answer.ReportedCost()!.Usd);
        var cost = Assert.Single(spent);
        Assert.True(cost.Estimated);
        Assert.Equal(1000, cost.InputTokens);
        Assert.Equal(500, cost.OutputTokens);
    }

    /// <summary>Без цены в каталоге деньги неизвестны, а токены всё равно записываются.</summary>
    [Fact]
    public async Task Tokens_are_booked_even_without_a_price()
    {
        var (client, _, spent) = Build(LlmProvider.OpenAI, "sk-proj-key", (_, _) => Json(OpenAiAnswer));

        await client.CreateChatCompletionAsync("openai:gpt-6-sol", [User("hi")], null, null, new VeniceParameters());

        var cost = Assert.Single(spent);
        Assert.False(cost.HasData);
        Assert.Equal(1500, cost.InputTokens + cost.OutputTokens);
    }

    [Fact]
    public async Task Anthropic_is_reached_with_its_own_header_and_protocol()
    {
        const string answer = """
            {"type":"message","role":"assistant","stop_reason":"end_turn",
             "content":[{"type":"text","text":"Hi there"}],
             "usage":{"input_tokens":10,"output_tokens":3}}
            """;
        var (client, recorder, _) = Build(LlmProvider.Anthropic, "sk-ant-key", (_, _) => Json(answer));

        var response = await client.CreateChatCompletionAsync(
            "anthropic:claude-haiku-5-5", [User("hi")], null, null, new VeniceParameters());

        var request = recorder.Requests[0];
        Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.Equal("sk-ant-key", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal(AnthropicMessages.ApiVersion, request.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("Hi there", ChatContent.ReadText(response.Choices[0].Message.Content));

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.Equal("claude-haiku-5-5", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task An_anthropic_stream_arrives_as_text_and_a_tool_call()
    {
        var sse = string.Join("\n", new[]
        {
            "event: message_start",
            """data: {"type":"message_start","message":{"usage":{"input_tokens":40}}}""",
            "",
            "event: content_block_start",
            """data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Looking"}}""",
            """data: {"type":"content_block_stop","index":0}""",
            """data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"read_file","input":{}}}""",
            """data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path\":\"b\"}"}}""",
            """data: {"type":"content_block_stop","index":1}""",
            """data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":9}}""",
            """data: {"type":"message_stop"}""",
            ""
        });

        var (client, _, spent) = Build(
            LlmProvider.Anthropic,
            "sk-ant-key",
            (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") },
            Priced("anthropic:claude-haiku-5-5", input: 0.1m, output: 0.5m));

        var streamed = await client.StreamChatCompletionAsync(
            "anthropic:claude-haiku-5-5", "anthropic:claude-haiku-5-5", [User("hi")], null, null, new VeniceParameters(), onText: null);

        Assert.Equal("Looking", streamed.Text);
        Assert.Equal("toolu_1", Assert.Single(streamed.ToolCalls).Id);
        Assert.Equal(40, streamed.PromptTokens);
        Assert.Equal(9, streamed.CompletionTokens);
        Assert.True(Assert.Single(spent).Estimated);
    }

    /// <summary>
    /// Отказ из-за полей размышления (модель не того поколения) — повтор без них, а не отказ хода.
    /// </summary>
    [Fact]
    public async Task Anthropic_retries_once_without_thinking()
    {
        const string answer = """{"type":"message","stop_reason":"end_turn","content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":1,"output_tokens":1}}""";
        var (client, recorder, _) = Build(LlmProvider.Anthropic, "sk-ant-key", (_, body) =>
            body.Contains("\"thinking\"", StringComparison.Ordinal)
                ? Json("""{"type":"error","error":{"type":"invalid_request_error","message":"thinking.type: adaptive is not supported"}}""", HttpStatusCode.BadRequest)
                : Json(answer));

        await client.CreateChatCompletionAsync(
            "anthropic:claude-haiku-4-5", [User("hi")], null, null, new VeniceParameters(),
            reasoning: new ReasoningChoice(false, null));

        Assert.Equal(2, recorder.Bodies.Count);
        Assert.DoesNotContain("\"thinking\"", recorder.Bodies[1], StringComparison.Ordinal);
    }

    /// <summary>Модель незнакомого провайдера не уходит никуда — прежде она ушла бы в Venice.</summary>
    [Fact]
    public async Task An_unknown_provider_sends_nothing()
    {
        var (client, recorder, _) = Build(LlmProvider.Parse("fromTheFuture"), "key", (_, _) => Json(OpenAiAnswer));

        await Assert.ThrowsAsync<VeniceApiException>(() => client.ListTextModelsAsync());
        Assert.Empty(recorder.Requests);
    }
}
