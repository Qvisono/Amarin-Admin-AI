using System.Net;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Цены и возможности прямых провайдеров из открытого каталога OpenRouter.
/// </summary>
/// <remarks>
/// OpenAI в списке моделей отдаёт голые идентификаторы, Anthropic — без цен. Без книги траты
/// таких ответов были бы неизвестны, а у OpenAI ещё и не было бы ни картинок, ни уровней размышления.
/// </remarks>
public sealed class ModelPriceBookTests : IDisposable
{
    private const string Catalog = """
        {"data":[
          {"id":"~openai/gpt-latest","pricing":{"prompt":"0.000009","completion":"0.000009"}},
          {"id":"openai/gpt-6-sol:free","pricing":{"prompt":"0","completion":"0"}},
          {"id":"openai/gpt-6-sol","name":"OpenAI: GPT-6 Sol","context_length":400000,
           "architecture":{"input_modalities":["text","image"]},
           "supported_parameters":["tools","reasoning"],
           "reasoning":{"supported_efforts":["xhigh","low","high","medium"],"default_effort":"medium"},
           "pricing":{"prompt":"0.00000125","completion":"0.00001"}},
          {"id":"anthropic/claude-haiku-5.5","name":"Anthropic: Claude Haiku 5.5",
           "supported_parameters":["tools","reasoning"],
           "pricing":{"prompt":"0.000001","completion":"0.000005"}},
          {"id":"deepseek/deepseek-v4-flash-0731","supported_parameters":["tools","reasoning"],
           "reasoning":{"supported_efforts":["max","low","high"],"default_effort":"high"},
           "pricing":{"prompt":"0.0000001","completion":"0.0000004"}}
        ]}
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-prices-" + Guid.NewGuid().ToString("N"));

    public ModelPriceBookTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ModelPriceBook Loaded()
    {
        var book = new ModelPriceBook(new HttpClient(new Router()), cachePath: null);
        book.Load(Catalog);
        return book;
    }

    private static VeniceModelInfo Bare(string id) => new()
    {
        Id = id,
        ModelSpec = new VeniceModelSpec
        {
            Capabilities = new VeniceModelCapabilities { SupportsFunctionCalling = true, SupportsReasoningEffort = true }
        }
    };

    [Theory]
    [InlineData("claude-haiku-5-5", "anthropic/claude-haiku-5.5")]
    [InlineData("claude-sonnet-5-5-20260815", "claude-sonnet-5.5")]
    [InlineData("gpt-6-sol-2026-08-01", "GPT-6-Sol")]
    public void Spellings_of_one_model_meet(string direct, string catalog) =>
        Assert.Equal(ModelFamily.Normalize(catalog), ModelFamily.Normalize(direct));

    [Fact]
    public void A_bare_openai_model_gets_price_vision_context_and_levels()
    {
        var model = Loaded().Enrich(LlmProvider.OpenAI, [Bare("openai:gpt-6-sol")]).Single();

        Assert.Equal("openai:gpt-6-sol", model.Id);
        Assert.Equal(1.25m, model.ModelSpec?.Pricing?.Input?.Usd);
        Assert.Equal(10m, model.ModelSpec?.Pricing?.Output?.Usd);
        Assert.Equal(400_000, model.ContextLength);
        Assert.True(model.ModelSpec?.Capabilities?.SupportsVision);
        Assert.Equal(["low", "medium", "high", "xhigh"], model.ModelSpec?.Capabilities?.ReasoningEffortOptions);
        Assert.Equal("medium", model.ModelSpec?.Capabilities?.DefaultReasoningEffort);
    }

    /// <summary>Что провайдер назвал сам, книга не перебивает: она только дополняет.</summary>
    [Fact]
    public void What_the_provider_said_itself_wins()
    {
        var own = new VeniceModelInfo
        {
            Id = "anthropic:claude-haiku-5-5",
            ContextLength = 200_000,
            ModelSpec = new VeniceModelSpec
            {
                Name = "Claude Haiku 5.5",
                Capabilities = new VeniceModelCapabilities
                {
                    SupportsFunctionCalling = true,
                    SupportsReasoningEffort = true,
                    ReasoningEffortOptions = ["low", "medium", "high"],
                    ThinkingModes = ["adaptive", "disabled"]
                }
            }
        };

        var model = Loaded().Enrich(LlmProvider.Anthropic, [own]).Single();

        Assert.Equal(200_000, model.ContextLength);
        Assert.Equal("Claude Haiku 5.5", model.ModelSpec?.Name);
        Assert.Equal(["low", "medium", "high"], model.ModelSpec?.Capabilities?.ReasoningEffortOptions);
        Assert.Equal(["adaptive", "disabled"], model.ModelSpec?.Capabilities?.ThinkingModes);
        Assert.Equal(5m, model.ModelSpec?.Pricing?.Output?.Usd);
    }

    /// <summary>
    /// Бесплатный вариант и псевдоним стоят в каталоге рядом с моделью, но цена у них не её:
    /// с ценой «:free» платный ответ записался бы бесплатным.
    /// </summary>
    [Fact]
    public void Variants_and_aliases_do_not_lend_their_price()
    {
        var model = Loaded().Enrich(LlmProvider.OpenAI, [Bare("openai:gpt-6-sol"), Bare("openai:gpt-latest")]);

        Assert.Equal(1.25m, model[0].ModelSpec?.Pricing?.Input?.Usd);
        Assert.Null(model[1].ModelSpec?.Pricing);
    }

    /// <summary>У Venice, OpenRouter, Groq и своих серверов книга ничего не меняет.</summary>
    [Fact]
    public void Providers_outside_the_book_are_left_alone()
    {
        var book = Loaded();
        var models = new[] { Bare("deepseek-v4-flash-0731") };

        Assert.False(ModelPriceBook.Covers(LlmProvider.Venice));
        Assert.False(ModelPriceBook.Covers(LlmProvider.Groq));
        Assert.False(ModelPriceBook.Covers(LlmProvider.Custom("a1b2c3")));
        Assert.Same(models, book.Enrich(LlmProvider.Venice, models));
    }

    /// <summary>Каталог провайдера доезжает уже с ценами: по ним считаются траты ответов.</summary>
    [Fact]
    public async Task A_direct_catalog_arrives_priced()
    {
        var router = new Router();
        var cache = Cache(router, LlmProvider.OpenAI, "sk-proj-0123456789", Path.Combine(_root, "prices.json"));

        var load = await cache.LoadAsync(LlmProvider.OpenAI);

        var model = Assert.Single(load.Models!);
        Assert.Equal(10m, model.ModelSpec?.Pricing?.Output?.Usd);
        Assert.Equal(1, router.CatalogCalls);
        Assert.True(File.Exists(Path.Combine(_root, "prices.json")));
    }

    /// <summary>У человека с одним ключом Venice программа в OpenRouter не ходит.</summary>
    [Fact]
    public async Task Venice_alone_never_asks_openrouter()
    {
        var router = new Router();
        var cache = Cache(router, LlmProvider.Venice, "vk-venice-0123456789", Path.Combine(_root, "prices.json"));

        await cache.LoadAsync(LlmProvider.Venice);

        Assert.Equal(0, router.CatalogCalls);
    }

    /// <summary>Свежий файл с прошлого запуска заменяет запрос.</summary>
    [Fact]
    public async Task A_fresh_file_saves_the_request()
    {
        var path = Path.Combine(_root, "prices.json");
        await File.WriteAllTextAsync(path, Catalog);
        var router = new Router();
        var book = new ModelPriceBook(new HttpClient(router), path);

        await book.EnsureLoadedAsync();
        var model = book.Enrich(LlmProvider.DeepSeek, [Bare("deepseek:deepseek-v4-flash-0731")]).Single();

        Assert.Equal(0, router.CatalogCalls);
        Assert.Equal(["low", "high", "max"], model.ModelSpec?.Capabilities?.ReasoningEffortOptions);
    }

    /// <summary>OpenRouter недоступен — список всё равно приходит, просто без цен.</summary>
    [Fact]
    public async Task No_book_still_gives_the_list()
    {
        var router = new Router { CatalogDown = true };
        var cache = Cache(router, LlmProvider.OpenAI, "sk-proj-0123456789", Path.Combine(_root, "prices.json"));

        var load = await cache.LoadAsync(LlmProvider.OpenAI);

        var model = Assert.Single(load.Models!);
        Assert.Null(model.ModelSpec?.Pricing);
        Assert.Null(load.Error);
    }

    private static VeniceModelListCache Cache(Router router, LlmProvider provider, string secret, string pricesPath)
    {
        var http = new HttpClient(router);
        var keys = new ApiKeyProvider(secret, provider);
        var client = new VeniceClient(http, new AgentOptions { Keys = keys, BaseUrl = "https://api.venice.ai/api/v1" });
        return new VeniceModelListCache(client, keys, prices: new ModelPriceBook(http, pricesPath));
    }

    /// <summary>Отвечает каталогом OpenRouter, списком OpenAI и списком Venice — по адресу запроса.</summary>
    private sealed class Router : HttpMessageHandler
    {
        private int _catalogCalls;

        public bool CatalogDown { get; init; }

        public int CatalogCalls => Volatile.Read(ref _catalogCalls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? "";
            if (uri.StartsWith("https://openrouter.ai/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _catalogCalls);
                Assert.Null(request.Headers.Authorization);
                return Task.FromResult(CatalogDown
                    ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<title>Access Denied</title>") }
                    : Json(Catalog));
            }

            if (uri.StartsWith("https://api.openai.com/", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""{"object":"list","data":[{"id":"gpt-6-sol","object":"model","owned_by":"openai"}]}"""));
            }

            return Task.FromResult(Json("""
                {"data":[{"id":"deepseek-v4-flash-0731","type":"text","model_spec":{"capabilities":{"supportsFunctionCalling":true}}}]}
                """));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
