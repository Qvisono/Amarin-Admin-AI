using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Побочные запросы — маршрутизатор, заголовок, сводка, сжатие, поиск по чатам, защитник, выбор
/// уровня агента и отчёт о работе — знают свою цену и у моделей OpenRouter.
/// </summary>
/// <remarks>
/// Venice называет цену ответа полем <c>cost</c>, OpenRouter — внутри <c>usage</c>. Клиент
/// списывал в журнал трат обе, а побочные запросы читали только первое: у слотов на OpenRouter
/// их строки пропадали из разбивки под сообщением, хотя деньги уходили на график. Маршрутизатор
/// на OpenRouter вдобавок оставлял свои деньги в строке модели.
/// </remarks>
public sealed class SideRequestCostTests
{
    private const string OpenRouterModel = "openrouter:deepseek/deepseek-v4-flash-0731";

    [Fact]
    public async Task The_chat_title_knows_its_price()
    {
        var keys = Keys();
        var titles = new ChatTitleGenerator(
            Http(_ => OpenRouterReply("Неверная математика")),
            Options(keys),
            () => new AppSettings { TitleModelId = OpenRouterModel });

        var draft = await titles.GenerateAsync("сколько будет 3+4");

        AssertPrice(draft.Cost);
    }

    [Fact]
    public async Task The_chat_summary_its_compaction_and_the_search_know_their_price()
    {
        var keys = Keys();
        var generator = new ChatSummaryGenerator(
            Http(_ => OpenRouterReply("1")),
            Options(keys),
            () => new AppSettings { SummaryModelId = OpenRouterModel });

        var summary = await generator.UpdateAsync(null, "User: сколько будет 3+4\nAssistant: 7");
        var compact = await generator.CompactAsync("User: сколько будет 3+4\nAssistant: 7");
        var search = await generator.SearchAsync("математика", [("c1", "Сводка про арифметику")]);

        AssertPrice(summary.Cost);
        AssertPrice(compact.Cost);
        AssertPrice(search.Cost);
    }

    [Fact]
    public async Task The_work_report_knows_its_price()
    {
        var keys = Keys();
        var writer = new WorkReportWriter(
            Http(_ => OpenRouterReply("## Что сделано\nНичего")),
            Options(keys),
            () => new AppSettings { SummaryModelId = OpenRouterModel });

        var draft = await writer.WriteAsync("проверь диск", [], CancellationToken.None);

        AssertPrice(draft.Cost);
    }

    [Fact]
    public async Task The_guard_knows_its_price()
    {
        var keys = Keys();
        var client = new VeniceClient(
            Http(_ => OpenRouterReply("1: safe")),
            Options(keys) with { Binding = keys.CredentialFor(OpenRouterModel, null) });
        var guard = new SynGuardChecker(client, OpenRouterModel, new ReasoningChoice(true, null));

        var report = await guard.CheckAsync(
            new SynGuardRequest("почисти временные файлы", [new SynGuardCall("write_file", "{}")]),
            CancellationToken.None);

        AssertPrice(report.Cost);
    }

    [Fact]
    public async Task The_agent_tier_choice_knows_its_price()
    {
        var keys = Keys();
        var client = new VeniceClient(
            Http(_ => OpenRouterReply("lite")),
            Options(keys) with { Binding = keys.CredentialFor(OpenRouterModel, null) });

        var decision = await AgentTierRouter.DecideAsync(
            client, OpenRouterModel, "", "проверь диск", null, new ReasoningChoice(true, null), CancellationToken.None);

        AssertPrice(decision.Cost);
    }

    /// <summary>
    /// Маршрутизатор чата платит тем же клиентом, что и разговор: его деньги уже в счёте хода.
    /// Без своей цены строка «Модель» забирала их себе, а строки маршрутизатора не было.
    /// </summary>
    [Fact]
    public async Task The_chat_router_stands_on_its_own_line_and_leaves_the_model_line()
    {
        var keys = Keys();
        var handler = new ScriptedHandler(body => body.Contains("\"stream\":true", StringComparison.Ordinal)
            ? VeniceStream("7", 0.02m)
            : OpenRouterReply("lite"));
        var options = Options(keys);
        var settings = new AppSettings
        {
            ChatModelId = "auto",
            LiteModelId = "qwen-3-7-plus",
            HeavyModelId = "claude-sonnet-5",
            RouterModelId = OpenRouterModel
        };
        var engine = new ChatEngine(new VeniceClient(new HttpClient(handler), options), options, () => settings, new ToolRegistry([]));
        var session = new ChatSession { Id = "s1", SelectedModelId = "auto" };

        await engine.RunTurnAsync(session, "сколько будет 3+4", new SilentObserver(), CancellationToken.None);

        var answer = session.Messages.Last(message => message.Role == "assistant");
        AssertPrice(answer.RouterCost);
        Assert.Equal(0.02m, answer.ModelCost!.Usd);
        Assert.Equal(0.27m, answer.Cost!.Usd);
    }

    /// <summary>
    /// Одно правило цены ответа на всех: им же клиент списывает деньги в журнал трат.
    /// </summary>
    [Theory]
    [InlineData("""{"choices":[],"cost":{"usd":0.5,"diem":0}}""", "0.5")]
    [InlineData("""{"choices":[],"usage":{"cost":0.25}}""", "0.25")]
    [InlineData("""{"choices":[],"cost":{"usd":0.5,"diem":0},"usage":{"cost":0.25}}""", "0.5")]
    [InlineData("""{"choices":[],"usage":{"cost":0}}""", null)]
    [InlineData("""{"choices":[]}""", null)]
    public void A_reply_names_its_price_whichever_way_the_provider_reports_it(string json, string? usd)
    {
        var reply = JsonSerializer.Deserialize(json, VeniceJsonContext.Default.ChatCompletionResponse)!;

        var cost = reply.ReportedCost();

        if (usd is null)
        {
            Assert.Null(cost);
        }
        else
        {
            Assert.NotNull(cost);
            Assert.True(cost.HasData);
            Assert.Equal(decimal.Parse(usd, System.Globalization.CultureInfo.InvariantCulture), cost.Usd);
        }
    }

    private static void AssertPrice(VeniceCost? cost)
    {
        Assert.NotNull(cost);
        Assert.True(cost.HasData, "цена ответа OpenRouter потерялась");
        Assert.Equal(0.25m, cost.Usd);
    }

    private static ApiKeyProvider Keys()
    {
        var keys = new ApiKeyProvider();
        keys.Use(
            new ApiCredential(LlmProvider.Venice, "ven-secret"),
            new ApiCredential(LlmProvider.Venice, "ven-secret"),
            [
                new KeyHandle("v1", LlmProvider.Venice, "ven-secret"),
                new KeyHandle("o1", LlmProvider.OpenRouter, "or-secret")
            ]);
        return keys;
    }

    private static AgentOptions Options(ApiKeyProvider keys) => new()
    {
        ApiKey = "ven-secret",
        BaseUrl = "https://api.venice.ai/api/v1",
        Model = "grok-4-6",
        MaxToolRounds = 2,
        Keys = keys
    };

    private static HttpClient Http(Func<string, HttpResponseMessage> script) => new(new ScriptedHandler(script));

    private static HttpResponseMessage OpenRouterReply(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(content) +
                "}}],\"usage\":{\"prompt_tokens\":40,\"completion_tokens\":2,\"cost\":0.25}}",
                Encoding.UTF8,
                "application/json")
        };

    private static HttpResponseMessage VeniceStream(string text, decimal usd)
    {
        var payload =
            "data: {\"choices\":[{\"delta\":{\"content\":" + JsonSerializer.Serialize(text) + "}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"cost\":{\"usd\":" +
            usd.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"diem\":0}}\n" +
            "data: [DONE]\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }

    private sealed class ScriptedHandler(Func<string, HttpResponseMessage> script) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return script(body);
        }
    }

    private sealed class SilentObserver : IChatTurnObserver
    {
        public void OnUserAppended(ChatDisplayMessage user)
        {
        }

        public void OnAssistantStarted(ChatDisplayMessage assistant)
        {
        }

        public void OnAssistantText(ChatDisplayMessage assistant)
        {
        }

        public void OnToolsChanged(ChatDisplayMessage assistant)
        {
        }

        public void OnAssistantCompleted(ChatDisplayMessage assistant)
        {
        }

        public void OnAssistantCancelled(ChatDisplayMessage assistant)
        {
        }

        public void OnAssistantContinued(ChatDisplayMessage assistant)
        {
        }

        public void OnError(string message)
        {
        }

        public bool TryTakeQueuedMessage(out string text)
        {
            text = "";
            return false;
        }
    }
}
