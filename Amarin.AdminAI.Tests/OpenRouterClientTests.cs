using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Разговор с OpenRouter: тело запроса, цена, размышление, каталог, остаток и адрес.
/// </summary>
/// <remarks>
/// Все отличия от Venice невидимы глазом — они в теле запроса и в разборе ответа. Ошибка здесь
/// выглядит либо как «модель не найдена», либо как пустой график трат, и объяснить её человеку
/// нечем.
/// </remarks>
public sealed class OpenRouterClientTests
{
    private const string VeniceKey = "vk-venice-key";
    private const string OpenRouterKey = "sk-or-v1-key";

    private static (VeniceClient Client, ApiKeyProvider Keys, Recorder Recorder) Build(
        Func<HttpRequestMessage, string, HttpResponseMessage> script,
        LlmProvider provider = LlmProvider.OpenRouter,
        Action<string, VeniceCost, string>? spendSink = null)
    {
        var recorder = new Recorder(script);
        var keys = new ApiKeyProvider(
            provider == LlmProvider.Venice ? VeniceKey : OpenRouterKey, provider);
        var options = new AgentOptions
        {
            Keys = keys,
            SpendSink = spendSink,
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            MaxToolRounds = 2
        };

        return (new VeniceClient(new HttpClient(recorder), options), keys, recorder);
    }

    // ───────────────────────── тело запроса ─────────────────────────

    /// <summary>
    /// Надстроек Venice в теле быть не должно — чужому серверу они незнакомы, — а просьба
    /// посчитать деньги быть обязана: без неё OpenRouter цену не сообщает вовсе.
    /// </summary>
    [Fact]
    public async Task The_body_carries_no_venice_parameters_and_asks_for_the_price()
    {
        var (client, _, recorder) = Build((_, _) => Json(Completion()));

        await client.CreateChatCompletionAsync(
            "openrouter:anthropic/claude-sonnet-4.5",
            [User("привет")],
            tools: null,
            toolChoice: null,
            new VeniceParameters { EnableWebSearch = "off" });

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.False(body.RootElement.TryGetProperty("venice_parameters", out _));
        Assert.True(body.RootElement.GetProperty("usage").GetProperty("include").GetBoolean());
    }

    /// <summary>Приставка провайдера — наша выдумка; на провод уходит имя, которое знает сервер.</summary>
    [Fact]
    public async Task The_model_goes_on_the_wire_without_the_prefix()
    {
        var (client, _, recorder) = Build((_, _) => Json(Completion()));

        await client.CreateChatCompletionAsync(
            "openrouter:anthropic/claude-3.7-sonnet:thinking",
            [User("привет")],
            tools: null,
            toolChoice: null,
            new VeniceParameters());

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.Equal("anthropic/claude-3.7-sonnet:thinking", body.RootElement.GetProperty("model").GetString());
    }

    /// <summary>У Venice всё осталось как было — это вторая половина той же проверки.</summary>
    [Fact]
    public async Task Venice_still_gets_its_own_shape()
    {
        var (client, _, recorder) = Build((_, _) => Json(Completion()), LlmProvider.Venice);

        await client.CreateChatCompletionAsync(
            "grok-4-6",
            [User("привет")],
            tools: null,
            toolChoice: null,
            new VeniceParameters { EnableWebSearch = "off" });

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.True(body.RootElement.TryGetProperty("venice_parameters", out _));
        Assert.False(body.RootElement.TryGetProperty("usage", out _));
        Assert.Equal("grok-4-6", body.RootElement.GetProperty("model").GetString());
    }

    /// <summary>
    /// Размышление уходит вложенным объектом, а не верхним <c>reasoning_effort</c>: так его
    /// описывает OpenRouter.
    /// </summary>
    [Fact]
    public async Task Thinking_goes_in_the_nested_object()
    {
        var (client, _, recorder) = Build((_, _) => Json(Completion()));

        await client.CreateChatCompletionAsync(
            "openrouter:openai/gpt-5",
            [User("привет")],
            tools: null,
            toolChoice: null,
            new VeniceParameters(),
            CancellationToken.None,
            new ReasoningChoice(DisableThinking: false, "high"));

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.Equal("high", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    /// <summary>
    /// «Отключить размышление» — отсутствие поля, а не <c>exclude: true</c>: последнее значит
    /// «думай, но не показывай», и токены за это списывают, тогда как переключатель в программе
    /// задуман как экономия.
    /// </summary>
    [Fact]
    public async Task Disabled_thinking_sends_no_reasoning_at_all()
    {
        var (client, _, recorder) = Build((_, _) => Json(Completion()));

        await client.CreateChatCompletionAsync(
            "openrouter:openai/gpt-5",
            [User("привет")],
            tools: null,
            toolChoice: null,
            new VeniceParameters(),
            CancellationToken.None,
            ReasoningChoice.Disabled);

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.False(body.RootElement.TryGetProperty("reasoning", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    /// <summary>
    /// Запрет усилия рядом с инструментами — свойство Venice, а не моделей: тот же GPT-5.x за
    /// OpenRouter принимает и то и другое. Разбор имени этой разницы не видит, имена у
    /// провайдеров одни и те же.
    /// </summary>
    [Fact]
    public void The_gpt5_tools_workaround_is_venice_only()
    {
        Assert.True(ReasoningPolicy.FamilyBlocksEffortWithTools("openai-gpt-56-luna"));
        Assert.False(ReasoningPolicy.FamilyBlocksEffortWithTools("openrouter:openai/gpt-5.6-luna"));
    }

    // ───────────────────────── цена ─────────────────────────

    /// <summary>
    /// Своего поля цены у OpenRouter нет — она внутри <c>usage</c>. Без этой ветки график трат
    /// по ключу OpenRouter остался бы пустым навсегда.
    /// </summary>
    [Fact]
    public async Task The_price_comes_out_of_usage()
    {
        var spent = new List<(string Sku, decimal Usd)>();
        var (client, _, _) = Build(
            (_, _) => Json(Completion(usageCost: 0.0123m)),
            spendSink: (_, cost, sku) => spent.Add((sku, cost.Usd)));

        await client.CreateChatCompletionAsync(
            "openrouter:openai/gpt-5",
            [User("привет")],
            tools: null,
            toolChoice: null,
            new VeniceParameters());

        Assert.Equal(0.0123m, client.RequestCost.Usd);
        Assert.Equal(("openrouter:openai/gpt-5", 0.0123m), spent.Single());
    }

    /// <summary>Цена приходит на завершающем куске, у которого <c>choices</c> пуст.</summary>
    [Fact]
    public async Task The_streamed_price_comes_out_of_usage()
    {
        var (client, _, _) = Build((_, _) => Sse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"да\"}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":99,\"cost\":0.0456}}\n" +
            "data: [DONE]\n"));

        var streamed = await client.StreamChatCompletionAsync(
            "openrouter:openai/gpt-5",
            "openrouter:openai/gpt-5",
            [User("привет")],
            tools: null,
            toolChoice: null,
            new VeniceParameters(),
            onText: null);

        Assert.Equal(0.0456m, streamed.Cost.Usd);
        Assert.Equal(99, streamed.PromptTokens);
    }

    /// <summary>
    /// Venice называет цену верхним полем. Если вдруг ответили оба, считаем один раз — иначе
    /// человек увидел бы в шапке сообщения двойной счёт.
    /// </summary>
    [Fact]
    public async Task Two_prices_in_one_answer_are_counted_once()
    {
        var (client, _, _) = Build((_, _) => Json(Completion(usageCost: 0.02m, topLevelCost: 0.02m)));

        await client.CreateChatCompletionAsync(
            "openrouter:openai/gpt-5",
            [User("привет")],
            tools: null,
            toolChoice: null,
            new VeniceParameters());

        Assert.Equal(0.02m, client.RequestCost.Usd);
    }

    /// <summary>
    /// Размышление у OpenRouter приходит под именем <c>reasoning</c>, у Venice — под
    /// <c>reasoning_content</c>. Одно и то же поле под двумя именами.
    /// </summary>
    [Fact]
    public async Task Thinking_arrives_under_either_name()
    {
        var (client, _, _) = Build((_, _) => Sse(
            "data: {\"choices\":[{\"delta\":{\"reasoning\":\"размышляю\"}}]}\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"ответ\"},\"finish_reason\":\"stop\"}]}\n" +
            "data: [DONE]\n"));

        var streamed = await client.StreamChatCompletionAsync(
            "openrouter:openai/gpt-5",
            "openrouter:openai/gpt-5",
            [User("привет")],
            tools: null,
            toolChoice: null,
            new VeniceParameters(),
            onText: null);

        Assert.Equal("размышляю", streamed.ReasoningText);
        Assert.Equal("ответ", streamed.Text);
    }

    // ───────────────────────── каталог ─────────────────────────

    /// <summary>
    /// Отбор по поддержке инструментов делает сам сервер: без них модель этой программе
    /// бесполезна, а полный список — под четыре сотни строк на каждое обновление.
    /// </summary>
    [Fact]
    public async Task The_catalogue_is_filtered_on_the_server()
    {
        var (client, _, recorder) = Build((_, _) => Json(Models()));

        await client.ListTextModelsAsync();

        Assert.Contains("supported_parameters=tools", recorder.Urls[0], StringComparison.Ordinal);
        Assert.StartsWith("https://openrouter.ai/api/v1/", recorder.Urls[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_catalogue_arrives_in_the_shape_the_picker_expects()
    {
        var (client, _, _) = Build((_, _) => Json(Models()));

        var models = await client.ListTextModelsAsync();
        var sonnet = models.Single(model => model.Id.EndsWith("claude-sonnet-4.5", StringComparison.Ordinal));

        Assert.Equal("openrouter:anthropic/claude-sonnet-4.5", sonnet.Id);
        Assert.Equal("Claude Sonnet 4.5", sonnet.ModelSpec?.Name);
        Assert.Equal(200000, sonnet.ModelSpec?.AvailableContextTokens);
        Assert.True(sonnet.ModelSpec?.Capabilities?.SupportsFunctionCalling);
        Assert.True(sonnet.ModelSpec?.Capabilities?.SupportsReasoning);
        Assert.True(sonnet.ModelSpec?.Capabilities?.SupportsVision);
    }

    /// <summary>
    /// У Venice в каталог пускают только те модели, что и вызывают инструменты, и умеют
    /// размышлять. У OpenRouter второе требование выбросило бы половину хороших моделей —
    /// быстрые Gemini и Command-R не рассуждают, но инструменты вызывают прекрасно.
    /// </summary>
    [Fact]
    public async Task A_model_without_thinking_still_makes_the_list()
    {
        var (client, _, _) = Build((_, _) => Json(Models()));

        var models = VeniceModelCatalog.FilterAgentic(await client.ListTextModelsAsync());

        Assert.Contains(models, model => model.Id == "openrouter:cohere/command-r");
        Assert.DoesNotContain(models, model => model.Id == "openrouter:some/no-tools");
    }

    // ───────────────────────── остаток ─────────────────────────

    /// <summary>Остаток до потолка трат точнее общего остатка счёта — и стоит один запрос.</summary>
    [Fact]
    public async Task A_key_with_a_spending_cap_needs_one_request()
    {
        var (client, _, recorder) = Build((_, _) => Json(
            "{\"data\":{\"usage\":3.5,\"limit\":10,\"limit_remaining\":6.5,\"is_free_tier\":false}}"));

        var limits = await client.GetRateLimitsAsync();

        Assert.Equal(6.5m, limits.Balances?.Usd);
        Assert.Equal(3.5m, limits.SpentUsd);
        Assert.True(limits.AccessPermitted);
        Assert.Single(recorder.Urls);
    }

    /// <summary>Потолка нет — остаток досчитывается вторым запросом.</summary>
    [Fact]
    public async Task A_key_without_a_cap_asks_for_the_credits()
    {
        var (client, _, recorder) = Build((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/key", StringComparison.Ordinal)
                ? Json("{\"data\":{\"usage\":4,\"limit\":null,\"is_free_tier\":false}}")
                : Json("{\"data\":{\"total_credits\":10,\"total_usage\":4}}"));

        var limits = await client.GetRateLimitsAsync();

        Assert.Equal(6m, limits.Balances?.Usd);
        Assert.Equal(4m, limits.SpentUsd);
        Assert.Equal(2, recorder.Urls.Count);
    }

    /// <summary>
    /// Неверный ключ обязан прийти тем же исключением, что и у Venice: на нём стоит фильтр
    /// <c>catch</c> в диалоге добавления ключа.
    /// </summary>
    [Fact]
    public async Task A_bad_key_is_refused_the_same_way()
    {
        var (client, _, _) = Build((_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":{\"message\":\"No auth credentials found\"}}")
            });

        var failure = await Assert.ThrowsAsync<VeniceApiException>(() => client.GetRateLimitsAsync());
        Assert.Contains("OpenRouter", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Журнала списаний у OpenRouter нет вовсе. Отказ приходит тем же исключением, что и отказ
    /// Venice без админ-ключа: вызывающий на оба отвечает одинаково — своим журналом.
    /// </summary>
    [Fact]
    public async Task There_is_no_spending_journal_to_ask_for()
    {
        var (client, _, recorder) = Build((_, _) => Json("{}"));

        await Assert.ThrowsAsync<VeniceAdminKeyRequiredException>(() => client.GetUsageHistoryAsync());
        Assert.Empty(recorder.Urls);
    }

    // ───────────────────────── адрес ─────────────────────────

    /// <summary>
    /// Главная проверка на снятие <c>BaseAddress</c>: адрес и ключ теперь садятся на сам
    /// запрос, поэтому один и тот же живой клиент обязан пойти к другому серверу, как только
    /// человек сменил активный ключ. Пересоздать его негде — он роздан движку чата и
    /// службам как <c>readonly</c>.
    /// </summary>
    [Fact]
    public async Task One_live_client_follows_the_active_key_to_another_server()
    {
        var (client, keys, recorder) = Build((_, _) => Json(Completion()), LlmProvider.Venice);

        await client.CreateChatCompletionAsync(
            "grok-4-6", [User("раз")], null, null, new VeniceParameters());

        keys.Use(new ApiCredential(LlmProvider.OpenRouter, OpenRouterKey));

        await client.CreateChatCompletionAsync(
            "openrouter:openai/gpt-5", [User("два")], null, null, new VeniceParameters());

        Assert.Equal("https://api.venice.ai/api/v1/chat/completions", recorder.Urls[0]);
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", recorder.Urls[1]);
        Assert.Equal("Bearer " + VeniceKey, recorder.Authorizations[0]);
        Assert.Equal("Bearer " + OpenRouterKey, recorder.Authorizations[1]);
    }

    // ───────────────────────── то, что умеет только Venice ─────────────────────────

    /// <summary>
    /// Картинки живут только у Venice. При активном ключе OpenRouter они обязаны уйти в Venice
    /// сохранённым ключом — и никак иначе: ключ OpenRouter на сервере Venice был бы и отказом,
    /// и секретом, уехавшим не туда.
    /// </summary>
    [Fact]
    public async Task Drawing_uses_the_stored_venice_key()
    {
        var recorder = new Recorder((_, _) => Json("{\"images\":[\"AAA\"]}"));
        var keys = new ApiKeyProvider();
        keys.Use(
            new ApiCredential(LlmProvider.OpenRouter, OpenRouterKey),
            new ApiCredential(LlmProvider.Venice, VeniceKey));

        var client = new VeniceClient(
            new HttpClient(recorder),
            new AgentOptions { Keys = keys, BaseUrl = "https://api.venice.ai/api/v1" });

        await client.GenerateImageAsync("кот");

        Assert.Equal("https://api.venice.ai/api/v1/image/generate", recorder.Urls[0]);
        Assert.Equal("Bearer " + VeniceKey, recorder.Authorizations[0]);
    }

    /// <summary>
    /// Ключа Venice нет — отказ, который читает модель. В нём сказано, что делать дальше:
    /// без этого она повторяет вызов раунд за раундом, и человек платит за каждый.
    /// </summary>
    [Fact]
    public async Task Without_a_venice_key_the_refusal_tells_the_model_what_to_do()
    {
        var recorder = new Recorder((_, _) => Json("{}"));
        var keys = new ApiKeyProvider(OpenRouterKey, LlmProvider.OpenRouter);
        var client = new VeniceClient(new HttpClient(recorder), new AgentOptions { Keys = keys });

        var drawing = await Assert.ThrowsAsync<VeniceApiException>(() => client.GenerateImageAsync("кот"));
        var reading = await Assert.ThrowsAsync<VeniceApiException>(
            () => client.ScrapeUrlAsync("https://example.com"));

        foreach (var message in new[] { drawing.Message, reading.Message })
        {
            Assert.Contains("Venice", message, StringComparison.Ordinal);
            Assert.Contains("Не повторяй", message, StringComparison.Ordinal);
        }

        Assert.Empty(recorder.Urls);
    }

    // ───────────────────────── поиск в сети ─────────────────────────

    /// <summary>
    /// У Venice поиск включается надстройкой, у OpenRouter — плагином. Ссылки он кладёт
    /// отдельным полем, и модель, отвечающая по ним, пересказать их текстом забывает —
    /// а без адресов поиск бесполезен тому, кто его звал.
    /// </summary>
    [Fact]
    public async Task Web_search_goes_through_the_plugin_and_keeps_the_links()
    {
        var (client, _, recorder) = Build((_, _) => Json(
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Нашёл вот что.\"," +
            "\"annotations\":[{\"type\":\"url_citation\",\"url_citation\":" +
            "{\"url\":\"https://example.com/a\"}}]}}]}"));

        var found = await client.SearchWebAsync("погода");

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.Equal("web", body.RootElement.GetProperty("plugins")[0].GetProperty("id").GetString());
        Assert.False(body.RootElement.TryGetProperty("venice_parameters", out _));
        Assert.Contains("Ссылки:", found, StringComparison.Ordinal);
        Assert.Contains("https://example.com/a", found, StringComparison.Ordinal);
    }

    // ───────────────────────── заготовки ─────────────────────────

    private static ChatMessage User(string text) =>
        new() { Role = "user", Content = ChatContent.Text(text) };

    private static string Completion(decimal? usageCost = null, decimal? topLevelCost = null)
    {
        var usage = usageCost is null
            ? ""
            : ",\"usage\":{\"prompt_tokens\":10,\"total_tokens\":20,\"cost\":" + Number(usageCost.Value) + "}";
        var cost = topLevelCost is null
            ? ""
            : ",\"cost\":{\"usd\":" + Number(topLevelCost.Value) + ",\"diem\":0}";

        return "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"да\"}," +
               "\"finish_reason\":\"stop\"}]" + usage + cost + "}";
    }

    private static string Number(decimal value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Models() =>
        """
        {"data":[
          {"id":"anthropic/claude-sonnet-4.5","name":"Anthropic: Claude Sonnet 4.5",
           "context_length":200000,
           "architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},
           "supported_parameters":["tools","tool_choice","reasoning","max_tokens"]},
          {"id":"cohere/command-r","name":"Cohere: Command R","context_length":128000,
           "architecture":{"input_modalities":["text"],"output_modalities":["text"]},
           "supported_parameters":["tools","tool_choice","max_tokens"]},
          {"id":"some/no-tools","name":"Some: No Tools","context_length":8000,
           "architecture":{"input_modalities":["text"],"output_modalities":["text"]},
           "supported_parameters":["max_tokens"]}
        ]}
        """;

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage Sse(string payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };

    /// <summary>Пишет адрес, ключ и тело каждого запроса и отвечает по сценарию.</summary>
    private sealed class Recorder : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _script;

        public Recorder(Func<HttpRequestMessage, string, HttpResponseMessage> script) => _script = script;

        public List<string> Urls { get; } = [];

        public List<string?> Authorizations { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri?.ToString() ?? "");
            Authorizations.Add(request.Headers.Authorization?.ToString());
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            return _script(request, body);
        }
    }
}
