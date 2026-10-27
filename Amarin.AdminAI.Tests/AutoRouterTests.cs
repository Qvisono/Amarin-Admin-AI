using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// «Авто» уводила список из четырёх примеров арифметики на флагман: прежний промпт описывал
/// heavy формой сообщения («many steps»), и четыре пункта под это правило честно подходили.
/// </summary>
/// <remarks>
/// Ни один тест не может проверить, что живая модель отнесёт то сообщение к lite. Сторожат три
/// вещи: правила, вызывавшего ошибку, в промпте больше нет; названия моделей действительно
/// уходят в запрос; в запрос не попадает ничего, кроме написанного человеком. Не превращать это
/// в обращения к настоящему API.
/// </remarks>
public sealed class AutoRouterTests
{
    [Fact]
    public void Router_prompt_does_not_call_a_pile_of_small_tasks_heavy()
    {
        Assert.DoesNotContain("many steps", ChatEngine.RouterSystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("A list of easy questions is still easy", ChatEngine.RouterSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("five easy sums", ChatEngine.RouterSystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Router_prompt_does_not_charge_the_chat_for_work_an_agent_does()
    {
        // Прежнее «classify the work itself» уводило просьбу поставить драйвер и проверить
        // Wi-Fi на тяжёлую модель чата: работу делает агент со своим маршрутизатором, а чат
        // только ставит задачу и пересказывает отчёт.
        Assert.DoesNotContain("classify the work itself", ChatEngine.RouterSystemPrompt, StringComparison.OrdinalIgnoreCase);
        var flat = string.Join(' ', ChatEngine.RouterSystemPrompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("work that goes to an agent does not make the chat reply heavy", flat, StringComparison.Ordinal);
        Assert.Contains("Judge what the reply itself has to know, reason out and get exactly right", flat, StringComparison.Ordinal);
    }

    [Fact]
    public void Router_prompt_sends_the_safety_of_the_user_to_the_heavy_chat_model()
    {
        // С 1.32.0: подозрение на вирус, взлом, утечку, обман — ответ чата оценивает опасность и
        // руководит агентами, поэтому heavy, даже когда саму проверку делает агент.
        var flat = string.Join(' ', ChatEngine.RouterSystemPrompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("Safety is the one exception to all of the above", flat, StringComparison.Ordinal);
        Assert.Contains("reply heavy, even when the work itself goes to an agent", flat, StringComparison.Ordinal);
        Assert.Contains("reply heavy if the message may be about the user's safety", flat, StringComparison.Ordinal);

        // lite больше не обещает любую работу на ПК — только ту, что не про безопасность.
        Assert.Contains("any job on this computer that is not about safety", flat, StringComparison.Ordinal);
        Assert.Contains("heavy = anything about the safety of the user or this computer", flat, StringComparison.Ordinal);
    }

    [Fact]
    public void Router_prompt_keeps_the_marker_line_its_tests_match_on()
    {
        // ParallelTurnTests отличает запрос маршрутизатора от запроса чата по этой подстроке в
        // теле HTTP. В самом промпте об этом не написано, так что следующая правка текста
        // сломала бы тот тест случайно.
        Assert.StartsWith("Classify the user request", ChatEngine.RouterSystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Router_sees_the_models_it_chooses_between()
    {
        var handler = Router("lite");
        var (engine, session) = Engine(handler, new AppSettings
        {
            ChatModelId = "auto",
            LiteModelId = "qwen-3-7-plus",
            HeavyModelId = "claude-sonnet-5",
            RouterModelId = "qwen-3-7-plus"
        });

        await engine.RunTurnAsync(session, "сколько будет 2+2", new SilentObserver(), CancellationToken.None);

        var routed = RouterBody(handler);
        Assert.Contains("Qwen 3.7 Plus", routed, StringComparison.Ordinal);
        Assert.Contains("Claude Sonnet 5", routed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Router_reads_the_previous_user_line_and_never_the_assistant_text()
    {
        var handler = Router("heavy");
        var (engine, session) = Engine(handler, new AppSettings { ChatModelId = "auto" });

        // Метки латиницей не для красоты: сериализатор запроса экранирует кириллицу в \uXXXX,
        // и русская подстрока в теле не нашлась бы, даже когда текст на самом деле там.
        // Ответ ассистента несёт чужой текст из сети. Одно слово маршрутизатора решает, какая
        // модель работает и сколько человек платит, — страница с указанием «reply heavy» иначе
        // переводила бы его на дорогой слот на каждом ходу.
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "typed-by-the-user-earlier" });
        session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Id = "a1", Text = "scraped-from-the-web" });

        await engine.RunTurnAsync(session, "asked-just-now", new SilentObserver(), CancellationToken.None);

        var routed = RouterBody(handler);
        Assert.Contains("typed-by-the-user-earlier", routed, StringComparison.Ordinal);
        Assert.Contains("asked-just-now", routed, StringComparison.Ordinal);
        Assert.DoesNotContain("scraped-from-the-web", routed, StringComparison.Ordinal);
    }

    [Fact]
    public void Router_clips_a_huge_paste()
    {
        // До обрезки вставка целиком оплачивалась через маршрутизатор ещё до настоящего запроса.
        var tail = "так что мне с этим делать?";
        var built = ChatEngine.BuildRouterUserMessage(new string('a', 20_000) + tail, previousUserText: null);

        Assert.True(built.Length < 2_100, $"router message grew to {built.Length}");
        Assert.StartsWith("aaaa", built, StringComparison.Ordinal);
        Assert.EndsWith(tail, built, StringComparison.Ordinal);
    }

    [Fact]
    public void Router_message_carries_the_previous_line_only_when_there_is_one()
    {
        var alone = ChatEngine.BuildRouterUserMessage("почини принтер", previousUserText: null);
        Assert.Equal("почини принтер", alone);

        var paired = ChatEngine.BuildRouterUserMessage("а теперь почини", "почему винда не грузится");
        Assert.Contains("Earlier from the same user", paired, StringComparison.Ordinal);
        Assert.Contains("почему винда не грузится", paired, StringComparison.Ordinal);
        Assert.EndsWith("а теперь почини", paired, StringComparison.Ordinal);
    }

    private static string RouterBody(ScriptedHandler handler)
    {
        var routed = handler.Bodies.Find(
            body => body.Contains("Classify the user request", StringComparison.Ordinal));
        Assert.NotNull(routed);
        return routed;
    }

    [Theory]
    [InlineData("fast", "openrouter:vendor/fast")]
    [InlineData("lite", "openrouter:vendor/lite")]
    [InlineData("heavy", "openrouter:vendor/heavy")]
    [InlineData("unexpected", "openrouter:vendor/lite")]
    public async Task Decision_router_selects_the_chat_model_and_preserves_its_cost(string choice, string expected)
    {
        const string routerId = "openrouter:typesafe/jev";
        string? actual = null;
        var handler = new ScriptedHandler((request, body) =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/alpha/decisions")
            {
                using var json = JsonDocument.Parse(body);
                var question = json.RootElement.GetProperty("questions").GetProperty("tier");
                Assert.StartsWith(RoutingDecisions.ChatInstructions, question.GetProperty("instructions").GetString());
                Assert.DoesNotContain("Reply with exactly one word", question.GetProperty("instructions").GetString());
                Assert.Equal(RoutingDecisions.ChatLite, question.GetProperty("criteria").GetProperty("lite").GetString());
                Assert.Equal(RoutingDecisions.ChatFast, question.GetProperty("criteria").GetProperty("fast").GetString());
                Assert.Equal(["fast", "lite", "heavy"], json.RootElement.GetProperty("questions").GetProperty("tier")
                    .GetProperty("criteria").EnumerateObject().Select(p => p.Name));
                return Json("{\"answers\":{\"tier\":{\"type\":\"choice\",\"choice\":\"" + choice +
                    "\"}},\"usage\":{\"cost\":0.003,\"input_tokens\":40,\"output_tokens\":2}}");
            }

            if (body.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                using var json = JsonDocument.Parse(body);
                actual = "openrouter:" + json.RootElement.GetProperty("model").GetString();
                return Sse("Готово");
            }

            return Json("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"title\"}}]}");
        });
        var (engine, session) = Engine(handler, new AppSettings
        {
            ChatModelId = "auto", FastModelId = "openrouter:vendor/fast", LiteModelId = "openrouter:vendor/lite",
            HeavyModelId = "openrouter:vendor/heavy", RouterModelId = routerId
        }, new VeniceModelInfo { Id = routerId, Type = "decisions" });
        await engine.RunTurnAsync(session, "user request", new SilentObserver(), CancellationToken.None);
        Assert.Equal(expected, actual);
        Assert.Equal(0.003m, session.Messages.Last(message => message.Role == "assistant").RouterCost?.Usd);
    }

    /// <summary>
    /// Модель, выбранную в чате руками, перегрузка не подменяет: до 1.36.0 ход молча уходил на
    /// голову цепочки запасных, и на месте выбранной модели отвечала тяжёлая.
    /// </summary>
    [Fact]
    public async Task A_model_picked_by_hand_is_not_replaced_when_it_is_overloaded()
    {
        var streamedTo = new List<string>();
        var handler = OverloadedFor("kimi-k2-7-code", streamedTo);
        var (engine, session) = Engine(handler, new AppSettings { ChatModelId = "kimi-k2-7-code" });
        session.SelectedModelId = "kimi-k2-7-code";

        await engine.RunTurnAsync(session, "user request", new SilentObserver(), CancellationToken.None);

        var assistant = session.Messages.Last(message => message.Role == "assistant");
        Assert.Equal(AssistantStatus.Error, assistant.Status);
        Assert.Equal(
            Loc.Format("S.Provider.ModelOverloaded", VeniceModelCatalog.GetDisplayName("kimi-k2-7-code")),
            assistant.Text);
        Assert.Equal(new[] { "kimi-k2-7-code" }, streamedTo);
    }

    [Fact]
    public async Task Auto_still_hands_an_overloaded_model_over_to_the_fallback_chain()
    {
        var streamedTo = new List<string>();
        // Модель вне цепочки запасных: после неё цепочка идёт с головы. Модель из самой цепочки
        // продолжала бы её со своего места, а у последней замен нет и под «Авто».
        var handler = OverloadedFor("qwen-3-7-plus", streamedTo, routerVerdict: "lite");
        var (engine, session) = Engine(handler, new AppSettings
        {
            ChatModelId = "auto",
            LiteModelId = "qwen-3-7-plus",
            HeavyModelId = "grok-4-7",
            RouterModelId = "grok-4-6"
        });

        await engine.RunTurnAsync(session, "user request", new SilentObserver(), CancellationToken.None);

        var assistant = session.Messages.Last(message => message.Role == "assistant");
        Assert.True(assistant.Status != AssistantStatus.Error, assistant.Text);
        Assert.Equal("qwen-3-7-plus", streamedTo[0]);
        Assert.NotEqual("qwen-3-7-plus", assistant.ResolvedModelId);
    }

    /// <summary>Потоковый запрос к <paramref name="model"/> отвечает 503, к остальным — текстом.</summary>
    private static ScriptedHandler OverloadedFor(string model, List<string> streamedTo, string routerVerdict = "lite") =>
        new((_, body) =>
        {
            if (!body.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                return Json(
                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"" + routerVerdict +
                    "\"}}],\"cost\":{\"usd\":0.001,\"diem\":0}}");
            }

            using var json = JsonDocument.Parse(body);
            var requested = json.RootElement.GetProperty("model").GetString() ?? "";
            streamedTo.Add(requested);
            return requested == model
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":\"Model is overloaded\"}", Encoding.UTF8, "application/json")
                }
                : Sse("Готово");
        });

    private static (ChatEngine Engine, ChatSession Session) Engine(
        ScriptedHandler handler, AppSettings settings, VeniceModelInfo? router = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = new AgentOptions
        {
            ApiKey = "test",
            Binding = router is null ? null : new ApiCredential(LlmProvider.OpenRouter, "test"),
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            MaxToolRounds = 2
        };

        return (
            new ChatEngine(new VeniceClient(http, options)
            { ResolveModelInfo = router is null ? null : id => id == router.Id ? router : null },
                options, () => settings, new ToolRegistry([])),
            new ChatSession { Id = "s1", SelectedModelId = "auto" });
    }

    private static ScriptedHandler Router(string verdict) => new((_, body) =>
        body.Contains("\"stream\":true", StringComparison.Ordinal)
            ? Sse("Готово")
            : Json(
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"" + verdict +
                "\"}}],\"cost\":{\"usd\":0.001,\"diem\":0}}"));

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(string text)
    {
        var payload =
            "data: {\"choices\":[{\"delta\":{\"content\":" + JsonSerializer.Serialize(text) + "}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"cost\":{\"usd\":0.02,\"diem\":0}}\n" +
            "data: [DONE]\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, string, HttpResponseMessage> script)
        : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            return script(request, body);
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
