using System.Net;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Запрос слота уходит на сервер своего провайдера и со своим ключом.
/// </summary>
/// <remarks>
/// До версии 1.23.0 форма тела запроса выбиралась по идентификатору модели, а адрес и
/// <c>Authorization</c> — по одному активному ключу на всю программу. Слот с моделью OpenRouter
/// при активном ключе Venice слал тело OpenRouter на <c>api.venice.ai</c> с ключом Venice.
/// Глазами этого не видно: в ответе четырёхсотка, которая читается как сетевой сбой.
/// </remarks>
public sealed class SlotCredentialRoutingTests
{
    private const string ReplyBody =
        """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"cost":0.25}}""";

    private static ApiKeyProvider Keys() =>
        BuildKeys(new ApiCredential(LlmProvider.Venice, "ven-secret-1"));

    private static ApiKeyProvider BuildKeys(ApiCredential selected)
    {
        var keys = new ApiKeyProvider();
        keys.Use(
            selected,
            new ApiCredential(LlmProvider.Venice, "ven-secret-1"),
            [
                new KeyHandle("v1", LlmProvider.Venice, "ven-secret-1"),
                new KeyHandle("v2", LlmProvider.Venice, "ven-secret-2"),
                new KeyHandle("o1", LlmProvider.OpenRouter, "or-secret-1")
            ]);
        return keys;
    }

    private static (VeniceClient Client, Recorder Recorder) Build(AgentOptions options)
    {
        var recorder = new Recorder();
        var http = new HttpClient(recorder);
        return (new VeniceClient(http, options), recorder);
    }

    [Fact]
    public async Task An_openrouter_slot_pays_openrouter_while_venice_is_selected()
    {
        var keys = Keys();
        var (client, recorder) = Build(new AgentOptions { Keys = keys });

        await client.CreateChatCompletionAsync(
            "openrouter:openai/gpt-5",
            [new ChatMessage { Role = "user", Content = ChatContent.Text("hi") }],
            tools: null,
            toolChoice: null,
            new VeniceParameters(),
            CancellationToken.None,
            reasoning: null,
            credential: keys.CredentialFor("openrouter:openai/gpt-5", null));

        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", Assert.Single(recorder.Urls));
        Assert.Equal("Bearer or-secret-1", Assert.Single(recorder.Authorizations));
    }

    /// <summary>Назначенный слоту ключ, а не первый попавшийся у того же провайдера.</summary>
    [Fact]
    public async Task A_slot_uses_the_key_it_was_given()
    {
        var keys = Keys();
        var (client, recorder) = Build(new AgentOptions { Keys = keys });

        await client.CreateChatCompletionAsync(
            "grok-4-6",
            [new ChatMessage { Role = "user", Content = ChatContent.Text("hi") }],
            tools: null,
            toolChoice: null,
            new VeniceParameters(),
            CancellationToken.None,
            reasoning: null,
            credential: keys.CredentialFor("grok-4-6", "v2"));

        Assert.Equal("Bearer ven-secret-2", Assert.Single(recorder.Authorizations));
    }

    /// <summary>
    /// Списание пишется на тот ключ, которым заплатили. Прежде оно уходило на выбранный ключ,
    /// и деньги заголовков чатов, оплаченных вторым ключом, попадали в чужой график.
    /// </summary>
    [Fact]
    public async Task Spending_is_booked_to_the_paying_key()
    {
        var keys = Keys();
        var booked = new List<string>();
        var (client, _) = Build(new AgentOptions
        {
            Keys = keys,
            SpendSink = (secret, _, _) => booked.Add(secret)
        });

        await client.CreateChatCompletionAsync(
            "openrouter:openai/gpt-5",
            [new ChatMessage { Role = "user", Content = ChatContent.Text("hi") }],
            tools: null,
            toolChoice: null,
            new VeniceParameters(),
            CancellationToken.None,
            reasoning: null,
            credential: keys.CredentialFor("openrouter:openai/gpt-5", null));

        Assert.Equal("or-secret-1", Assert.Single(booked));
    }

    /// <summary>
    /// У провайдера нет ключа — отказ понятной строкой, а не четырёхсотка чужого сервера.
    /// Эту строку читает модель, поэтому в ней сказано, что делать дальше.
    /// </summary>
    [Fact]
    public async Task A_provider_without_a_key_refuses_in_words()
    {
        var keys = new ApiKeyProvider();
        keys.Use(
            new ApiCredential(LlmProvider.Venice, "ven-secret-1"),
            new ApiCredential(LlmProvider.Venice, "ven-secret-1"),
            [new KeyHandle("v1", LlmProvider.Venice, "ven-secret-1")]);

        var (client, recorder) = Build(new AgentOptions { Keys = keys });

        var failure = await Assert.ThrowsAsync<VeniceApiException>(() =>
            client.CreateChatCompletionAsync(
                "openrouter:openai/gpt-5",
                [new ChatMessage { Role = "user", Content = ChatContent.Text("hi") }],
                tools: null,
                toolChoice: null,
                new VeniceParameters(),
                CancellationToken.None,
                reasoning: null,
                credential: keys.CredentialFor("openrouter:openai/gpt-5", null)));

        Assert.Contains("OpenRouter", failure.Message, StringComparison.Ordinal);
        Assert.Empty(recorder.Urls);
    }

    /// <summary>
    /// Копия настроек под слот несёт его ключ: у агента, SynGuard, заголовков и сводок клиент
    /// свой, и внутренние вызовы про ключи ничего не знают.
    /// </summary>
    [Fact]
    public async Task A_bound_options_copy_carries_its_key()
    {
        var keys = Keys();
        var (client, recorder) = Build(new AgentOptions
        {
            Keys = keys,
            Binding = keys.CredentialFor("openrouter:openai/gpt-5", null)
        });

        await client.CreateChatCompletionAsync(
            "openrouter:openai/gpt-5",
            [new ChatMessage { Role = "user", Content = ChatContent.Text("hi") }],
            tools: null,
            toolChoice: null,
            new VeniceParameters(),
            CancellationToken.None);

        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", Assert.Single(recorder.Urls));
        Assert.Equal("Bearer or-secret-1", Assert.Single(recorder.Authorizations));
    }

    /// <summary>
    /// Остаток снимается только с выбранного ключа: заголовок чата, оплаченный вторым ключом,
    /// поставил бы человеку на плашку в композере чужую цифру.
    /// </summary>
    [Fact]
    public async Task A_foreign_key_does_not_touch_the_balance()
    {
        var keys = Keys();
        var (client, _) = Build(new AgentOptions { Keys = keys });

        await client.CreateChatCompletionAsync(
            "grok-4-6",
            [new ChatMessage { Role = "user", Content = ChatContent.Text("hi") }],
            tools: null,
            toolChoice: null,
            new VeniceParameters(),
            CancellationToken.None,
            reasoning: null,
            credential: keys.CredentialFor("grok-4-6", "v2"));

        Assert.Null(client.LastBalance);
    }

    /// <summary>
    /// Поиск в сети идёт к тому же провайдеру, что и модель хода, и его же ключом.
    /// </summary>
    /// <remarks>
    /// Поиск оплачивается токенами модели, которая его ведёт: у Venice это
    /// <c>venice_parameters</c>, у OpenRouter — плагин, и уйти они обязаны каждый на свой
    /// сервер.
    /// </remarks>
    [Fact]
    public async Task Web_search_follows_the_model_of_the_turn()
    {
        var keys = Keys();
        var (client, recorder) = Build(new AgentOptions { Keys = keys });

        var turn = new VeniceTurnContext
        {
            RequestedModelId = "openrouter:openai/gpt-5",
            ModelId = "openrouter:openai/gpt-5",
            Credential = keys.CredentialFor("openrouter:openai/gpt-5", null)
        };

        using (VeniceTurnScope.Push(turn))
        {
            await client.SearchWebAsync("погода");
        }

        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", Assert.Single(recorder.Urls));
        Assert.Equal("Bearer or-secret-1", Assert.Single(recorder.Authorizations));
    }

    /// <summary>
    /// Внутри прогона агента ход подавлен, и ключ виден только на копии настроек — поиск
    /// обязан взять его, а не ключ провайдера по умолчанию.
    /// </summary>
    /// <remarks>
    /// Прежде здесь брался <c>DefaultFor</c>, и деньги за поиск внутри агента уходили
    /// в журнал чужого ключа: слот агента платил одним, поиск внутри него — другим.
    /// </remarks>
    [Fact]
    public async Task Web_search_inside_an_agent_pays_with_the_agent_key()
    {
        var keys = Keys();
        var booked = new List<string>();
        var (client, recorder) = Build(new AgentOptions
        {
            Keys = keys,
            Model = "grok-4-6",
            Binding = keys.CredentialFor("grok-4-6", "v2"),
            SpendSink = (secret, _, _) => booked.Add(secret)
        });

        // Ровно то, что делает AgentHost вокруг прогона.
        using (VeniceTurnScope.Suppress())
        {
            await client.SearchWebAsync("погода");
        }

        Assert.Equal("Bearer ven-secret-2", Assert.Single(recorder.Authorizations));
        Assert.Equal("ven-secret-2", Assert.Single(booked));
    }

    /// <summary>
    /// Замена перегруженной модели не трогает модель программы, если у запроса свой ключ.
    /// </summary>
    /// <remarks>
    /// Подмена в общих настройках была бы подменой чужого слота: у него своя модель и свой
    /// провайдер, а <c>_options.Model</c> — «модель по умолчанию» на всю программу. Заодно
    /// проверяется, что вся цепочка осталась у того же провайдера: уйти к соседу она не может,
    /// его ключ этому запросу не предъявляли.
    /// </remarks>
    [Fact]
    public async Task A_fallback_leaves_the_program_wide_model_alone()
    {
        var keys = Keys();
        var options = new AgentOptions { Keys = keys, Model = "grok-4-6" };
        var overloaded = new Overloaded();
        var client = new VeniceClient(new HttpClient(overloaded), options);

        await client.CreateChatCompletionAsync(
            "grok-4-6",
            [new ChatMessage { Role = "user", Content = ChatContent.Text("hi") }],
            tools: null,
            toolChoice: null,
            new VeniceParameters(),
            CancellationToken.None,
            reasoning: null,
            credential: keys.CredentialFor("grok-4-6", "v2"));

        Assert.Equal("grok-4-6", options.Model);
        Assert.True(overloaded.Models.Count > 1, "замена модели должна была сработать");
        Assert.All(overloaded.Models, model => Assert.Equal(LlmProvider.Venice, ModelRef.Of(model)));
        Assert.All(
            overloaded.Authorizations,
            header => Assert.Equal("Bearer ven-secret-2", header));
    }

    /// <summary>Первая модель отвечает 503, остальные — обычным ответом.</summary>
    private sealed class Overloaded : HttpMessageHandler
    {
        public List<string> Models { get; } = [];

        public List<string?> Authorizations { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var document = System.Text.Json.JsonDocument.Parse(body);
            Models.Add(document.RootElement.GetProperty("model").GetString() ?? "");

            if (Models.Count == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        """{"error":{"message":"model is overloaded"}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReplyBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = [];

        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            Urls.Add(request.RequestUri?.ToString() ?? "");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReplyBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
