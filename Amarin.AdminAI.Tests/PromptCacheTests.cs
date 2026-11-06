using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Кэш провайдеров: что уходит каждому, куда садятся метки, что бывает при отказе и как считается цена.
/// </summary>
/// <remarks>
/// Кэш ломается молча: запросы идут, ответы приходят, только счёт выше. Поэтому здесь проверяется
/// то, что глазом не увидеть, — тело запроса и разбор токенов.
/// </remarks>
public sealed class PromptCacheTests
{
    private const string Key = "chat-1";

    private const string OpenAiAnswer = """
        {"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":1000,"completion_tokens":500,"total_tokens":1500}}
        """;

    private const string AnthropicAnswer = """
        {"type":"message","stop_reason":"end_turn","content":[{"type":"text","text":"ok"}],
         "usage":{"input_tokens":100,"cache_creation_input_tokens":200,"cache_read_input_tokens":700,"output_tokens":500}}
        """;

    private sealed class Recorder(Func<HttpRequestMessage, string, HttpResponseMessage> script) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Bodies)
            {
                Requests.Add(request);
                Bodies.Add(body);
            }

            return script(request, body);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(params string[] events) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                string.Concat(events.Select(line => "data: " + line + "\n")),
                Encoding.UTF8,
                "text/event-stream")
        };

    private static (VeniceClient Client, Recorder Recorder, List<VeniceCost> Spent) Build(
        LlmProvider provider,
        Func<HttpRequestMessage, string, HttpResponseMessage>? script = null,
        VeniceModelInfo? info = null)
    {
        var recorder = new Recorder(script ?? ((_, _) => Json(OpenAiAnswer)));
        var spent = new List<VeniceCost>();
        var options = new AgentOptions
        {
            Keys = new ApiKeyProvider("sk-test", provider),
            SpendSink = (_, cost, _) => spent.Add(cost),
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6"
        };

        return (new VeniceClient(new HttpClient(recorder), options) { ResolveModelInfo = _ => info }, recorder, spent);
    }

    /// <summary>Клиент своего сервера: адрес и протоколы едут вместе с ключом.</summary>
    private static (VeniceClient Client, Recorder Recorder) BuildServer(
        ProviderEndpoint endpoint,
        Func<HttpRequestMessage, string, HttpResponseMessage> script,
        VeniceModelInfo? info = null)
    {
        var recorder = new Recorder(script);
        var server = LlmProvider.Custom("a1b2c3");
        var credential = new ApiCredential(server, "sk-star", endpoint);
        var keys = new ApiKeyProvider();
        keys.Use(credential, null, [new KeyHandle("k1", server, credential.Secret, credential.Endpoint)]);
        var options = new AgentOptions { Keys = keys, BaseUrl = "https://api.venice.ai/api/v1", Model = "grok-4-6" };
        return (new VeniceClient(new HttpClient(recorder), options) { ResolveModelInfo = _ => info }, recorder);
    }

    private static ChatMessage System(string text) => new() { Role = "system", Content = ChatContent.Text(text) };

    private static ChatMessage User(string text) => new() { Role = "user", Content = ChatContent.Text(text) };

    private static ChatMessage Answer(string text) => new() { Role = "assistant", Content = ChatContent.Text(text) };

    private static ChatMessage Calling(string id, JsonElement? replay = null) => new()
    {
        Role = "assistant",
        ToolCalls = [new ToolCall { Id = id, Function = new FunctionCall { Name = "read_file", Arguments = "{}" } }],
        ReplayBlocks = replay
    };

    private static ChatMessage Result(string id, string text) => new()
    {
        Role = "tool",
        ToolCallId = id,
        Content = ChatContent.Text(text)
    };

    private static ToolDefinition Definition() => new()
    {
        Function = new FunctionDefinition
        {
            Name = "read_file",
            Description = "reads a file",
            Parameters = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone()
        }
    };

    private static VeniceModelInfo Priced(decimal? cacheRead, decimal? cacheWrite) => new()
    {
        Id = "m",
        ModelSpec = new VeniceModelSpec
        {
            Pricing = new VeniceModelPricing
            {
                Input = new VeniceUnitPrice { Usd = 10m },
                Output = new VeniceUnitPrice { Usd = 20m },
                CacheInput = cacheRead is null ? null : new VeniceUnitPrice { Usd = cacheRead },
                CacheWrite = cacheWrite is null ? null : new VeniceUnitPrice { Usd = cacheWrite }
            }
        }
    };

    private static JsonObject Anthropic(List<ChatMessage> messages, string? key = Key, bool cache = true, ReasoningChoice? reasoning = null) =>
        AnthropicMessages.Build(
            new ChatCompletionRequest
            {
                Model = "anthropic:claude-sonnet-5-5",
                Messages = messages,
                Tools = [Definition()],
                ToolChoice = "auto",
                CacheKey = key,
                ReasoningChoice = reasoning
            },
            "claude-sonnet-5-5",
            null,
            stream: true,
            official: true,
            cache: cache).Json;

    /// <summary>Все блоки тела с меткой кэша: в <c>system</c> и в сообщениях.</summary>
    private static List<JsonNode> MarkedBlocks(JsonObject body)
    {
        var blocks = new List<JsonNode>();
        void Collect(JsonNode? node)
        {
            if (node is JsonArray array)
            {
                blocks.AddRange(array.OfType<JsonObject>().Where(block => block["cache_control"] is not null));
            }
        }

        Collect(body["system"]);
        foreach (var message in body["messages"]!.AsArray())
        {
            Collect(message!["content"]);
        }

        return blocks;
    }

    private static JsonObject Body(string json) => JsonNode.Parse(json)!.AsObject();

    private static int Marks(string json) => MarkedBlocks(Body(json)).Count;

    // ───────────────────────── метки Anthropic ─────────────────────────

    /// <summary>
    /// Системный промпт — общая часть чатов и агентов; конец истории и последний ответ модели —
    /// начало следующего запроса. Верхнее поле ставило только метку конца.
    /// </summary>
    [Fact]
    public void A_conversation_marks_the_prompt_the_last_answer_and_the_tail()
    {
        var body = Anthropic([System("rules"), User("q"), Calling("c1"), Result("c1", "text")]);

        Assert.Null(body["cache_control"]);
        Assert.Equal("ephemeral", (string?)body["system"]![0]!["cache_control"]!["type"]);
        Assert.Null(body["system"]![0]!["cache_control"]!["ttl"]);

        var marked = MarkedBlocks(body);
        Assert.Equal(["text", "tool_use", "tool_result"], marked.Select(block => (string?)block["type"]));
    }

    /// <summary>Хвост разового запроса не повторится: запись его в кэш была бы наценкой впустую.</summary>
    [Fact]
    public void A_one_off_request_marks_only_the_prompt()
    {
        var body = Anthropic([System("rules"), User("q")], key: null);

        var marked = Assert.Single(MarkedBlocks(body));
        Assert.Equal("rules", (string?)marked["text"]);
    }

    [Fact]
    public void A_thinking_block_never_carries_a_mark()
    {
        var thinking = JsonDocument.Parse("""[{"type":"thinking","thinking":"plan","signature":"sig"}]""").RootElement.Clone();
        var body = Anthropic([User("q"), Calling("c1", thinking), Result("c1", "text")], reasoning: new ReasoningChoice(false, null));

        Assert.All(MarkedBlocks(body), block => Assert.NotEqual("thinking", (string?)block["type"]));
        Assert.Equal("thinking", (string?)body["messages"]![1]!["content"]![0]!["type"]);
    }

    [Fact]
    public void Without_cache_the_body_has_no_marks_and_a_plain_prompt()
    {
        var body = Anthropic([System("rules"), User("q"), Answer("a"), User("q2")], cache: false);

        Assert.Equal("rules", (string?)body["system"]);
        Assert.Empty(MarkedBlocks(body));
    }

    /// <summary>
    /// Агент с мышью: к следующему раунду прошлый снимок становится заглушкой, и запись на конце
    /// истории уже не совпадёт. Совпасть обязана запись на последнем ответе модели, иначе вся
    /// история переписывалась бы с наценкой на каждом раунде.
    /// </summary>
    [Fact]
    public void After_a_picture_turns_into_a_stub_the_mark_on_the_answer_still_matches()
    {
        var before = Anthropic([User("q"), Calling("c1"), Result("c1", "stub"), Calling("c2"), Result("c2", "PICTURE")]);
        var after = Anthropic([User("q"), Calling("c1"), Result("c1", "stub"), Calling("c2"), Result("c2", "stub"), Calling("c3"), Result("c3", "PICTURE 2")]);

        // Запись первого запроса заканчивается на вызове c2: до него второй запрос совпадает байт в байт.
        var first = before["messages"]!.AsArray();
        Assert.NotNull(first[3]!["content"]!.AsArray()[^1]!["cache_control"]);
        Assert.Equal(Prefix(before, 4), Prefix(after, 4));
    }

    /// <summary>Сообщения до места, без меток: сами метки на совпадение начала не влияют.</summary>
    private static string Prefix(JsonObject body, int count)
    {
        var copy = JsonNode.Parse(body["messages"]!.ToJsonString())!.AsArray();
        foreach (var message in copy)
        {
            foreach (var block in message!["content"]!.AsArray().OfType<JsonObject>())
            {
                block.Remove("cache_control");
            }
        }

        return string.Join("\n", copy.Take(count).Select(message => message!.ToJsonString()));
    }

    // ───────────────────────── метки OpenAI-формы ─────────────────────────

    [Fact]
    public void Marks_leave_the_list_they_were_given_and_every_other_message_alone()
    {
        var replay = JsonDocument.Parse("""[{"type":"thinking"}]""").RootElement.Clone();
        var answer = Calling("c1", replay);
        List<ChatMessage> messages = [System("rules"), User("q"), answer, Result("c1", "text")];

        var marked = PromptCacheMarks.Apply(messages, conversation: true);

        Assert.Equal(JsonValueKind.String, messages[0].Content!.Value.ValueKind);
        Assert.Same(answer, marked[2]);
        Assert.Equal("c1", marked[3].ToolCallId);

        // Вызов без текста метку не несёт, и она садится на текст перед ним.
        Assert.Equal("ephemeral", marked[0].Content!.Value[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("q", marked[1].Content!.Value[0].GetProperty("text").GetString());
        Assert.True(marked[1].Content!.Value[0].TryGetProperty("cache_control", out _));
        Assert.True(marked[3].Content!.Value[0].TryGetProperty("cache_control", out _));
    }

    [Fact]
    public void A_mark_sits_on_the_text_of_a_message_with_a_picture()
    {
        var picture = new ChatMessage
        {
            Role = "user",
            Content = ChatContent.ToolImages("screenshot", "here", [new Amarin.Tools.ImageAttachment("QUJD", "image/png")])
        };

        var marked = PromptCacheMarks.Apply([picture], conversation: true).Single();

        var parts = marked.Content!.Value.EnumerateArray().ToList();
        Assert.Equal(picture.Content!.Value.GetArrayLength(), parts.Count);
        Assert.Contains(parts, part => part.GetProperty("type").GetString() == "image_url");
        var text = Assert.Single(parts, part => part.TryGetProperty("cache_control", out _));
        Assert.Equal("text", text.GetProperty("type").GetString());
    }

    [Fact]
    public void A_one_off_request_keeps_its_tail_unmarked()
    {
        var marked = PromptCacheMarks.Apply([System("rules"), User("q")], conversation: false);

        Assert.Equal(JsonValueKind.Array, marked[0].Content!.Value.ValueKind);
        Assert.Equal(JsonValueKind.String, marked[1].Content!.Value.ValueKind);
    }

    // ───────────────────────── что уходит каждому провайдеру ─────────────────────────

    public static TheoryData<string, string> KeyFieldProviders => new()
    {
        { "venice", "grok-4-6" },
        { "openRouter", "openrouter:openai/gpt-6-sol" },
        { "openAI", "openai:gpt-6-sol" },
        { "moonshot", "moonshot:kimi-k3" },
        { "mistral", "mistral:mistral-large-latest" }
    };

    [Theory]
    [MemberData(nameof(KeyFieldProviders))]
    public async Task The_conversation_is_named_in_the_body(string provider, string model)
    {
        var (client, recorder, _) = Build(LlmProvider.Parse(provider));

        await client.CreateChatCompletionAsync(
            model, [System("rules"), User("q")], null, null, new VeniceParameters(), cacheKey: Key);

        Assert.Equal(Key, (string?)Body(recorder.Bodies[0])["prompt_cache_key"]);
        Assert.Equal(0, Marks(recorder.Bodies[0]));
    }

    [Fact]
    public async Task A_one_off_request_names_no_conversation()
    {
        var (client, recorder, _) = Build(LlmProvider.OpenAI);

        await client.CreateChatCompletionAsync("openai:gpt-6-sol", [User("q")], null, null, new VeniceParameters());

        Assert.Null(Body(recorder.Bodies[0])["prompt_cache_key"]);
    }

    [Fact]
    public async Task Grok_gets_the_conversation_in_a_header_and_not_in_the_body()
    {
        var (client, recorder, _) = Build(LlmProvider.XAi);

        await client.CreateChatCompletionAsync("xai:grok-4-6", [User("q")], null, null, new VeniceParameters(), cacheKey: Key);

        Assert.Equal(Key, recorder.Requests[0].Headers.GetValues("x-grok-conv-id").Single());
        Assert.Null(Body(recorder.Bodies[0])["prompt_cache_key"]);
    }

    /// <summary>Эти серверы кэшируют сами, а незнакомое поле могли бы отвергнуть.</summary>
    [Theory]
    [InlineData("google", "google:gemini-3-pro")]
    [InlineData("deepSeek", "deepseek:deepseek-chat")]
    [InlineData("zAi", "zai:glm-5")]
    [InlineData("groq", "groq:openai/gpt-oss-120b")]
    public async Task Providers_that_cache_on_their_own_get_nothing_extra(string provider, string model)
    {
        var (client, recorder, _) = Build(LlmProvider.Parse(provider));

        await client.CreateChatCompletionAsync(model, [System("rules"), User("q")], null, null, new VeniceParameters(), cacheKey: Key);

        Assert.Null(Body(recorder.Bodies[0])["prompt_cache_key"]);
        Assert.False(recorder.Requests[0].Headers.Contains("x-grok-conv-id"));
        Assert.Equal(0, Marks(recorder.Bodies[0]));
    }

    [Fact]
    public async Task A_custom_openai_server_gets_nothing_extra()
    {
        var endpoint = new ProviderEndpoint("star", "https://star.example/v1", true, false);
        var (client, recorder) = BuildServer(endpoint, (_, _) => Json(OpenAiAnswer));

        await client.CreateChatCompletionAsync(
            ModelRef.Qualify(LlmProvider.Custom("a1b2c3"), "gpt-6-sol"),
            [System("rules"), User("q")], null, null, new VeniceParameters(), cacheKey: Key);

        Assert.Null(Body(recorder.Bodies[0])["prompt_cache_key"]);
        Assert.Equal(0, Marks(recorder.Bodies[0]));
    }

    [Theory]
    [InlineData("openRouter", "openrouter:anthropic/claude-sonnet-4.5", 2)]
    [InlineData("openRouter", "openrouter:~anthropic/claude-haiku-latest", 2)]
    [InlineData("openRouter", "openrouter:qwen/qwen3-coder-plus", 2)]
    [InlineData("qwen", "qwen:qwen3.7-plus", 2)]
    [InlineData("openRouter", "openrouter:google/gemini-3-pro", 0)]
    public async Task Models_that_cache_only_by_marks_get_them(string provider, string model, int marks)
    {
        var (client, recorder, _) = Build(LlmProvider.Parse(provider));

        await client.CreateChatCompletionAsync(model, [System("rules"), User("q")], null, null, new VeniceParameters(), cacheKey: Key);

        Assert.Equal(marks, Marks(recorder.Bodies[0]));
    }

    /// <summary>Роль <c>tool</c> у агента последняя в каждом раунде: метка уходит блоком, не строкой.</summary>
    [Fact]
    public async Task A_tool_result_at_the_tail_goes_as_a_block_with_its_mark()
    {
        var (client, recorder, _) = Build(LlmProvider.OpenRouter);

        await client.CreateChatCompletionAsync(
            "openrouter:anthropic/claude-sonnet-4.5",
            [System("rules"), User("q"), Calling("c1"), Result("c1", "text")],
            [Definition()], "auto", new VeniceParameters(), cacheKey: Key);

        var tail = Body(recorder.Bodies[0])["messages"]!.AsArray()[^1]!;
        Assert.Equal("tool", (string?)tail["role"]);
        Assert.Equal("c1", (string?)tail["tool_call_id"]);
        Assert.Equal("ephemeral", (string?)tail["content"]![0]!["cache_control"]!["type"]);
    }

    /// <summary>Заявку считают когда придётся: кэш к этому времени истечёт.</summary>
    [Fact]
    public async Task The_batch_queue_gets_no_cache()
    {
        var (client, recorder, _) = Build(LlmProvider.OpenRouter, (_, _) => Json("""{"error":{"message":"no"}}""", HttpStatusCode.BadRequest));

        await Assert.ThrowsAnyAsync<VeniceApiException>(() => client.CreateChatCompletionAsync(
            "openrouter:anthropic/claude-sonnet-4.5:batch", [System("rules"), User("q")], null, null, new VeniceParameters(), cacheKey: Key));

        Assert.DoesNotContain("cache_control", recorder.Bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_cache_key", recorder.Bodies[0], StringComparison.Ordinal);
    }

    /// <summary>Claude не кэшируется без меток, поэтому и посредник их получает, а бета-полей нет.</summary>
    [Fact]
    public async Task A_custom_anthropic_server_gets_the_marks()
    {
        var endpoint = new ProviderEndpoint("star", "https://star.example/v1", true, true);
        var (client, recorder) = BuildServer(endpoint, (_, _) => Json(AnthropicAnswer));

        await client.CreateChatCompletionAsync(
            ModelRef.Qualify(LlmProvider.Custom("a1b2c3"), "claude-sonnet-5-5"),
            [System("rules"), User("q")], null, null, new VeniceParameters(), cacheKey: Key);

        Assert.Equal("https://star.example/v1/messages", recorder.Requests[0].RequestUri!.ToString());
        Assert.Equal(2, Marks(recorder.Bodies[0]));
        Assert.False(recorder.Requests[0].Headers.Contains("anthropic-beta"));
    }

    // ───────────────────────── отказ полям кэша ─────────────────────────

    [Fact]
    public async Task A_refused_key_is_dropped_and_the_reasoning_stays()
    {
        var (client, recorder, _) = Build(LlmProvider.Moonshot, (_, body) => body.Contains("prompt_cache_key", StringComparison.Ordinal)
            ? Json("""{"error":{"message":"unknown field prompt_cache_key"}}""", HttpStatusCode.BadRequest)
            : Json(OpenAiAnswer));

        await client.CreateChatCompletionAsync(
            "moonshot:kimi-k3", [User("q")], null, null, new VeniceParameters(), reasoning: new ReasoningChoice(false, "high"), cacheKey: Key);
        await client.CreateChatCompletionAsync(
            "moonshot:kimi-k3", [User("q")], null, null, new VeniceParameters(), reasoning: new ReasoningChoice(false, "high"), cacheKey: Key);

        // Второй вопрос идёт сразу без ключа: иначе каждый запрос уходил бы дважды.
        Assert.Equal(3, recorder.Bodies.Count);
        Assert.Equal("high", (string?)Body(recorder.Bodies[1])["reasoning_effort"]);
        Assert.Null(Body(recorder.Bodies[2])["prompt_cache_key"]);
    }

    [Fact]
    public async Task Refused_marks_are_dropped_on_openai_form()
    {
        var (client, recorder, _) = Build(LlmProvider.Qwen, (_, body) => body.Contains("cache_control", StringComparison.Ordinal)
            ? Json("""{"error":{"message":"cache_control is not supported"}}""", (HttpStatusCode)422)
            : Json(OpenAiAnswer));

        await client.CreateChatCompletionAsync("qwen:qwen-old", [System("rules"), User("q")], null, null, new VeniceParameters(), cacheKey: Key);

        Assert.Equal(2, recorder.Bodies.Count);
        Assert.Equal("rules", (string?)Body(recorder.Bodies[1])["messages"]![0]!["content"]);
    }

    /// <summary>
    /// Прежний повтор снимал вместе с метками и размышление: у посредника, не знающего меток, оно
    /// пропадало бы на каждом запросе.
    /// </summary>
    [Fact]
    public async Task A_server_refusing_marks_keeps_the_thinking()
    {
        var info = new VeniceModelInfo
        {
            Id = "claude-sonnet-5-5",
            ModelSpec = new VeniceModelSpec { Capabilities = new VeniceModelCapabilities { ThinkingModes = ["adaptive"] } }
        };
        var endpoint = new ProviderEndpoint("star", "https://star.example/v1", true, true);
        var (client, recorder) = BuildServer(
            endpoint,
            (_, body) => body.Contains("cache_control", StringComparison.Ordinal)
                ? Json("""{"type":"error","error":{"message":"Extra inputs: cache_control"}}""", HttpStatusCode.BadRequest)
                : Json(AnthropicAnswer),
            info);
        var model = ModelRef.Qualify(LlmProvider.Custom("a1b2c3"), "claude-sonnet-5-5");

        await client.CreateChatCompletionAsync(
            model, [System("rules"), User("q")], null, null, new VeniceParameters(), reasoning: new ReasoningChoice(false, "high"), cacheKey: Key);
        await client.CreateChatCompletionAsync(
            model, [System("rules"), User("q")], null, null, new VeniceParameters(), reasoning: new ReasoningChoice(false, "high"), cacheKey: Key);

        Assert.Equal(3, recorder.Bodies.Count);
        Assert.Equal("adaptive", (string?)Body(recorder.Bodies[1])["thinking"]!["type"]);
        Assert.Equal(0, Marks(recorder.Bodies[1]));
        Assert.Equal("rules", (string?)Body(recorder.Bodies[2])["system"]);
    }

    /// <summary>Отказ полям размышления кэш не отменяет: метки здесь ни при чём.</summary>
    [Fact]
    public async Task A_refusal_of_thinking_leaves_the_marks()
    {
        var (client, recorder, _) = Build(LlmProvider.Anthropic, (_, body) => body.Contains("output_config", StringComparison.Ordinal)
            ? Json("""{"type":"error","error":{"message":"output_config: not supported"}}""", HttpStatusCode.BadRequest)
            : Json(AnthropicAnswer));

        await client.CreateChatCompletionAsync(
            "anthropic:claude-sonnet-5-5", [System("rules"), User("q")], null, null, new VeniceParameters(),
            reasoning: new ReasoningChoice(false, "high"), cacheKey: Key);

        Assert.Equal(2, recorder.Bodies.Count);
        Assert.Null(Body(recorder.Bodies[1])["output_config"]);
        Assert.Equal(2, Marks(recorder.Bodies[1]));
    }

    // ───────────────────────── токены и цена ─────────────────────────

    [Theory]
    [InlineData("""{"prompt_tokens":10,"prompt_tokens_details":{"cached_tokens":7,"cache_write_tokens":2}}""", 7, 2)]
    [InlineData("""{"prompt_tokens":10,"prompt_tokens_details":{"cached_tokens":7,"cache_creation_input_tokens":3}}""", 7, 3)]
    [InlineData("""{"prompt_tokens":10,"prompt_cache_hit_tokens":6,"prompt_cache_miss_tokens":4}""", 6, 0)]
    [InlineData("""{"prompt_tokens":10,"cached_tokens":5}""", 5, 0)]
    [InlineData("""{"prompt_tokens":10}""", 0, 0)]
    public void Every_spelling_of_cached_tokens_is_read(string usage, int read, int written)
    {
        var response = JsonSerializer.Deserialize(
            "{\"choices\":[],\"usage\":" + usage + "}", VeniceJsonContext.Default.ChatCompletionResponse);

        Assert.Equal(read, response!.Usage!.CacheReadTokens);
        Assert.Equal(written, response.Usage.CacheWriteTokens);
    }

    /// <summary>Поле для учёта не должно отнимать ответ: посредники пишут здесь что угодно.</summary>
    [Theory]
    [InlineData("""{"prompt_tokens":10,"prompt_tokens_details":[]}""")]
    [InlineData("""{"prompt_tokens":10,"prompt_tokens_details":null}""")]
    [InlineData("""{"prompt_tokens":10,"prompt_tokens_details":{"cached_tokens":"7"}}""")]
    [InlineData("""{"prompt_tokens":10,"prompt_cache_hit_tokens":{},"cached_tokens":[]}""")]
    [InlineData("""{"prompt_tokens":10,"prompt_cache_hit_tokens":70000000000000000000}""")]
    public void A_strange_breakdown_does_not_cost_the_answer(string usage)
    {
        var response = JsonSerializer.Deserialize(
            "{\"choices\":[],\"usage\":" + usage + "}", VeniceJsonContext.Default.ChatCompletionResponse);

        Assert.Equal(10, response!.Usage!.PromptTokens);
        Assert.Equal(0, response.Usage.CacheReadTokens);
    }

    [Fact]
    public void The_anthropic_answer_names_its_cache_tokens()
    {
        var usage = AnthropicMessages.ParseResponse(AnthropicAnswer).Usage!;

        Assert.Equal(1000, usage.PromptTokens);
        Assert.Equal(700, usage.CacheReadTokens);
        Assert.Equal(200, usage.CacheWriteTokens);
    }

    [Fact]
    public async Task Read_tokens_cost_the_cache_price_and_stay_in_the_token_count()
    {
        const string answer = """
            {"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1000,"completion_tokens":500,"total_tokens":1500,"prompt_tokens_details":{"cached_tokens":800}}}
            """;
        var (client, _, spent) = Build(LlmProvider.OpenAI, (_, _) => Json(answer), Priced(cacheRead: 1m, cacheWrite: null));

        await client.CreateChatCompletionAsync("openai:gpt-6-sol", [User("q")], null, null, new VeniceParameters());

        // 200 × 10 + 800 × 1 + 500 × 20 за миллион.
        var cost = Assert.Single(spent);
        Assert.Equal(0.0128m, cost.Usd);
        Assert.Equal(1000, cost.InputTokens);
        Assert.True(cost.Estimated);
    }

    [Fact]
    public async Task Written_tokens_cost_the_write_price()
    {
        var (client, _, spent) = Build(LlmProvider.Anthropic, (_, _) => Json(AnthropicAnswer), Priced(cacheRead: 1m, cacheWrite: 12.5m));

        await client.CreateChatCompletionAsync("anthropic:claude-sonnet-5-5", [User("q")], null, null, new VeniceParameters());

        // 100 × 10 + 700 × 1 + 200 × 12,5 + 500 × 20 за миллион.
        Assert.Equal(0.0142m, Assert.Single(spent).Usd);
    }

    /// <summary>Каталог цены кэша не назвал — всё считается как прежде, по обычному входу.</summary>
    [Fact]
    public async Task Without_cache_prices_the_tokens_cost_the_input_price()
    {
        var (client, _, spent) = Build(LlmProvider.Anthropic, (_, _) => Json(AnthropicAnswer), Priced(cacheRead: null, cacheWrite: null));

        await client.CreateChatCompletionAsync("anthropic:claude-sonnet-5-5", [User("q")], null, null, new VeniceParameters());

        Assert.Equal(0.02m, Assert.Single(spent).Usd);
    }

    [Fact]
    public async Task A_named_price_is_not_recounted()
    {
        const string answer = """
            {"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1000,"completion_tokens":500,"total_tokens":1500,"cost":0.5,"prompt_tokens_details":{"cached_tokens":800}}}
            """;
        var (client, _, spent) = Build(LlmProvider.OpenRouter, (_, _) => Json(answer), Priced(cacheRead: 1m, cacheWrite: null));

        await client.CreateChatCompletionAsync("openrouter:openai/gpt-6-sol", [User("q")], null, null, new VeniceParameters());

        Assert.Equal(0.5m, Assert.Single(spent).Usd);
    }

    [Fact]
    public async Task The_stream_counts_cached_tokens_too()
    {
        var (client, _, spent) = Build(
            LlmProvider.OpenAI,
            (_, _) => Sse(
                """{"choices":[{"delta":{"content":"ok"}}]}""",
                """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
                """{"choices":[],"usage":{"prompt_tokens":1000,"completion_tokens":500,"total_tokens":1500,"prompt_tokens_details":{"cached_tokens":800}}}""",
                "[DONE]"),
            Priced(cacheRead: 1m, cacheWrite: null));

        await client.StreamChatCompletionAsync(
            "openai:gpt-6-sol", "openai:gpt-6-sol", [User("q")], null, null, new VeniceParameters(), null, fallback: false);

        Assert.Equal(0.0128m, Assert.Single(spent).Usd);
    }

    [Fact]
    public async Task The_anthropic_stream_counts_cached_tokens_too()
    {
        var (client, _, spent) = Build(
            LlmProvider.Anthropic,
            (_, _) => Sse(
                """{"type":"message_start","message":{"usage":{"input_tokens":100,"cache_creation_input_tokens":200,"cache_read_input_tokens":700}}}""",
                """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":"ok"}}""",
                """{"type":"content_block_stop","index":0}""",
                """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":500}}"""),
            Priced(cacheRead: 1m, cacheWrite: 12.5m));

        await client.StreamChatCompletionAsync(
            "anthropic:claude-sonnet-5-5", "anthropic:claude-sonnet-5-5", [User("q")], null, null, new VeniceParameters(), null, fallback: false);

        Assert.Equal(0.0142m, Assert.Single(spent).Usd);
    }

    [Fact]
    public void The_price_book_brings_cache_prices()
    {
        var book = new ModelPriceBook(new HttpClient(), cachePath: null);
        book.Load("""
            {"data":[{"id":"anthropic/claude-sonnet-5.5","pricing":
              {"prompt":"0.000002","completion":"0.00001","input_cache_read":"0.0000002","input_cache_write":"0.0000025"}}]}
            """);

        var model = book.Enrich(LlmProvider.Anthropic, [new VeniceModelInfo { Id = "anthropic:claude-sonnet-5-5" }]).Single();

        Assert.Equal(0.2m, model.ModelSpec?.Pricing?.CacheInput?.Usd);
        Assert.Equal(2.5m, model.ModelSpec?.Pricing?.CacheWrite?.Usd);
    }

    // ───────────────────────── чат и агент ─────────────────────────

    private static AppSettings QuietSettings()
    {
        var settings = AppSettings.CreateDefault();
        settings.AgentLiteModelId = "agent-lite";
        settings.SynGuardEnabled = false;
        return settings;
    }

    /// <summary>Один чат — один ключ, что бы ни шло рядом: заголовок и сводка своего хвоста не повторят.</summary>
    [Fact]
    public async Task A_chat_turn_goes_under_the_id_of_the_chat()
    {
        var recorder = new Recorder((_, _) => Sse(
            """{"choices":[{"delta":{"content":"ok"}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]"));
        var options = new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "grok-4-6", MaxToolRounds = 2 };
        using var http = new HttpClient(recorder) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var settings = QuietSettings();
        var engine = new ChatEngine(new VeniceClient(http, options), options, () => settings, new ToolRegistry([]));
        var session = new ChatSession { Id = Key, SelectedModelId = "grok-4-6" };
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "q" });
        session.ApiMessages.Add(User("q"));

        await engine.GenerateAssistantAsync(session, SilentTurnObserver.Instance, CancellationToken.None);

        Assert.Contains(recorder.Bodies, body => (string?)Body(body)["prompt_cache_key"] == Key);
    }

    private static AgentHost HostOn(Recorder recorder, AppSettings settings) =>
        new(
            new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "agent-lite", MaxToolRounds = 3 },
            new HttpClient { BaseAddress = new Uri("https://example.invalid/") },
            () => settings,
            new ConfirmationQueue(() => settings),
            new AgentRegistry())
        {
            Route = (_, _, _) => Task.FromResult(new AgentTierDecision("lite", null)),
            Http = _ => new HttpClient(recorder, disposeHandler: false) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") }
        };

    [Fact]
    public async Task An_agent_goes_under_its_own_key_inside_the_chat()
    {
        var recorder = new Recorder((_, _) => Json(OpenAiAnswer));
        var settings = QuietSettings();
        var host = HostOn(recorder, settings);

        using (AgentRunScope.Push(new AgentRunContext
        {
            Call = new ToolCallRecord { Id = "call", Name = InitAgentTool.ToolName },
            Assistant = new ChatDisplayMessage { Role = "assistant" },
            Observer = SilentTurnObserver.Instance,
            SessionId = Key,
            Conversations = new AgentConversations()
        }))
        {
            await host.RunAsync("task-text", null, CancellationToken.None);
        }

        var key = Assert.Single(recorder.Bodies.Select(body => (string?)Body(body)["prompt_cache_key"]).Distinct());
        Assert.StartsWith(Key + ":", key, StringComparison.Ordinal);
        Assert.True(key!.Length > Key.Length + 1, "ключ агента обязан отличаться от ключа чата");
    }

    /// <summary>
    /// Прямой провайдер цену не называет, её считает клиент по каталогу. Клиент агента каталога не
    /// получал, и его работа записывалась одними токенами: график трат и лимиты её не видели.
    /// </summary>
    [Fact]
    public async Task An_agent_on_a_direct_provider_is_priced_by_the_catalog()
    {
        const string answer = """
            {"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1000,"completion_tokens":500,"total_tokens":1500,"prompt_tokens_details":{"cached_tokens":800}}}
            """;
        var recorder = new Recorder((_, _) => Json(answer));
        var spent = new List<VeniceCost>();
        var settings = QuietSettings();
        settings.AgentLiteModelId = "openai:gpt-6-sol";
        var host = new AgentHost(
            new AgentOptions
            {
                Keys = new ApiKeyProvider("sk-test", LlmProvider.OpenAI),
                SpendSink = (_, cost, _) => spent.Add(cost),
                BaseUrl = "https://api.venice.ai/api/v1",
                Model = "openai:gpt-6-sol",
                MaxToolRounds = 3
            },
            new HttpClient { BaseAddress = new Uri("https://example.invalid/") },
            () => settings,
            new ConfirmationQueue(() => settings),
            new AgentRegistry(),
            _ => Priced(cacheRead: 1m, cacheWrite: null))
        {
            Route = (_, _, _) => Task.FromResult(new AgentTierDecision("lite", null)),
            Http = _ => new HttpClient(recorder, disposeHandler: false) { BaseAddress = new Uri("https://api.openai.com/v1/") }
        };

        await host.RunAsync("task-text", null, CancellationToken.None);

        // 200 × 10 + 800 × 1 + 500 × 20 за миллион.
        var cost = Assert.Single(spent);
        Assert.Equal(0.0128m, cost.Usd);
        Assert.True(cost.Estimated);
    }

    /// <summary>Прогону по расписанию чата нет, а общий ключ свёл бы все прогоны на одну машину.</summary>
    [Fact]
    public async Task Agents_without_a_chat_get_a_key_of_their_own()
    {
        var recorder = new Recorder((_, _) => Json(OpenAiAnswer));
        var host = HostOn(recorder, QuietSettings());

        await host.RunAsync("task-text", null, CancellationToken.None);
        await host.RunAsync("task-text", null, CancellationToken.None);

        var keys = recorder.Bodies.Select(body => (string?)Body(body)["prompt_cache_key"]).Distinct().ToList();
        Assert.Equal(2, keys.Count);
        Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(key)));
    }
}
