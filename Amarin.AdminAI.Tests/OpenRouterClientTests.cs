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

    // ───────────────────────── пакетная очередь ─────────────────────────

    /// <summary>
    /// Пакетная модель остаётся в списке: она работает, просто иначе. Убрать её значило бы
    /// отнять у человека половинную цену вместе с ошибкой.
    /// </summary>
    [Fact]
    public async Task A_batch_model_stays_in_the_list()
    {
        var (client, _, _) = Build((_, _) => Json(Models()));

        var models = VeniceModelCatalog.FilterAgentic(await client.ListTextModelsAsync());

        Assert.Contains(models, model => model.Id == "openrouter:anthropic/claude-sonnet-4.5:batch");
        Assert.Contains(models, model => model.Id == "openrouter:anthropic/claude-sonnet-4.5");
    }

    /// <summary>
    /// Главное: выбранная в списке пакетная модель доводит ход до ответа. Обычный эндпоинт при
    /// этом не трогается вовсе — именно его четырёхсотка и была видна человеку.
    /// </summary>
    [Fact]
    public async Task A_batch_model_goes_through_the_queue_and_returns_an_answer()
    {
        var polls = 0;
        var (client, _, recorder) = Build((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json(Batch("validating"), HttpStatusCode.Accepted);
            }

            polls++;
            return Json(polls == 1 ? Batch("in_progress") : CompletedBatch("да"));
        });
        Instant(client);

        var answer = await client.CreateChatCompletionAsync(
            "openrouter:anthropic/claude-sonnet-4.5:batch",
            [User("привет")],
            tools: null,
            toolChoice: null,
            new VeniceParameters());

        Assert.Equal("да", Text(answer));
        Assert.DoesNotContain(recorder.Urls, url => url.Contains("chat/completions", StringComparison.Ordinal));
        Assert.Equal("https://openrouter.ai/api/v1/batches", recorder.Urls[0]);
        Assert.All(recorder.Urls.Skip(1), url =>
            Assert.Equal("https://openrouter.ai/api/v1/batches/batch_1", url));
    }

    /// <summary>
    /// Сервер разбирает заявку потоком и отвечает <c>400</c>, если <c>requests</c> встретился
    /// раньше <c>endpoint</c> и <c>model</c>. Порядок полей здесь — контракт, а не оформление,
    /// и перестановка строк в классе сломала бы запрос молча.
    /// </summary>
    [Fact]
    public async Task The_batch_body_names_the_endpoint_before_the_requests()
    {
        var (client, _, recorder) = Build((request, _) => request.Method == HttpMethod.Post
            ? Json(Batch("validating"), HttpStatusCode.Accepted)
            : Json(CompletedBatch("да")));
        Instant(client);

        await client.CreateChatCompletionAsync(
            "openrouter:anthropic/claude-sonnet-4.5:batch",
            [User("привет")],
            tools: null,
            toolChoice: null,
            new VeniceParameters());

        var body = recorder.Bodies[0];
        Assert.True(body.IndexOf("\"endpoint\"", StringComparison.Ordinal) <
                    body.IndexOf("\"requests\"", StringComparison.Ordinal));
        Assert.True(body.IndexOf("\"model\"", StringComparison.Ordinal) <
                    body.IndexOf("\"requests\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Пакетность задаёт эндпоинт, а не имя модели: <c>:batch</c> — пометка каталога, и заявка
    /// с ней уехала бы в отказ. Запрещённого очередью в теле тоже быть не должно.
    /// </summary>
    [Fact]
    public async Task The_batch_body_drops_everything_the_queue_rejects()
    {
        var (client, _, recorder) = Build((request, _) => request.Method == HttpMethod.Post
            ? Json(Batch("validating"), HttpStatusCode.Accepted)
            : Json(CompletedBatch("да")));
        Instant(client);

        await client.CreateChatCompletionAsync(
            "openrouter:anthropic/claude-sonnet-4.5:batch",
            [User("привет")],
            tools: null,
            toolChoice: null,
            new VeniceParameters { EnableWebSearch = "on" });

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        var root = body.RootElement;

        Assert.Equal("/v1/chat/completions", root.GetProperty("endpoint").GetString());
        Assert.Equal("anthropic/claude-sonnet-4.5", root.GetProperty("model").GetString());

        var inner = root.GetProperty("requests")[0].GetProperty("body");
        Assert.Equal("anthropic/claude-sonnet-4.5", inner.GetProperty("model").GetString());
        Assert.False(inner.TryGetProperty("stream", out _));
        Assert.False(inner.TryGetProperty("stream_options", out _));
        Assert.False(inner.TryGetProperty("venice_parameters", out _));
        Assert.False(inner.TryGetProperty("plugins", out _));
        Assert.False(inner.TryGetProperty("usage", out _));
    }

    /// <summary>
    /// Потока у очереди нет, но ход не должен стоять немым: пока заявка считается, на месте
    /// ответа идёт отчёт о её состоянии, а в конце его заменяет сам ответ.
    /// </summary>
    [Fact]
    public async Task While_the_queue_works_the_turn_says_so()
    {
        var polls = 0;
        var (client, _, _) = Build((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json(Batch("validating"), HttpStatusCode.Accepted);
            }

            polls++;
            return Json(polls == 1 ? Batch("in_progress") : CompletedBatch("готово"));
        });
        Instant(client);

        var shown = new List<string>();
        var streamed = await client.StreamChatCompletionAsync(
            "openrouter:anthropic/claude-sonnet-4.5:batch",
            "openrouter:anthropic/claude-sonnet-4.5:batch",
            [User("привет")],
            tools: null,
            toolChoice: null,
            new VeniceParameters(),
            shown.Add);

        Assert.Equal("готово", streamed.Text);
        Assert.Equal("готово", shown[^1]);
        Assert.Contains(shown, line => line.Contains("очередь", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Между «заявка принята» и первым ответом на опрос проходят секунды: очередь её уже
    /// сохранила, но ещё не видит и отвечает 404 «Batch job not found». Это ход дела, а не
    /// отказ, — иначе первый же опрос обрывал ход ошибкой, которую не обойти.
    /// </summary>
    [Fact]
    public async Task A_batch_that_has_not_landed_yet_is_not_a_failure()
    {
        var polls = 0;
        var (client, _, _) = Build((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json(Batch("validating"), HttpStatusCode.Accepted);
            }

            polls++;
            return polls <= 3
                ? Json("{\"error\":{\"message\":\"Batch job not found\"}}", HttpStatusCode.NotFound)
                : Json(CompletedBatch("да"));
        });
        Instant(client);

        var answer = await client.CreateChatCompletionAsync(
            "openrouter:anthropic/claude-sonnet-4.5:batch",
            [User("привет")],
            tools: null,
            toolChoice: null,
            new VeniceParameters());

        Assert.Equal("да", Text(answer));
        Assert.Equal(4, polls);
    }

    /// <summary>
    /// Но ждать вечно тоже нельзя: заявка, не проявившаяся за окно, потеряна, и человек должен
    /// узнать об этом с её номером на руках, а не сидеть перед молчащим чатом.
    /// </summary>
    [Fact]
    public async Task A_batch_that_never_lands_is_reported_with_its_number()
    {
        var (client, _, _) = Build((request, _) => request.Method == HttpMethod.Post
            ? Json(Batch("validating"), HttpStatusCode.Accepted)
            : Json("{\"error\":{\"message\":\"Batch job not found\"}}", HttpStatusCode.NotFound));
        Instant(client);

        var failure = await Assert.ThrowsAsync<VeniceApiException>(() =>
            client.CreateChatCompletionAsync(
                "openrouter:anthropic/claude-sonnet-4.5:batch",
                [User("привет")],
                tools: null,
                toolChoice: null,
                new VeniceParameters()));

        Assert.Contains("batch_1", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Заявка, которая уже отвечала, а потом пропала, — это не «ещё не доехала», а удалённая
    /// заявка: ждать её возвращения нечего.
    /// </summary>
    [Fact]
    public async Task A_batch_that_vanishes_after_answering_is_not_waited_for()
    {
        var polls = 0;
        var (client, _, _) = Build((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json(Batch("validating"), HttpStatusCode.Accepted);
            }

            polls++;
            return polls == 1
                ? Json(Batch("in_progress"))
                : Json("{\"error\":{\"message\":\"Batch job not found\"}}", HttpStatusCode.NotFound);
        });
        Instant(client);

        await Assert.ThrowsAsync<VeniceApiException>(() =>
            client.CreateChatCompletionAsync(
                "openrouter:anthropic/claude-sonnet-4.5:batch",
                [User("привет")],
                tools: null,
                toolChoice: null,
                new VeniceParameters()));

        // Второй опрос — и сразу отказ, без ожидания окна проявления.
        Assert.Equal(2, polls);
    }

    /// <summary>
    /// Вложения уходят в запрос строкой <c>data:</c>, а очередь берёт их только ссылкой и
    /// отвергает заявку целиком — минутами позже и уже за деньги. Отказываем до заявки.
    /// </summary>
    [Fact]
    public async Task An_attachment_is_refused_before_the_batch_is_submitted()
    {
        var (client, _, recorder) = Build((_, _) => Json(CompletedBatch("да")));
        Instant(client);

        var failure = await Assert.ThrowsAsync<VeniceApiException>(() =>
            client.CreateChatCompletionAsync(
                "openrouter:anthropic/claude-sonnet-4.5:batch",
                [WithImage("что на картинке?")],
                tools: null,
                toolChoice: null,
                new VeniceParameters()));

        Assert.Empty(recorder.Urls);
        Assert.Contains("(batch)", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Заявка отказала — человек читает почему, а не «заявка не completed».</summary>
    [Fact]
    public async Task A_failed_batch_is_reported_in_words()
    {
        var (client, _, _) = Build((request, _) => request.Method == HttpMethod.Post
            ? Json(Batch("validating"), HttpStatusCode.Accepted)
            : Json("""
                {"id":"batch_1","status":"failed","request_counts":{"total":1,"completed":0,"failed":1},
                 "results":null,"error":{"message":"model has no :batch endpoint"}}
                """));
        Instant(client);

        var failure = await Assert.ThrowsAsync<VeniceApiException>(() =>
            client.CreateChatCompletionAsync(
                "openrouter:anthropic/claude-sonnet-4.5:batch",
                [User("привет")],
                tools: null,
                toolChoice: null,
                new VeniceParameters()));

        Assert.Contains("model has no :batch endpoint", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Отменённый ход обязан снять и заявку: она уже стоит в очереди провайдера и будет
    /// посчитана, а читать её ответ уже некому.
    /// </summary>
    [Fact]
    public async Task Cancelling_the_turn_cancels_the_batch()
    {
        using var cancellation = new CancellationTokenSource();
        var (client, _, recorder) = Build((request, _) =>
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/batches", StringComparison.Ordinal))
            {
                return Json(Batch("validating"), HttpStatusCode.Accepted);
            }

            // Отмена приходит, пока заявка ещё считается, — как нажатие «стоп» в чате.
            cancellation.Cancel();
            return Json(Batch("in_progress"));
        });
        client.BatchDelay = (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CreateChatCompletionAsync(
                "openrouter:anthropic/claude-sonnet-4.5:batch",
                [User("привет")],
                tools: null,
                toolChoice: null,
                new VeniceParameters(),
                cancellation.Token));

        Assert.Contains(
            recorder.Urls,
            url => url.EndsWith("/batches/batch_1/cancel", StringComparison.Ordinal));
    }

    /// <summary>
    /// Цену очередь называет по всей заявке, а не внутри ответа. Без переноса в ответ деньги
    /// за пакетный ход не попали бы в график трат вовсе.
    /// </summary>
    [Fact]
    public async Task The_price_of_a_batch_reaches_the_ledger()
    {
        var spent = new List<(string Sku, decimal Usd)>();
        var (client, _, _) = Build(
            (request, _) => request.Method == HttpMethod.Post
                ? Json(Batch("validating"), HttpStatusCode.Accepted)
                : Json(CompletedBatch("да", cost: 0.000225m)),
            spendSink: (_, cost, sku) => spent.Add((sku, cost.Usd)));
        Instant(client);

        await client.CreateChatCompletionAsync(
            "openrouter:anthropic/claude-sonnet-4.5:batch",
            [User("привет")],
            tools: null,
            toolChoice: null,
            new VeniceParameters());

        Assert.Equal(
            ("openrouter:anthropic/claude-sonnet-4.5:batch", 0.000225m),
            spent.Single());
    }

    /// <summary>
    /// Служебную работу очередь не обслуживает: заголовок переписки, сводку, маршрутизатор
    /// и поиск в сети ждёт не человек, а сам ход, и сутки ожидания там — это зависший чат.
    /// Такой запрос уходит обычным путём к близнецу без пометки.
    /// </summary>
    [Fact]
    public async Task Service_work_goes_round_the_queue()
    {
        var (client, _, recorder) = Build((_, _) => Json(Completion()));
        Instant(client);

        using (VeniceClient.ChargeAs(VeniceSku.ChatTitle))
        {
            await client.CreateChatCompletionAsync(
                "openrouter:anthropic/claude-sonnet-4.5:batch",
                [User("придумай заголовок")],
                tools: null,
                toolChoice: null,
                new VeniceParameters());
        }

        Assert.DoesNotContain(recorder.Urls, url => url.EndsWith("/batches", StringComparison.Ordinal));
        Assert.EndsWith("/chat/completions", recorder.Urls.Single(), StringComparison.Ordinal);

        using var body = JsonDocument.Parse(recorder.Bodies[0]);
        Assert.Equal("anthropic/claude-sonnet-4.5", body.RootElement.GetProperty("model").GetString());
    }

    /// <summary>Опрос без потолка растянулся бы до часов, частый — это тысячи запросов.</summary>
    [Fact]
    public void The_poll_interval_grows_to_a_ceiling()
    {
        var first = OpenRouterBatchPlan.NextDelay(TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(2), first);
        Assert.True(OpenRouterBatchPlan.NextDelay(first) > first);

        var delay = first;
        for (var step = 0; step < 50; step++)
        {
            delay = OpenRouterBatchPlan.NextDelay(delay);
        }

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    /// <summary>Опрос прекращается только на окончательном состоянии.</summary>
    [Theory]
    [InlineData("completed", true)]
    [InlineData("failed", true)]
    [InlineData("expired", true)]
    [InlineData("cancelled", true)]
    [InlineData("validating", false)]
    [InlineData("in_progress", false)]
    [InlineData("finalizing", false)]
    [InlineData("cancelling", false)]
    public void Only_a_terminal_status_stops_the_polling(string status, bool terminal) =>
        Assert.Equal(terminal, OpenRouterBatchPlan.IsTerminal(status));

    /// <summary>
    /// Отказ называет того, кто отказал. Зашитое «Venice API error» на ходе через OpenRouter
    /// отправляло человека проверять ключ Venice, которого дело не касалось вовсе.
    /// </summary>
    [Fact]
    public async Task A_refusal_names_the_provider_that_refused()
    {
        var (client, _, _) = Build((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                """{"error":{"message":"Specified model not found"}}""",
                Encoding.UTF8,
                "application/json")
        });

        var failure = await Assert.ThrowsAsync<VeniceApiException>(() =>
            client.CreateChatCompletionAsync(
                "openrouter:anthropic/claude-sonnet-4.5",
                [User("привет")],
                tools: null,
                toolChoice: null,
                new VeniceParameters()));

        Assert.StartsWith("OpenRouter API error (404)", failure.Message, StringComparison.Ordinal);
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
          {"id":"anthropic/claude-sonnet-4.5:batch","name":"Anthropic: Claude Sonnet 4.5 (batch)",
           "context_length":200000,
           "architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},
           "supported_parameters":["tools","tool_choice","reasoning","max_tokens"]},
          {"id":"some/no-tools","name":"Some: No Tools","context_length":8000,
           "architecture":{"input_modalities":["text"],"output_modalities":["text"]},
           "supported_parameters":["max_tokens"]}
        ]}
        """;

    /// <summary>Убирает перерывы между опросами: иначе проверка ожидания стоила бы секунды.</summary>
    private static void Instant(VeniceClient client) =>
        client.BatchDelay = (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

    /// <summary>Заявка в работе — без результатов, как их и не отдают до готовности.</summary>
    private static string Batch(string status) =>
        "{\"id\":\"batch_1\",\"object\":\"batch\",\"endpoint\":\"/v1/chat/completions\"," +
        "\"model\":\"anthropic/claude-sonnet-4.5\",\"status\":\"" + status + "\"," +
        "\"request_counts\":{\"total\":1,\"completed\":0,\"failed\":0}," +
        "\"usage\":null,\"results\":null,\"error\":null}";

    /// <remarks>
    /// Собирается сложением, а не интерполяцией необработанной строки: в теле заявки полно
    /// закрывающих фигурных скобок подряд, и компилятор читает их как конец подстановки.
    /// </remarks>
    private static string CompletedBatch(string answer, decimal? cost = null)
    {
        var usage = cost is null
            ? "null"
            : "{\"prompt_tokens\":20,\"completion_tokens\":40,\"total_tokens\":60,\"cost\":" +
              Number(cost.Value) + "}";

        return "{\"id\":\"batch_1\",\"object\":\"batch\",\"endpoint\":\"/v1/chat/completions\"," +
               "\"model\":\"anthropic/claude-sonnet-4.5\",\"status\":\"completed\"," +
               "\"request_counts\":{\"total\":1,\"completed\":1,\"failed\":0}," +
               "\"usage\":" + usage + "," +
               "\"results\":[{\"id\":\"batch_req_1\",\"custom_id\":\"amarin-1\"," +
               "\"response\":{\"status_code\":200,\"body\":{\"choices\":[{\"message\":" +
               "{\"role\":\"assistant\",\"content\":\"" + answer + "\"}," +
               "\"finish_reason\":\"stop\"}]}},\"error\":null}]," +
               "\"error\":null}";
    }

    private static string Text(ChatCompletionResponse response) =>
        response.Choices[0].Message.Content?.GetString() ?? "";

    /// <summary>Сообщение с картинкой — ровно в том виде, в каком его шлёт программа.</summary>
    private static ChatMessage WithImage(string prompt) =>
        new()
        {
            Role = "user",
            Content = JsonSerializer.SerializeToElement(new object[]
            {
                new { type = "text", text = prompt },
                new
                {
                    type = "image_url",
                    image_url = new { url = "data:image/png;base64,iVBORw0KGgo=" }
                }
            })
        };

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
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
