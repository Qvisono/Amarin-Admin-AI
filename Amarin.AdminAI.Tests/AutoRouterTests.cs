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
        Assert.Contains("hardest single step", ChatEngine.RouterSystemPrompt, StringComparison.Ordinal);
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
        Assert.Contains("thinking that reply has to do itself", flat, StringComparison.Ordinal);
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

    private static (ChatEngine Engine, ChatSession Session) Engine(ScriptedHandler handler, AppSettings settings)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            MaxToolRounds = 2
        };

        return (
            new ChatEngine(new VeniceClient(http, options), options, () => settings, new ToolRegistry([])),
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
