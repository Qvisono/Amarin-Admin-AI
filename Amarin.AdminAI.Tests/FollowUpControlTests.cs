using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// A line typed while an agent is running. It used to reach the model only after the agent had
/// finished — that is, after exactly the work the person was asking it not to do. These tests pin
/// the seam that lets "стоп" and "быстрее" arrive while they can still change the outcome.
/// </summary>
public sealed class FollowUpControlTests
{
    private static AgentOptions Options() => new()
    {
        ApiKey = "test",
        BaseUrl = "https://api.venice.ai/api/v1",
        Model = "grok-4-6",
        MaxToolRounds = 3
    };

    private static RunningAgent Agent(AgentRegistry registry, CancellationTokenSource cts) =>
        registry.Register("s", "проверь диск C", "lite", "grok-4-3", cts);

    // ───────────────────────── the registry ─────────────────────────

    [Fact]
    public void A_running_agent_is_reachable_by_the_chat_it_was_started_from()
    {
        var registry = new AgentRegistry();
        using var mine = new CancellationTokenSource();
        using var neighbour = new CancellationTokenSource();

        var agent = Agent(registry, mine);
        registry.Register("other", "чужая задача", "lite", "grok-4-3", neighbour);

        // The neighbouring chat's agent must not be in this chat's list: stopping "the agent"
        // would otherwise kill work started from a conversation the person is not even looking at.
        Assert.Equal([agent.Id], registry.ListFor("s").Select(item => item.Id));
        Assert.Empty(registry.ListFor(""));

        registry.Unregister(agent.Id);
        Assert.Empty(registry.ListFor("s"));
    }

    [Fact]
    public void Asking_an_agent_to_stop_cancels_only_its_own_token()
    {
        var registry = new AgentRegistry();
        using var turn = new CancellationTokenSource();
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(turn.Token);

        Agent(registry, interrupt).Request(new AgentInterrupt(AgentInterruptKind.Stop));

        Assert.True(interrupt.IsCancellationRequested);

        // The turn itself survives: the whole point is that the answer goes on without the agent.
        Assert.False(turn.IsCancellationRequested);
    }

    // ───────────────────────── the decision ─────────────────────────

    [Fact]
    public void The_question_lists_the_agents_that_are_actually_running()
    {
        var registry = new AgentRegistry();
        using var cts = new CancellationTokenSource();
        var agent = Agent(registry, cts);

        var prompt = FollowUpDirector.BuildPrompt("стой, не надо", [agent]);

        Assert.Contains(agent.Id, prompt, StringComparison.Ordinal);
        Assert.Contains("grok-4-3", prompt, StringComparison.Ordinal);
        Assert.Contains("проверь диск C", prompt, StringComparison.Ordinal);
        Assert.Contains("стой, не надо", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stop_names_every_agent_when_it_names_none()
    {
        var registry = new AgentRegistry();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var agents = new[] { Agent(registry, first), Agent(registry, second) };

        var decision = FollowUpDirector.Parse("""{"action":"stop","note":"Останавливаю."}""", agents);

        Assert.Equal(AgentInterruptKind.Stop, decision.Kind);
        Assert.Equal(agents.Select(agent => agent.Id), decision.AgentIds);
        Assert.Equal("Останавливаю.", decision.Note);
    }

    [Fact]
    public void A_switch_without_a_tier_is_read_as_a_request_to_hurry()
    {
        var registry = new AgentRegistry();
        using var cts = new CancellationTokenSource();
        var agent = Agent(registry, cts);

        var fenced =
            "```json\n" +
            "{\"action\":\"switch\",\"agents\":[\"" + agent.Id + "\"]," +
            "\"note\":\"Беру модель побыстрее.\"}\n" +
            "```";

        var decision = FollowUpDirector.Parse(fenced, [agent]);

        // Fenced, and with no complexity at all — both are everyday model output.
        Assert.Equal(AgentInterruptKind.Switch, decision.Kind);
        Assert.Equal("fast", decision.Complexity);
        Assert.Equal([agent.Id], decision.AgentIds);
    }

    [Fact]
    public void A_correction_is_addressed_to_the_agent_it_concerns()
    {
        var registry = new AgentRegistry();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var agents = new[] { Agent(registry, first), Agent(registry, second) };

        var decision = FollowUpDirector.Parse(
            "{\"action\":\"tell\",\"agents\":[\"" + agents[1].Id + "\"]," +
            "\"message\":\"Смотреть диск D, а не C.\",\"note\":\"Передаю агенту.\"}",
            agents,
            "ой, я про диск D");

        Assert.Equal(AgentInterruptKind.Tell, decision.Kind);
        Assert.Equal([agents[1].Id], decision.AgentIds);
        Assert.Equal("Смотреть диск D, а не C.", decision.Message);

        // Ни одна отмена не тронута: уточнение не прерывает работу.
        Assert.False(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
    }

    [Fact]
    public void A_correction_the_model_did_not_rewrite_is_passed_on_as_the_person_wrote_it()
    {
        var registry = new AgentRegistry();
        using var cts = new CancellationTokenSource();
        var agent = Agent(registry, cts);

        var decision = FollowUpDirector.Parse("""{"action":"tell"}""", [agent], "диск D, а не C");

        Assert.Equal("диск D, а не C", decision.Message);
        Assert.Equal([agent.Id], decision.AgentIds);
    }

    [Fact]
    public void An_empty_correction_is_not_handed_on_at_all()
    {
        var registry = new AgentRegistry();
        using var cts = new CancellationTokenSource();

        // Передавать агенту пустую строку — значит потратить его шаг на ничто.
        Assert.Equal(
            AgentInterruptKind.None,
            FollowUpDirector.Parse("""{"action":"tell","message":"  "}""", [Agent(registry, cts)]).Kind);
    }

    [Fact]
    public void What_is_told_to_an_agent_waits_for_it_rather_than_interrupting_it()
    {
        var registry = new AgentRegistry();
        using var cts = new CancellationTokenSource();
        var agent = Agent(registry, cts);

        agent.Tell("диск D, а не C");
        agent.Tell("   ");
        agent.Tell("и не трогай загрузки");

        Assert.False(cts.IsCancellationRequested);
        Assert.Equal(["диск D, а не C", "и не трогай загрузки"], agent.TakeNotes());

        // Забрали — значит отдали агенту; второй раз то же самое он получить не должен.
        Assert.Empty(agent.TakeNotes());
    }

    [Theory]
    [InlineData("""{"action":"none"}""")]
    [InlineData("не знаю, что тут ответить")]
    [InlineData("")]
    [InlineData("""{"action":"stop","agents":["a999"]}""")]
    public void Anything_unclear_leaves_the_work_alone(string answer)
    {
        var registry = new AgentRegistry();
        using var cts = new CancellationTokenSource();
        var agent = Agent(registry, cts);

        var decision = FollowUpDirector.Parse(answer, [agent]);

        // Except for the last case: an unknown id falls back to "all of them", which is still a
        // stop. Everything else must be None — a wrong stop costs the work already done.
        if (answer.Contains("stop", StringComparison.Ordinal))
        {
            Assert.Equal(AgentInterruptKind.Stop, decision.Kind);
            return;
        }

        Assert.Equal(AgentInterruptKind.None, decision.Kind);
        Assert.Empty(decision.AgentIds);
    }

    [Fact]
    public async Task A_broken_answer_from_the_model_is_not_a_broken_turn()
    {
        var options = Options();
        using var http = new HttpClient(new FailingHandler())
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };

        var registry = new AgentRegistry();
        using var cts = new CancellationTokenSource();

        var decision = await FollowUpDirector.DecideAsync(
            new VeniceClient(http, options), "fast-model", "стоп", [Agent(registry, cts)]);

        Assert.Equal(AgentInterruptKind.None, decision.Kind);
    }

    // ───────────────────────── what reaches the agent ─────────────────────────

    [Fact]
    public async Task A_correction_reaches_the_agent_at_its_next_step_in_a_place_the_api_accepts()
    {
        var bodies = new List<string>();
        var round = 0;
        var handler = new CapturingHandler(
            bodies.Add,
            () => Interlocked.Increment(ref round) == 1
                ? """
                  {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
                    {"id":"c1","type":"function","function":{"name":"read_file","arguments":"{}"}}]},
                    "finish_reason":"tool_calls"}]}
                  """
                : """{"choices":[{"message":{"role":"assistant","content":"Посмотрел диск D."}}]}""");

        var options = Options();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };

        var notes = new Queue<string>(["диск D, а не C"]);
        var agent = new Agent(
            new VeniceClient(http, options),
            new ToolRegistry([new StubTool()]),
            options,
            new SilentAgentUi(),
            new SessionUndoTracker())
        {
            TakeNotes = () => notes.Count == 0 ? [] : [notes.Dequeue()]
        };

        var result = await agent.RunAsync("посмотри диск C");

        Assert.Equal("Посмотрел диск D.", result.AssistantText);
        Assert.Equal(2, bodies.Count);

        var roles = Roles(bodies[1]);

        // Вводная стоит после ответов инструментов, а не между вызовом и ответом на него:
        // такую пару API отвергает целиком.
        Assert.Equal("user", roles[^1]);
        Assert.Equal("tool", roles[^2]);
        Assert.Equal("диск D, а не C", LastText(bodies[1]));
    }

    [Fact]
    public async Task An_agent_nobody_wrote_to_carries_on_exactly_as_before()
    {
        var bodies = new List<string>();
        var handler = new CapturingHandler(
            bodies.Add,
            () => """{"choices":[{"message":{"role":"assistant","content":"Готово."}}]}""");

        var options = Options();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };

        var agent = new Agent(
            new VeniceClient(http, options),
            new ToolRegistry([new StubTool()]),
            options,
            new SilentAgentUi(),
            new SessionUndoTracker());

        var result = await agent.RunAsync("посмотри диск C");

        // Крючок не подключён вовсе — так агент работает везде, кроме чата.
        Assert.Equal("Готово.", result.AssistantText);
        Assert.Equal(["system", "user"], Roles(Assert.Single(bodies)));
    }

    /// <summary>Текст последнего сообщения запроса. Читается разбором: в теле кириллица экранирована.</summary>
    private static string LastText(string body)
    {
        var content = JsonDocument.Parse(body).RootElement.GetProperty("messages")
            .EnumerateArray()
            .Last()
            .GetProperty("content");

        return (content.ValueKind == JsonValueKind.String ? content.GetString() : null) ?? "";
    }

    private static List<string> Roles(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("messages")
            .EnumerateArray()
            .Select(message => message.GetProperty("role").GetString() ?? "")
            .ToList();

    // ───────────────────────── the agent itself ─────────────────────────

    /// <remarks>
    /// <c>Route</c> подменён вместе с <c>Attempt</c>: живой выбор уровня уходит в сеть, а здесь
    /// проверяется то, что вокруг агента, — остановка, пересадка и доставка уточнений.
    /// </remarks>
    private static AgentHost Host(AgentRegistry registry, AppSettings settings) =>
        new(Options(),
            new HttpClient { BaseAddress = new Uri("https://example.invalid/") },
            () => settings,
            new ConfirmationQueue(() => settings),
            registry)
        {
            Route = (_, _, _) => Task.FromResult(new AgentTierDecision("lite", null))
        };

    [Fact]
    public async Task An_agent_asked_to_hurry_is_moved_to_the_fast_model_and_finishes_there()
    {
        var registry = new AgentRegistry();
        var settings = AppSettings.CreateDefault();
        settings.AgentLiteModelId = "lite-model";
        settings.AgentFastModelId = "fast-model";

        var host = Host(registry, settings);
        var models = new List<string>();
        host.Attempt = async (_, modelId, token) =>
        {
            models.Add(modelId);
            if (models.Count == 1)
            {
                // Первая попытка ждёт — как настоящий агент ждёт своей модели.
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }

            return new AgentRunResult { AssistantText = "Готово, места хватает." };
        };

        var switching = Task.Run(async () =>
        {
            while (registry.ListFor("s").Count == 0)
            {
                await Task.Delay(10);
            }

            registry.ListFor("s")[0].Request(new AgentInterrupt(AgentInterruptKind.Switch, "fast"));
        });

        var call = new ToolCallRecord { Id = "c1", Name = "init_agent" };
        ToolResult result;
        using (AgentRunScope.Push(new AgentRunContext
        {
            Call = call,
            Assistant = new ChatDisplayMessage { Role = "assistant" },
            Observer = new QueueingObserver(),
            SessionId = "s"
        }))
        {
            result = await host.RunAsync("проверь диск C", notes: null, CancellationToken.None);
        }

        await switching;

        Assert.Equal(["lite-model", "fast-model"], models);
        Assert.True(result.Success);
        Assert.Contains("Готово, места хватает.", result.Output, StringComparison.Ordinal);

        // И слот агента показывает ту модель, на которой он в итоге работал.
        Assert.Equal("fast-model", call.NestedAgent!.ModelId);
        Assert.Equal(AgentRunStatus.Complete, call.NestedAgent.Status);

        // Никто не остался в реестре: иначе следующая просьба «стоп» пошла бы в пустоту.
        Assert.Empty(registry.ListFor("s"));
    }

    [Fact]
    public async Task A_note_written_just_before_a_switch_travels_to_the_agent_that_replaces_it()
    {
        var registry = new AgentRegistry();
        var settings = AppSettings.CreateDefault();
        var host = Host(registry, settings);

        var attempts = 0;
        IReadOnlyList<string> seen = [];
        host.Attempt = async (_, _, token) =>
        {
            var entry = registry.ListFor("s")[0];
            if (Interlocked.Increment(ref attempts) == 1)
            {
                // Обе просьбы приходят подряд — так и печатают: сначала уточнение, следом «быстрее».
                entry.Tell("и не трогай загрузки");
                entry.Request(new AgentInterrupt(AgentInterruptKind.Switch, "fast"));
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }

            seen = entry.TakeNotes();
            return new AgentRunResult { AssistantText = "Готово." };
        };

        using (AgentRunScope.Push(new AgentRunContext
        {
            Call = new ToolCallRecord { Id = "c1", Name = "init_agent" },
            Assistant = new ChatDisplayMessage { Role = "assistant" },
            Observer = new QueueingObserver(),
            SessionId = "s"
        }))
        {
            await host.RunAsync("проверь диск C", notes: null, CancellationToken.None);
        }

        Assert.Equal(2, attempts);
        Assert.Equal(["и не трогай загрузки"], seen);
    }

    [Fact]
    public async Task An_agent_asked_to_stop_reports_it_instead_of_failing_the_turn()
    {
        var registry = new AgentRegistry();
        var settings = AppSettings.CreateDefault();
        var host = Host(registry, settings);

        var attempts = 0;
        host.Attempt = async (_, _, token) =>
        {
            Interlocked.Increment(ref attempts);
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            return new AgentRunResult { AssistantText = "не должно случиться" };
        };

        var stopping = Task.Run(async () =>
        {
            while (registry.ListFor("s").Count == 0)
            {
                await Task.Delay(10);
            }

            registry.ListFor("s")[0].Request(new AgentInterrupt(AgentInterruptKind.Stop));
        });

        var call = new ToolCallRecord { Id = "c1", Name = "init_agent" };
        ToolResult result;
        using (AgentRunScope.Push(new AgentRunContext
        {
            Call = call,
            Assistant = new ChatDisplayMessage { Role = "assistant" },
            Observer = new QueueingObserver(),
            SessionId = "s"
        }))
        {
            result = await host.RunAsync("проверь диск C", notes: null, CancellationToken.None);
        }

        await stopping;

        // Остановка — это ответ, а не сбой: модель читает его текст и рассказывает человеку,
        // что произошло, вместо того чтобы молча начать всё заново.
        Assert.True(result.Success);
        Assert.Equal(1, attempts);
        Assert.Contains("остановлен", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentRunStatus.Cancelled, call.NestedAgent!.Status);
    }

    [Fact]
    public async Task Cancelling_the_whole_turn_is_still_a_cancellation_and_not_a_report()
    {
        var registry = new AgentRegistry();
        var settings = AppSettings.CreateDefault();
        var host = Host(registry, settings);
        host.Attempt = async (_, _, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            return new AgentRunResult();
        };

        using var turn = new CancellationTokenSource();
        var running = host.RunAsync("проверь диск C", notes: null, turn.Token);
        await turn.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    // ───────────────────────── the seam, end to end ─────────────────────────

    [Fact]
    public async Task A_line_typed_while_an_agent_works_stops_it_without_stopping_the_turn()
    {
        var registry = new AgentRegistry();
        var observer = new QueueingObserver();
        var tool = new AgentLikeTool(registry);

        var round = 0;
        var handler = new ScriptedHandler(_ => Interlocked.Increment(ref round) == 1
            ? SseWithToolCall("Запускаю агента.", AgentLikeTool.ToolName)
            : Sse("Агента остановил, жду указаний."));

        var options = Options();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };
        var settings = AppSettings.CreateDefault();
        var engine = new ChatEngine(
            new VeniceClient(http, options),
            options,
            () => settings,
            new ToolRegistry([tool]),
            registry)
        {
            // Стоит подменить: живой путь ушёл бы в сеть за решением быстрой модели.
            FollowUpDecider = (_, agents, _) => Task.FromResult(
                new FollowUpDecision(AgentInterruptKind.Stop, agents.Select(a => a.Id).ToList(), "",
                    "Понял, останавливаю агента."))
        };

        // Типизируем ровно тот момент, ради которого всё затевалось: агент уже работает.
        tool.Started = () => observer.Enqueue("стой, не надо");

        var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
        await engine.RunTurnAsync(session, "посмотри диск C", observer, CancellationToken.None);

        Assert.True(tool.WasInterrupted, "агент доработал до конца, хотя его просили остановиться");

        // Ход при этом жив: ответ есть, и дописанная строка лежит в стенограмме ровно один раз.
        Assert.Equal(["user", "assistant", "tool", "user", "assistant"],
            session.ApiMessages.Select(message => message.Role));
        Assert.Single(
            session.ApiMessages,
            message => ChatContent.ReadText(message.Content) == "стой, не надо");

        var note = session.Messages
            .SelectMany(message => message.ToolRounds)
            .Select(item => item.FollowUpNote)
            .First(text => !string.IsNullOrWhiteSpace(text));
        Assert.Equal("Понял, останавливаю агента.", note);
    }

    [Fact]
    public async Task A_correction_typed_mid_run_reaches_the_agent_without_costing_it_its_work()
    {
        var registry = new AgentRegistry();
        var observer = new QueueingObserver();
        var tool = new AgentLikeTool(registry);

        var round = 0;
        var handler = new ScriptedHandler(_ => Interlocked.Increment(ref round) == 1
            ? SseWithToolCall("Запускаю агента.", AgentLikeTool.ToolName)
            : Sse("Агент переключился на диск D."));

        var options = Options();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };
        var settings = AppSettings.CreateDefault();
        var engine = new ChatEngine(
            new VeniceClient(http, options),
            options,
            () => settings,
            new ToolRegistry([tool]),
            registry)
        {
            FollowUpDecider = (_, agents, _) => Task.FromResult(new FollowUpDecision(
                AgentInterruptKind.Tell,
                agents.Select(agent => agent.Id).ToList(),
                "",
                "Передаю агенту: смотреть диск D.",
                "смотри диск D, а не C"))
        };

        tool.Started = () => observer.Enqueue("ой, я про диск D");

        var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
        await engine.RunTurnAsync(session, "посмотри диск C", observer, CancellationToken.None);

        Assert.Equal(["смотри диск D, а не C"], tool.Told);

        // Главное отличие от «стоп»: агента не трогали, он доработал и отчитался сам.
        Assert.False(tool.WasInterrupted, "агента прервали ради уточнения");

        // Строка при этом всё равно попала в стенограмму: ответ пишется человеку, а не агенту.
        Assert.Single(
            session.ApiMessages,
            message => ChatContent.ReadText(message.Content) == "ой, я про диск D");

        var note = session.Messages
            .SelectMany(message => message.ToolRounds)
            .Select(item => item.FollowUpNote)
            .First(text => !string.IsNullOrWhiteSpace(text));
        Assert.Equal("Передаю агенту: смотреть диск D.", note);
    }

    [Fact]
    public async Task With_no_agent_running_the_line_is_still_shown_as_noticed()
    {
        var observer = new QueueingObserver();
        var tool = new AgentLikeTool(agents: null);

        var round = 0;
        var handler = new ScriptedHandler(_ => Interlocked.Increment(ref round) == 1
            ? SseWithToolCall("", AgentLikeTool.ToolName)
            : Sse("Готово."));

        var options = Options();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };
        var settings = AppSettings.CreateDefault();
        var engine = new ChatEngine(
            new VeniceClient(http, options), options, () => settings, new ToolRegistry([tool]));

        tool.Started = () => observer.Enqueue("и ещё про диск D");

        var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
        await engine.RunTurnAsync(session, "посмотри диск C", observer, CancellationToken.None);

        var note = session.Messages
            .SelectMany(message => message.ToolRounds)
            .Select(item => item.FollowUpNote)
            .First(text => !string.IsNullOrWhiteSpace(text));

        Assert.Equal(Loc.Get("S.Tools.FollowUpSeen"), note);
    }

    [Fact]
    public async Task The_answer_ends_where_the_line_was_taken_in_instead_of_growing_above_it()
    {
        var observer = new QueueingObserver();
        var tool = new AgentLikeTool(agents: null);

        var round = 0;
        var handler = new ScriptedHandler(_ => Interlocked.Increment(ref round) == 1
            ? SseWithToolCall("Смотрю диск.", AgentLikeTool.ToolName)
            : Sse("Теперь про диск D."));

        var options = Options();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };
        var settings = AppSettings.CreateDefault();
        var engine = new ChatEngine(
            new VeniceClient(http, options), options, () => settings, new ToolRegistry([tool]));

        tool.Started = () => observer.Enqueue("а теперь диск D");

        var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
        await engine.RunTurnAsync(session, "посмотри диск C", observer, CancellationToken.None);

        var assistants = session.Messages.Where(message => message.Role == "assistant").ToList();

        // Два ответа, а не один: иначе продолжение дописывалось бы в пузырь, который стоит выше
        // реплики человека, и ответ читался бы раньше вопроса.
        Assert.Equal(2, assistants.Count);
        Assert.Equal("", assistants[0].Text);
        Assert.Single(assistants[0].ToolRounds);
        Assert.Equal("Теперь про диск D.", assistants[1].Text);
        Assert.All(assistants, message => Assert.Equal(AssistantStatus.Complete, message.Status));

        // Закрытый посреди хода ответ — это не «ответ готов»: на нём не должно быть ни тоста,
        // ни звука, иначе каждая дописанная строка превращалась бы в ложное уведомление.
        Assert.Equal(1, observer.Continued);
        Assert.Equal(1, observer.Completed);
    }

    // ───────────────────────── helpers ─────────────────────────

    private static HttpResponseMessage Sse(string text)
    {
        var payload =
            "data: {\"model\":\"grok-4-6\",\"choices\":[{\"delta\":{\"content\":" +
            JsonSerializer.Serialize(text) + "}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n" +
            "data: [DONE]\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }

    private static HttpResponseMessage SseWithToolCall(string text, string tool)
    {
        var payload =
            "data: {\"model\":\"grok-4-6\",\"choices\":[{\"delta\":{\"content\":" +
            JsonSerializer.Serialize(text) + "}}]}\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\"," +
            "\"function\":{\"name\":\"" + tool + "\",\"arguments\":\"{}\"}}]}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n" +
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

    private sealed class CapturingHandler(Action<string> capture, Func<string> reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            capture(request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply(), Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>Агент без интерфейса: в этих тестах важно, что он отправляет, а не что рисует.</summary>
    private sealed class SilentAgentUi : IAgentUi
    {
        public void Warn(string message)
        {
        }

        public void Error(string message)
        {
        }

        public void Info(string message)
        {
        }

        public void AssistantMessage(string text)
        {
        }

        public void ToolCall(string name, string argumentsJson)
        {
        }

        public void ToolResult(string name, ToolResult result)
        {
        }

        public Task<T> RunBusyAsync<T>(
            string message,
            Func<Task<T>> work,
            CancellationToken cancellationToken = default) => work();

        public Task<bool> ConfirmDangerousActionAsync(
            DangerousActionInfo info,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class StubTool : ITool
    {
        public string Name => "read_file";

        public string Description => "stub";

        public JsonElement ParametersSchema =>
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        public Task<ToolResult> ExecuteAsync(
            JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Ok("свободно 40 ГБ"));
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("no network");
    }

    /// <summary>
    /// Стоит на месте init_agent: записывается в реестр и ждёт, пока его не прервут, — так же,
    /// как настоящий агент ждёт своей модели внутри вызова инструмента.
    /// </summary>
    private sealed class AgentLikeTool(AgentRegistry? agents) : ITool
    {
        public const string ToolName = "init_agent";

        public Action? Started { get; set; }

        public List<string> Told { get; } = [];

        public bool WasInterrupted { get; private set; }

        public string Name => ToolName;

        public string Description => "stub";

        public JsonElement ParametersSchema =>
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        public async Task<ToolResult> ExecuteAsync(
            JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var entry = agents?.Register(
                AgentRunScope.Current?.SessionId ?? "", "проверь диск C", "lite", "grok-4-3", interrupt);

            Started?.Invoke();
            try
            {
                // Дольше одного оборота наблюдателя, но не настолько, чтобы тест висел, если
                // прерывание не сработает. По дороге агент забирает вводные — так же, как
                // настоящий делает это на границе своего раунда.
                for (var i = 0; i < 50; i++)
                {
                    await Task.Delay(100, interrupt.Token);
                    var notes = entry?.TakeNotes() ?? [];
                    if (notes.Count > 0)
                    {
                        Told.AddRange(notes);
                        return ToolResult.Ok("Отчёт агента: учёл вводную — " + string.Join("; ", notes));
                    }
                }

                return ToolResult.Ok("Отчёт агента: диск в порядке.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                WasInterrupted = true;
                return ToolResult.Ok("Агент остановлен по просьбе пользователя.");
            }
            finally
            {
                if (entry is not null)
                {
                    agents!.Unregister(entry.Id);
                }
            }
        }
    }

    /// <summary>Стоит на месте окна: держит очередь и отдаёт её движку.</summary>
    private sealed class QueueingObserver : IChatTurnObserver
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _queued = new();

        public void Enqueue(string text) => _queued.Enqueue(text);

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

        public int Completed { get; private set; }

        public int Continued { get; private set; }

        public void OnAssistantCompleted(ChatDisplayMessage assistant) => Completed++;

        public void OnAssistantContinued(ChatDisplayMessage assistant) => Continued++;

        public void OnAssistantCancelled(ChatDisplayMessage assistant)
        {
        }

        public void OnError(string message)
        {
        }

        public bool TryTakeQueuedMessage(out string text) => _queued.TryDequeue(out text!);
    }
}
