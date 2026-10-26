using System.Net;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Список моделей после отказа загружается снова, а удачный не перезапрашивается.
/// </summary>
/// <remarks>
/// До 1.34.0 отказ ложился в кэш пустым списком: человеку, которому OpenRouter ответил
/// «Access Denied» из-за страны, список не возвращался до перезапуска, сколько бы он ни
/// открывал плашку. Таймаут клиента вылетал исключением мимо окна, и плашка стояла на
/// «Загрузка…» навсегда.
/// </remarks>
public sealed class ModelCatalogRetryTests
{
    private const string OpenRouterKey = "sk-or-v1-key";

    private const string Catalog = """
        {"data":[{"id":"anthropic/claude-haiku-5.5","name":"Anthropic: Claude Haiku 5.5",
          "supported_parameters":["tools","reasoning"],
          "pricing":{"prompt":"0.0000001","completion":"0.0000005"}}]}
        """;

    private const string AccessDenied = """
        <!DOCTYPE html><html><head><title>Access Denied</title><style>body{color:red}</style></head>
        <body><h1>Access Denied</h1><p>You don't have permission to access this server. Reference #18.5f2a</p></body></html>
        """;

    private static (VeniceModelListCache Cache, Script Script) Build(TimeSpan? timeout = null)
    {
        var script = new Script();
        var keys = new ApiKeyProvider(OpenRouterKey, LlmProvider.OpenRouter);
        var options = new AgentOptions { Keys = keys, BaseUrl = "https://api.venice.ai/api/v1" };
        var client = new VeniceClient(new HttpClient(script), options);
        return (new VeniceModelListCache(client, keys, timeout), script);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Html(string body, HttpStatusCode status) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    [Fact]
    public async Task A_refusal_is_not_remembered_and_the_next_request_brings_the_list()
    {
        var (cache, script) = Build();
        script.Next(_ => Html(AccessDenied, HttpStatusCode.Forbidden));
        script.Next(_ => Json(Catalog));

        var refused = await cache.LoadAsync(LlmProvider.OpenRouter);

        Assert.Null(refused.Models);
        Assert.Equal(Loc.Format("S.Models.LoadDenied", "OpenRouter", 403), refused.Error);
        Assert.Null(cache.CachedFor(LlmProvider.OpenRouter));

        var loaded = await cache.LoadAsync(LlmProvider.OpenRouter);

        // Модель каталога и её близнец :nitro — его достраивает загрузка (1.37.0-beta.7).
        Assert.NotNull(loaded.Models);
        Assert.Equal(
            ["openrouter:anthropic/claude-haiku-5.5", "openrouter:anthropic/claude-haiku-5.5:nitro"],
            loaded.Models!.Select(model => model.Id));
        Assert.Null(loaded.Error);
        Assert.Null(cache.ErrorFor(LlmProvider.OpenRouter));
        Assert.Equal(2, script.Calls);
    }

    [Fact]
    public async Task A_loaded_list_is_not_fetched_again()
    {
        var (cache, script) = Build();
        script.Next(_ => Json(Catalog));

        await cache.LoadAsync(LlmProvider.OpenRouter);
        var again = await cache.LoadAsync(LlmProvider.OpenRouter);

        Assert.NotNull(again.Models);
        Assert.Equal(1, script.Calls);
    }

    /// <summary>Срок истёк — это ответ с понятной строкой, а не исключение мимо окна.</summary>
    [Fact]
    public async Task A_timeout_is_an_answer_not_an_exception()
    {
        var (cache, script) = Build(TimeSpan.FromMilliseconds(150));
        script.NextAsync(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Json(Catalog);
        });

        var load = await cache.LoadAsync(LlmProvider.OpenRouter);

        Assert.Null(load.Models);
        Assert.Equal(Loc.Format("S.Models.LoadTimeout", "OpenRouter"), load.Error);
    }

    [Fact]
    public async Task No_network_is_named_as_such()
    {
        var (cache, script) = Build();
        script.Next(_ => throw new HttpRequestException("No such host is known."));

        var load = await cache.LoadAsync(LlmProvider.OpenRouter);

        Assert.Equal(Loc.Format("S.Models.LoadOffline", "OpenRouter"), load.Error);
    }

    /// <summary>Плашка и поле в настройках, открытые разом, ждут один запрос, а не шлют два.</summary>
    [Fact]
    public async Task Simultaneous_requests_share_one_download()
    {
        var (cache, script) = Build();
        var gate = new TaskCompletionSource();
        script.NextAsync(async _ =>
        {
            await gate.Task;
            return Json(Catalog);
        });

        var first = cache.LoadAsync(LlmProvider.OpenRouter);
        var second = cache.LoadAsync(LlmProvider.OpenRouter);
        gate.SetResult();

        Assert.NotNull((await first).Models);
        Assert.NotNull((await second).Models);
        Assert.Equal(1, script.Calls);
    }

    /// <summary>
    /// Закрытая плашка отменяет своё ожидание, но не загрузку: её итог достаётся следующему.
    /// </summary>
    [Fact]
    public async Task Cancelling_the_wait_does_not_cancel_the_download()
    {
        var (cache, script) = Build();
        var gate = new TaskCompletionSource();
        script.NextAsync(async _ =>
        {
            await gate.Task;
            return Json(Catalog);
        });

        using var closed = new CancellationTokenSource();
        var waiting = cache.LoadAsync(LlmProvider.OpenRouter, closed.Token);
        await closed.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        gate.SetResult();
        var load = await cache.LoadAsync(LlmProvider.OpenRouter);

        Assert.NotNull(load.Models);
        Assert.Equal(1, script.Calls);
    }

    /// <summary>Отвечает по очереди заготовленными ответами и считает запросы.</summary>
    private sealed class Script : HttpMessageHandler
    {
        private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _answers = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Next(Func<CancellationToken, HttpResponseMessage> answer) =>
            _answers.Enqueue(token => Task.FromResult(answer(token)));

        public void NextAsync(Func<CancellationToken, Task<HttpResponseMessage>> answer) =>
            _answers.Enqueue(answer);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Func<CancellationToken, Task<HttpResponseMessage>> answer;
            lock (_answers)
            {
                answer = _answers.Dequeue();
            }

            return answer(cancellationToken);
        }
    }
}
