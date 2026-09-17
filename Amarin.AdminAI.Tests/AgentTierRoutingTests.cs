using System.Net;
using System.Text;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Кто выбирает исполнителя. Раньше — модель чата аргументом <c>complexity</c>, и на рутине она
/// звала флагманского агента: правилами в промпте это чинили дважды и оба раза ненадолго.
/// Теперь уровень называет маршрутизатор, и проверять надо именно это: что аргумента больше нет,
/// что решение приходит со стороны и что отказ маршрутизатора не стоит человеку дорогого слота.
/// </summary>
public sealed class AgentTierRoutingTests
{
    /// <param name="baseUrl">
    /// Недостижимый адрес по умолчанию: живой маршрутизатор из этих тестов в сеть ходить не
    /// должен, а тот единственный тест, что проверяет его отказ, на этом отказе и стоит.
    /// </param>
    private static AgentOptions Options(string baseUrl = "https://example.invalid/v1") => new()
    {
        ApiKey = "test",
        BaseUrl = baseUrl,
        Model = "grok-4-6",
        MaxToolRounds = 3
    };

    private static AgentHost Host(AppSettings settings) =>
        new(Options(),
            new HttpClient { BaseAddress = new Uri("https://example.invalid/") },
            () => settings,
            new ConfirmationQueue(() => settings));

    /// <summary>Запуск агента так, как его видит вызов инструмента: со своим контекстом.</summary>
    private static async Task<ToolCallRecord> RunAsync(
        AgentHost host,
        string? notes = null,
        string? forcedTier = null)
    {
        var call = new ToolCallRecord { Id = "c1", Name = "init_agent" };
        using (AgentRunScope.Push(new AgentRunContext
        {
            Call = call,
            Assistant = new ChatDisplayMessage { Role = "assistant" },
            Observer = new SilentObserver(),
            SessionId = "s",
            ForcedTier = forcedTier
        }))
        {
            await host.RunAsync("посмотри, что с принтером", notes, CancellationToken.None);
        }

        return call;
    }

    // ───────────────────────── инструмент ─────────────────────────

    [Fact]
    public void Init_agent_no_longer_offers_a_tier_to_choose()
    {
        var schema = new InitAgentTool(new AgentSlotLimiter(), new RecordingHost()).ParametersSchema;

        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("prompt", out _));
        Assert.True(properties.TryGetProperty("notes", out _));
        Assert.False(properties.TryGetProperty("complexity", out _));

        // Пометки необязательны: сказать сверх задания бывает нечего, и выдумывать строку ради
        // схемы значит кормить маршрутизатор шумом.
        Assert.Equal(
            ["prompt"],
            schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));

        Assert.DoesNotContain("heavy", schema.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_tier_sent_out_of_old_habit_is_ignored_rather_than_refused()
    {
        // Отказ стоил бы целого раунда ради аргумента, который всё равно ничего не решает.
        var host = new RecordingHost();
        var tool = new InitAgentTool(new AgentSlotLimiter(), host);

        var result = await tool.ExecuteAsync(
            JsonSchema.Parse("""{"prompt":"глянь свободное место","complexity":"heavy"}"""));

        Assert.True(result.Success);
        Assert.Null(host.LastNotes);
    }

    [Fact]
    public async Task Notes_are_optional_and_reach_the_host_when_written()
    {
        var host = new RecordingHost();
        var tool = new InitAgentTool(new AgentSlotLimiter(), host);

        Assert.True((await tool.ExecuteAsync(JsonSchema.Parse("""{"prompt":"глянь место"}"""))).Success);
        Assert.Null(host.LastNotes);

        Assert.True((await tool.ExecuteAsync(
            JsonSchema.Parse("""{"prompt":"глянь место","notes":"пользователь просит быстрее"}"""))).Success);
        Assert.Equal("пользователь просит быстрее", host.LastNotes);
    }

    // ───────────────────────── разбор ответа ─────────────────────────

    [Theory]
    [InlineData("heavy", "heavy")]
    [InlineData("HEAVY.", "heavy")]
    [InlineData("`fast`", "fast")]
    [InlineData("fast — задача простая", "fast")]
    [InlineData("lite", "lite")]
    [InlineData("не могу решить", "lite")]
    [InlineData("", "lite")]
    [InlineData(null, "lite")]
    public void The_tier_is_the_first_word_and_anything_else_is_lite(string? answer, string expected) =>
        Assert.Equal(expected, AgentTierRouter.ParseTier(answer));

    [Fact]
    public void The_router_prompt_states_a_rule_and_not_a_case()
    {
        // CLAUDE.md: разбор конкретного запроса в промпте тянет ответ к нему и к похожим
        // формулировкам вместо того, о чём спросили на самом деле.
        foreach (var example in new[] { "For example", "e.g.", "Например", "Пример" })
        {
            Assert.DoesNotContain(example, AgentTierRouter.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        }

        Assert.StartsWith("Choose who runs this task.", AgentTierRouter.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("fast, lite or heavy", AgentTierRouter.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("reply lite", AgentTierRouter.SystemPrompt, StringComparison.Ordinal);

        // Мера — известны ли шаги заранее, а не каким глаголом названа работа. Слова из этого
        // списка сюда возвращаться не должны, и каждое стоило одного прогона вслепую:
        // «install» уводил установку драйвера на флагманский слот дословно, а «find» — следом,
        // потому что задания начинаются со слова «найди», и поиск читался как «путь надо найти».
        foreach (var trap in new[] { "install", "repair", "uninstall", "registry", "boot", "find", "found" })
        {
            Assert.DoesNotContain(trap, AgentTierRouter.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("the steps are known before the work starts", AgentTierRouter.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains("not knowing what the steps are", AgentTierRouter.SystemPrompt,
            StringComparison.Ordinal);

        // Поиск и выбор из вариантов — обычная работа, а не повод для дорогого слота; названная
        // цель есть у каждой задачи; при равных берётся дешёвое. Каждое из трёх правил снимало
        // свою долю завышений, и без них замер разъезжается обратно.
        Assert.Contains("choosing between candidates", AgentTierRouter.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Naming the goal", AgentTierRouter.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("take the cheaper one", AgentTierRouter.SystemPrompt, StringComparison.Ordinal);

        // Первая строка своя: по ней тест отличает этот запрос от запроса маршрутизатора чата.
        Assert.NotEqual(
            ChatEngine.RouterSystemPrompt[..20], AgentTierRouter.SystemPrompt[..20]);
    }

    [Fact]
    public void The_router_reads_the_task_and_the_notes_and_clips_a_huge_one()
    {
        var message = AgentTierRouter.BuildUserMessage("почини печать", "пользователь просит быстрее");

        Assert.Contains("почини печать", message, StringComparison.Ordinal);
        Assert.Contains("пользователь просит быстрее", message, StringComparison.Ordinal);

        // Без пометок лишней подписи в сообщении нет: пустая строка только сбивает.
        Assert.DoesNotContain("Notes", AgentTierRouter.BuildUserMessage("почини печать", null),
            StringComparison.Ordinal);

        // Задание на десятки килобайт не должно оплачиваться ещё и выбором исполнителя.
        var huge = AgentTierRouter.BuildUserMessage(new string('ф', 20_000), null);
        Assert.True(huge.Length < 2100, $"router message grew to {huge.Length}");
    }

    // ───────────────────────── решение и запуск ─────────────────────────

    [Fact]
    public async Task The_tier_comes_from_the_router_and_not_from_the_chat_model()
    {
        var settings = AppSettings.CreateDefault();
        settings.AgentLiteModelId = "lite-model";
        settings.AgentHeavyModelId = "heavy-model";

        var host = Host(settings);
        var models = new List<string>();
        host.Route = (_, _, _) => Task.FromResult(new AgentTierDecision("lite", null));
        host.Attempt = (_, modelId, _) =>
        {
            models.Add(modelId);
            return Task.FromResult(new AgentRunResult { AssistantText = "Готово." });
        };

        await RunAsync(host);

        Assert.Equal(["lite-model"], models);
    }

    [Fact]
    public async Task The_router_sees_the_task_and_the_notes_it_was_given()
    {
        var host = Host(AppSettings.CreateDefault());
        string? seenPrompt = null;
        string? seenNotes = null;
        host.Route = (prompt, notes, _) =>
        {
            seenPrompt = prompt;
            seenNotes = notes;
            return Task.FromResult(new AgentTierDecision("lite", null));
        };
        host.Attempt = (_, _, _) => Task.FromResult(new AgentRunResult { AssistantText = "Готово." });

        await RunAsync(host, "исход неочевиден");

        Assert.Equal("посмотри, что с принтером", seenPrompt);
        Assert.Equal("исход неочевиден", seenNotes);
    }

    [Fact]
    public async Task A_tier_named_by_the_person_never_pays_for_a_router_call()
    {
        var settings = AppSettings.CreateDefault();
        settings.AgentFastModelId = "fast-model";

        var host = Host(settings);
        var models = new List<string>();
        host.Route = (_, _, _) => throw new InvalidOperationException(
            "Уровень назвал человек командой — спрашивать о нём модель значит тратить его деньги.");
        host.Attempt = (_, modelId, _) =>
        {
            models.Add(modelId);
            return Task.FromResult(new AgentRunResult { AssistantText = "Готово." });
        };

        await RunAsync(host, forcedTier: "fast");

        Assert.Equal(["fast-model"], models);
    }

    [Fact]
    public async Task A_router_failure_falls_back_to_lite_rather_than_to_the_flagship()
    {
        var settings = AppSettings.CreateDefault();
        settings.AgentLiteModelId = "lite-model";
        settings.AgentHeavyModelId = "heavy-model";
        settings.RouterModelId = "router-model";

        // Живой путь целиком, но сеть отвечает отказом: агент всё равно обязан запуститься,
        // и запускается он на дешёвом уровне.
        var host = new AgentHost(
            Options(),
            new HttpClient { BaseAddress = new Uri("https://example.invalid/") },
            () => settings,
            new ConfirmationQueue(() => settings));

        var models = new List<string>();
        host.Attempt = (_, modelId, _) =>
        {
            models.Add(modelId);
            return Task.FromResult(new AgentRunResult { AssistantText = "Готово." });
        };

        await RunAsync(host);

        Assert.Equal(["lite-model"], models);
    }

    [Fact]
    public async Task The_price_of_the_decision_is_billed_to_the_agent_and_not_lost()
    {
        // Маршрутизатор работает на клиенте агента, ход чата от него закрыт: не положить его
        // деньги в счёт агента значило бы потерять их совсем.
        var host = Host(AppSettings.CreateDefault());
        host.Route = (_, _, _) => Task.FromResult(
            new AgentTierDecision("lite", new VeniceCost { Usd = 0.001m, HasData = true }));
        host.Attempt = (_, _, _) => Task.FromResult(new AgentRunResult
        {
            AssistantText = "Готово.",
            Cost = new VeniceCost { Usd = 0.02m, HasData = true }
        });

        var call = await RunAsync(host);

        Assert.Equal(0.021m, call.NestedAgent!.Cost!.Usd);
    }

    [Fact]
    public async Task The_slash_command_carries_its_tier_all_the_way_to_the_agent()
    {
        // Единственное звено, где уровень от человека мог бы молча потеряться: команду
        // подделывают под вызов инструмента, а ключа уровня в аргументах больше нет.
        var handler = new ScriptedHandler(_ => Sse("Готово."));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = Options("https://api.venice.ai/api/v1");
        var probe = new TierProbeTool();
        var engine = new ChatEngine(
            new VeniceClient(http, options), options, AppSettings.CreateDefault, new ToolRegistry([probe]));

        await engine.RunAgentCommandAsync(
            new ChatSession { Id = "s", SelectedModelId = "grok-4-6" },
            "/agent-fast посмотри диск",
            "посмотри диск",
            "fast",
            new SilentObserver(),
            CancellationToken.None);

        Assert.Equal("fast", probe.SeenTier);
    }

    /// <summary>Встаёт на место <c>init_agent</c> и запоминает, какой уровень до него доехал.</summary>
    private sealed class TierProbeTool : ITool
    {
        public string? SeenTier { get; private set; }

        public string Name => InitAgentTool.ToolName;

        public string Description => "probe";

        public System.Text.Json.JsonElement ParametersSchema =>
            JsonSchema.Parse("""{"type":"object","properties":{}}""");

        public Task<ToolResult> ExecuteAsync(
            System.Text.Json.JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            SeenTier = AgentRunScope.Current?.ForcedTier;
            return Task.FromResult(ToolResult.Ok("Отчёт агента: готово."));
        }
    }

    private static HttpResponseMessage Sse(string text)
    {
        var payload =
            "data: {\"choices\":[{\"delta\":{\"content\":\"" + text + "\"}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"cost\":{\"usd\":0.01,\"diem\":0}}\n" +
            "data: [DONE]\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> script)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(script(request));
    }

    private sealed class RecordingHost : IAgentHost
    {
        public string? LastNotes { get; private set; }

        public Task<ToolResult> RunAsync(string prompt, string? notes, CancellationToken cancellationToken)
        {
            LastNotes = notes;
            return Task.FromResult(ToolResult.Ok("готово"));
        }
    }

    private sealed class SilentObserver : IChatTurnObserver
    {
        public void OnUserAdded(ChatDisplayMessage message) { }

        public void OnAssistantStarted(ChatDisplayMessage message) { }

        public void OnAssistantText(ChatDisplayMessage message) { }

        public void OnToolsChanged(ChatDisplayMessage message) { }

        public void OnAssistantCompleted(ChatDisplayMessage message) { }

        public void OnAssistantCancelled(ChatDisplayMessage message) { }

        public void OnUserAppended(ChatDisplayMessage message) { }

        public void OnError(string message) { }
    }
}
