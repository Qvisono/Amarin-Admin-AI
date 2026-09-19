using System.Net;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Свой слот у сводок чата.
/// </summary>
/// <remarks>
/// До версии 1.23.0 сводки молча брали модель быстрого агента: поменяв модель агенту, человек
/// менял её и сводкам, не зная об этом.
/// </remarks>
public sealed class SummarySlotTests
{
    [Fact]
    public void Summaries_take_their_own_slot()
    {
        var settings = AppSettings.CreateDefault();
        settings.AgentFastModelId = "grok-4-6";
        settings.SummaryModelId = "openrouter:deepseek/deepseek-chat";

        Assert.Equal(
            "openrouter:deepseek/deepseek-chat",
            ChatSummary.ResolveModel(settings, "fallback"));
    }

    /// <summary>
    /// Пустой слот достаётся только тому, кто вычистил его руками: при первом чтении старых
    /// настроек туда переносится выбор быстрого агента.
    /// </summary>
    [Fact]
    public void An_empty_slot_still_falls_back_to_the_fast_agent()
    {
        var settings = AppSettings.CreateDefault();
        settings.AgentFastModelId = "grok-4-6";
        settings.SummaryModelId = "";

        Assert.Equal("grok-4-6", ChatSummary.ResolveModel(settings, "fallback"));
    }

    /// <summary>
    /// Перенос старых настроек: сводки обязаны остаться на том же, чем платили вчера, —
    /// и моделью, и ключом.
    /// </summary>
    [Fact]
    public void Upgrading_carries_the_fast_agent_choice_over()
    {
        var settings = AppSettings.CreateDefault();
        settings.AgentFastModelId = "openrouter:openai/gpt-5-mini";
        ModelSlots.WriteKey(settings, ModelSlot.AgentFast, "key-a");
        settings.SummaryModelId = "";

        Assert.True(AppSettingsStore.MigrateSummarySlot(settings));
        Assert.Equal("openrouter:openai/gpt-5-mini", settings.SummaryModelId);
        Assert.Equal("key-a", ModelSlots.ReadKey(settings, ModelSlot.Summary));
    }

    /// <summary>Уже выбранное не перетирается: перенос имеет смысл ровно один раз.</summary>
    [Fact]
    public void A_chosen_summary_model_survives_the_migration()
    {
        var settings = AppSettings.CreateDefault();
        settings.AgentFastModelId = "grok-4-6";
        settings.SummaryModelId = "claude-sonnet-5";

        Assert.False(AppSettingsStore.MigrateSummarySlot(settings));
        Assert.Equal("claude-sonnet-5", settings.SummaryModelId);
    }
}

/// <summary>
/// Настройка поиска в интернете: провайдер и ключ, без выбора модели.
/// </summary>
/// <remarks>
/// Отдельной «ручки поиска» у провайдеров нет — поиск едет надстройкой на обычном запросе,
/// — но человека это не касается: он выбирает, через кого искать и чьим ключом платить, а
/// модель подбирает программа.
/// </remarks>
public sealed class WebSearchTargetTests
{
    private const string ReplyBody =
        """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"cost":0.25}}""";

    private static ApiKeyProvider Keys()
    {
        var keys = new ApiKeyProvider();
        keys.Use(
            new ApiCredential(LlmProvider.Venice, "ven-secret-1"),
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
        return (new VeniceClient(new HttpClient(recorder), options), recorder);
    }

    /// <summary>Заводское состояние — прежнее поведение: искать там же, где идёт разговор.</summary>
    [Fact]
    public void Without_a_provider_nothing_is_pinned()
    {
        var settings = AppSettings.CreateDefault();
        Assert.Null(settings.WebSearchProvider);
        Assert.Equal("", ModelSlots.WebSearch(settings).Binding.ModelId);
    }

    /// <summary>
    /// Выбран провайдер — модель подбирается сама, и человека о ней не спрашивают.
    /// </summary>
    [Fact]
    public void A_chosen_provider_gets_a_model_of_its_own()
    {
        var settings = AppSettings.CreateDefault();
        settings.WebSearchProvider = LlmProvider.OpenRouter;

        var binding = ModelSlots.WebSearch(settings).Binding;

        Assert.False(string.IsNullOrWhiteSpace(binding.ModelId));
        Assert.Equal(LlmProvider.OpenRouter, binding.Provider);
    }

    /// <summary>
    /// У своего провайдера берётся слот «лёгкие задачи»: эту модель человек выбрал сам, и она
    /// уже сверена с живым каталогом.
    /// </summary>
    [Fact]
    public void The_light_slot_is_reused_when_it_fits()
    {
        var settings = AppSettings.CreateDefault();
        settings.LiteModelId = "openrouter:google/gemini-2.5-flash";
        settings.WebSearchProvider = LlmProvider.OpenRouter;

        Assert.Equal(
            "openrouter:google/gemini-2.5-flash",
            ModelSlots.WebSearch(settings).Binding.ModelId);
    }

    /// <summary>Чужой слот не берётся: он уехал бы не к тому провайдеру.</summary>
    [Fact]
    public void A_light_slot_of_another_provider_is_ignored()
    {
        var settings = AppSettings.CreateDefault();
        settings.LiteModelId = "grok-4-6";
        settings.WebSearchProvider = LlmProvider.OpenRouter;

        Assert.Equal(LlmProvider.OpenRouter, ModelSlots.WebSearch(settings).Binding.Provider);
    }

    [Fact]
    public async Task A_pinned_search_ignores_the_model_of_the_turn()
    {
        var keys = Keys();
        var (client, recorder) = Build(new AgentOptions { Keys = keys });

        var settings = AppSettings.CreateDefault();
        settings.WebSearchProvider = LlmProvider.OpenRouter;
        settings.WebSearchKeyId = "o1";

        // Разговор идёт у Venice...
        var turn = new VeniceTurnContext
        {
            RequestedModelId = "grok-4-6",
            ModelId = "grok-4-6",
            Credential = keys.CredentialFor("grok-4-6", null)
        };

        using (VeniceTurnScope.Push(turn))
        {
            await client.SearchWebAsync("погода", ModelSlots.WebSearch(settings));
        }

        // ...а искать человек велел через OpenRouter и его ключом.
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", Assert.Single(recorder.Urls));
        Assert.Equal("Bearer or-secret-1", Assert.Single(recorder.Authorizations));
    }

    /// <summary>Закреплённый поиск платит своим ключом, а не ключом разговора.</summary>
    [Fact]
    public async Task A_pinned_search_pays_with_its_own_key()
    {
        var keys = Keys();
        var booked = new List<string>();
        var (client, _) = Build(new AgentOptions
        {
            Keys = keys,
            SpendSink = (secret, _, _) => booked.Add(secret)
        });

        var settings = AppSettings.CreateDefault();
        settings.WebSearchProvider = LlmProvider.Venice;
        settings.WebSearchKeyId = "v2";

        await client.SearchWebAsync("погода", ModelSlots.WebSearch(settings));

        Assert.Equal("ven-secret-2", Assert.Single(booked));
    }

    /// <summary>Провайдер не выбран — поиск идёт за ходом, как и раньше.</summary>
    [Fact]
    public async Task Without_a_provider_the_search_follows_the_turn()
    {
        var keys = Keys();
        var (client, recorder) = Build(new AgentOptions { Keys = keys });
        var settings = AppSettings.CreateDefault();

        var turn = new VeniceTurnContext
        {
            RequestedModelId = "openrouter:openai/gpt-5",
            ModelId = "openrouter:openai/gpt-5",
            Credential = keys.CredentialFor("openrouter:openai/gpt-5", null)
        };

        using (VeniceTurnScope.Push(turn))
        {
            await client.SearchWebAsync("погода", ModelSlots.WebSearch(settings));
        }

        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", Assert.Single(recorder.Urls));
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

/// <summary>
/// Выбор движка поиска у OpenRouter.
/// </summary>
/// <remarks>
/// Цена движков отличается десятикратно — от $0.001 у Parallel Turbo до $0.012 у Exa Deep, —
/// и это единственная причина, по которой выбор вообще показывается человеку.
/// </remarks>
public sealed class WebSearchEngineTests
{
    private const string ReplyBody =
        """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"cost":0.25}}""";

    private static ApiKeyProvider Keys()
    {
        var keys = new ApiKeyProvider();
        keys.Use(
            new ApiCredential(LlmProvider.Venice, "ven-secret-1"),
            new ApiCredential(LlmProvider.Venice, "ven-secret-1"),
            [
                new KeyHandle("v1", LlmProvider.Venice, "ven-secret-1"),
                new KeyHandle("o1", LlmProvider.OpenRouter, "or-secret-1")
            ]);
        return keys;
    }

    /// <summary>Каждая строка списка — своё сочетание, иначе выбрать её было бы нечем.</summary>
    [Fact]
    public void Every_option_is_distinct()
    {
        var pairs = WebSearchEngines.All
            .Select(option => (option.Engine, option.Mode))
            .ToList();

        Assert.Equal(pairs.Count, pairs.Distinct().Count());
    }

    /// <summary>Незнакомое сочетание — «Авто»: его мог записать выпуск новее этого.</summary>
    [Fact]
    public void An_unknown_engine_falls_back_to_auto()
    {
        var option = WebSearchEngines.Match("something-new", "turbo");
        Assert.Null(option.Engine);
        Assert.Same(WebSearchEngines.All[0], option);
    }

    [Fact]
    public void A_saved_engine_is_found_again()
    {
        var option = WebSearchEngines.Match("parallel", "turbo");
        Assert.Equal("parallel", option.Engine);
        Assert.Equal("turbo", option.Mode);
        Assert.Equal("$0.001", option.Price);
    }

    /// <summary>Выбранный движок уезжает в плагин запроса.</summary>
    [Fact]
    public async Task The_chosen_engine_reaches_the_request()
    {
        var keys = Keys();
        var recorder = new Recorder();
        var client = new VeniceClient(new HttpClient(recorder), new AgentOptions { Keys = keys });

        var settings = AppSettings.CreateDefault();
        settings.WebSearchProvider = LlmProvider.OpenRouter;
        settings.WebSearchEngine = "parallel";
        settings.WebSearchEngineMode = "turbo";

        await client.SearchWebAsync("погода", ModelSlots.WebSearch(settings));

        var body = Assert.Single(recorder.Bodies);
        Assert.Contains("\"engine\":\"parallel\"", body, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"turbo\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Движок действует и без закреплённого провайдера: если ход и так идёт у OpenRouter,
    /// искать дорогим движком вместо выбранного человеком незачем.
    /// </summary>
    [Fact]
    public async Task The_engine_applies_even_when_the_provider_follows_the_turn()
    {
        var keys = Keys();
        var recorder = new Recorder();
        var client = new VeniceClient(new HttpClient(recorder), new AgentOptions { Keys = keys });

        var settings = AppSettings.CreateDefault();
        settings.WebSearchEngine = "perplexity";

        var turn = new VeniceTurnContext
        {
            RequestedModelId = "openrouter:openai/gpt-5",
            ModelId = "openrouter:openai/gpt-5",
            Credential = keys.CredentialFor("openrouter:openai/gpt-5", null)
        };

        using (VeniceTurnScope.Push(turn))
        {
            await client.SearchWebAsync("погода", ModelSlots.WebSearch(settings));
        }

        Assert.Contains("\"engine\":\"perplexity\"", Assert.Single(recorder.Bodies), StringComparison.Ordinal);
    }

    /// <summary>
    /// У Venice движка нет вовсе: лишнее поле он отвергает вместе со всем запросом.
    /// </summary>
    [Fact]
    public async Task Venice_never_sees_an_engine()
    {
        var keys = Keys();
        var recorder = new Recorder();
        var client = new VeniceClient(new HttpClient(recorder), new AgentOptions { Keys = keys });

        var settings = AppSettings.CreateDefault();
        settings.WebSearchProvider = LlmProvider.Venice;
        settings.WebSearchEngine = "parallel";
        settings.WebSearchEngineMode = "turbo";

        await client.SearchWebAsync("погода", ModelSlots.WebSearch(settings));

        var body = Assert.Single(recorder.Bodies);
        Assert.DoesNotContain("\"engine\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"plugins\"", body, StringComparison.Ordinal);
    }

    /// <summary>Ничего не выбрано — полей движка в запросе нет, решает OpenRouter.</summary>
    [Fact]
    public async Task Without_a_choice_no_engine_is_sent()
    {
        var keys = Keys();
        var recorder = new Recorder();
        var client = new VeniceClient(new HttpClient(recorder), new AgentOptions { Keys = keys });

        var settings = AppSettings.CreateDefault();
        settings.WebSearchProvider = LlmProvider.OpenRouter;

        await client.SearchWebAsync("погода", ModelSlots.WebSearch(settings));

        var body = Assert.Single(recorder.Bodies);
        Assert.Contains("\"plugins\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"engine\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"mode\"", body, StringComparison.Ordinal);
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReplyBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
