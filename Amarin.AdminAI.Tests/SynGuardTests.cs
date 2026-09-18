using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// SynGuard: модель-защитник, читающая то, что агент собирается запустить.
/// </summary>
/// <remarks>
/// <see cref="DangerousActionGuard"/> судит по тому, чего команда касается, — и потому не видит
/// намерения: скрипт, собирающий пароли и отправляющий их наружу, не содержит ни одного
/// командлета из его регулярок. Здесь проверяется то, что нельзя увидеть глазами: что
/// помеченный вызов не исполнился, что соседние исполнились, и что непрочитанный ответ не
/// превращается в запрет.
/// </remarks>
public sealed class SynGuardTests
{
    private static AgentOptions Options() => new()
    {
        ApiKey = "test",
        BaseUrl = "https://api.venice.ai/api/v1",
        Model = "grok-4-6",
        MaxToolRounds = 3
    };

    // ───────────────────────── запрос и разбор ─────────────────────────

    [Fact]
    public void The_whole_round_goes_into_one_request_numbered_from_one()
    {
        // Раунд целиком — ради цены и ради связок: «записать скрипт» и «поставить его в
        // планировщик» опасны парой, а поодиночке нет.
        var text = SynGuard.BuildUserMessage(new SynGuardRequest(
            "",
            [
                new SynGuardCall("run_powershell", """{"command":"Get-Process"}"""),
                new SynGuardCall("scheduled_task", """{"action":"create"}""")
            ]));

        Assert.Contains("1. run_powershell", text, StringComparison.Ordinal);
        Assert.Contains("2. scheduled_task", text, StringComparison.Ordinal);
        Assert.Contains("Get-Process", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_task_goes_into_the_request_ahead_of_the_calls()
    {
        // Без задания защитник судил по одним командам и называл атакой саму задачу
        // планировщика — то есть единственный способ сделать напоминание, которое просили.
        var text = SynGuard.BuildUserMessage(new SynGuardRequest(
            "Поставь напоминание на 17:02",
            [new SynGuardCall("scheduled_task", """{"action":"create"}""")]));

        var task = text.IndexOf("Поставь напоминание", StringComparison.Ordinal);
        var call = text.IndexOf("scheduled_task", StringComparison.Ordinal);
        Assert.True(task >= 0, "задания в запросе нет");
        Assert.True(task < call, "задание оказалось после вызовов");
    }

    [Fact]
    public void A_long_argument_is_clipped_so_the_check_does_not_pay_for_the_whole_file()
    {
        var huge = new string('x', 20_000);
        var text = SynGuard.BuildUserMessage(new SynGuardRequest("", [new SynGuardCall("write_file", huge)]));

        Assert.True(text.Length < 6_000, $"запрос вышел на {text.Length} символов");
    }

    [Fact]
    public void A_long_task_is_clipped_too()
    {
        // Промпт агента бывает на страницу; проверке от него нужна цель, а не подробности.
        var text = SynGuard.BuildUserMessage(new SynGuardRequest(
            new string('t', 20_000),
            [new SynGuardCall("read_file", "{}")]));

        Assert.True(text.Length < 3_000, $"запрос вышел на {text.Length} символов");
    }

    [Theory]
    [InlineData("1: safe\n2: dangerous", new[] { true, false })]
    [InlineData("1. SAFE\n2. Dangerous", new[] { true, false })]
    [InlineData("**1:** dangerous\n**2:** safe", new[] { false, true })]
    public void The_verdicts_are_read_per_call(string reply, bool[] expected) =>
        Assert.Equal(expected, SynGuard.ParseReport(reply, expected.Length));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Я не могу оценить эти команды без контекста.")]
    [InlineData("3: dangerous")]
    public void Anything_unreadable_counts_as_safe(string? reply)
    {
        // Не небрежность, а решение: непрочитанный ответ значит, что проверки не было, а
        // «проверки не было» не может значить «не работай».
        Assert.Equal([true, true], SynGuard.ParseReport(reply, 2));
    }

    [Fact]
    public void The_model_falls_back_to_the_shipped_one_when_the_setting_is_empty()
    {
        var settings = AppSettings.CreateDefault();
        Assert.Equal(SynGuard.FallbackModelId, SynGuard.ResolveModel(settings));

        settings.SynGuardModelId = "  ";
        Assert.Equal(SynGuard.FallbackModelId, SynGuard.ResolveModel(settings));

        settings.SynGuardModelId = "some-other-model";
        Assert.Equal("some-other-model", SynGuard.ResolveModel(settings));
    }

    [Fact]
    public void The_prompt_states_a_rule_rather_than_walking_through_a_case()
    {
        // Памятка проекта: разобранный случай тянет модель к нему и к похожим формулировкам
        // вместо того, о чём спросили на самом деле.
        foreach (var banned in new[] { "For example", "for example", "e.g.", "Например" })
        {
            Assert.DoesNotContain(banned, SynGuard.SystemPrompt, StringComparison.Ordinal);
        }

        // И граница, без которой защитник переловит всю обычную работу агента.
        Assert.Contains("not dangerous", SynGuard.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_dangerous_list_names_the_machine_working_for_someone_else_and_nothing_wider()
    {
        // Вредонос здесь — майнер и удалённое управление, отправка данных наружу, кража,
        // вымогательство и нечитаемый код. Всё, что описывало способ, а не результат, из
        // списка убрано: по способу защитник и ловил обычную работу.
        var prompt = SynGuard.SystemPrompt;
        var boundary = prompt.IndexOf("safe is everything else", StringComparison.Ordinal);
        Assert.True(boundary > 0, "в промпте не стало абзаца про обычную работу");

        foreach (var effect in new[] { "mining", "remote control", "sends them off this machine", "encoded" })
        {
            var at = prompt.IndexOf(effect, StringComparison.OrdinalIgnoreCase);
            Assert.True(at > 0 && at < boundary, $"«{effect}» пропал из списка опасного");
        }

        // А установка программы — обычная работа: под запретом «притащил из сети и запустил»
        // оказывалась ровно она.
        var install = prompt.IndexOf("installing and removing software", StringComparison.Ordinal);
        Assert.True(install > boundary, "установка софта снова числится опасной");
    }

    [Fact]
    public void Keeping_something_running_afterwards_is_ordinary_ground_and_not_an_effect_to_catch()
    {
        // Ровно тот случай, на котором защитник встал: «напоминай каждые 40 минут» делается
        // задачей планировщика и больше никак. Прежний промпт держал закрепление в системе в
        // списке опасного, и любое напоминание по определению выходило атакой.
        var prompt = SynGuard.SystemPrompt;
        var boundary = prompt.IndexOf("ordinary ground", StringComparison.Ordinal);
        Assert.True(boundary > 0, "в промпте не стало абзаца про обычную работу");

        foreach (var ordinary in new[] { "scheduled", "startup", "background" })
        {
            var at = prompt.IndexOf(ordinary, StringComparison.OrdinalIgnoreCase);
            Assert.True(at > 0, $"«{ordinary}» в промпте не упомянут вовсе");
            Assert.True(
                at > prompt.IndexOf("safe is everything else", StringComparison.Ordinal),
                $"«{ordinary}» снова стоит в списке опасного, а не в границе");
        }
    }

    // ───────────────────────── что делает агент ─────────────────────────

    [Fact]
    public async Task A_flagged_call_the_owner_refused_never_runs_while_its_neighbour_does()
    {
        var ran = new List<string>();
        var ui = new SilentAgentUi { Answer = _ => false };

        await RunRoundAsync(ui, ran, new SynGuardReport([false, true], null));

        Assert.Equal(["read_file"], ran);

        // Спросили ровно про помеченный вызов, и спросили один раз.
        var asked = Assert.Single(ui.Asked);
        Assert.Equal("run_powershell", asked.ToolName);
    }

    [Fact]
    public async Task A_flagged_call_the_owner_allowed_runs()
    {
        // Защитник ошибается: задачу планировщика он читает как закрепление в системе. Без
        // этого пути его ошибка — тупик, из которого человек не выведет работу иначе как
        // выключив защиту целиком.
        var ran = new List<string>();
        var ui = new SilentAgentUi();

        await RunRoundAsync(ui, ran, new SynGuardReport([false, true], null));

        Assert.Equal(["run_powershell", "read_file"], ran);
    }

    [Fact]
    public async Task The_question_shows_the_whole_call_and_is_asked_past_the_approve_everything_mode()
    {
        var ui = new SilentAgentUi { Answer = _ => false };
        await RunRoundAsync(ui, [], new SynGuardReport([false, true], null));

        var asked = Assert.Single(ui.Asked);
        Assert.True(asked.AlwaysAsk, "вопрос защитника обходится режимом «подтверждать всё»");
        Assert.Equal(DangerousRiskLevel.Critical, asked.RiskLevel);
        Assert.Contains("{}", asked.CodeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_allowed_call_is_not_asked_about_a_second_time()
    {
        // write_file подтверждается всегда. Не пропусти мы обычный вопрос, человек ответил бы
        // на один вызов дважды подряд, причём второй вопрос слабее первого: в первом ему
        // показали команду целиком.
        var ran = new List<string>();
        var ui = new SilentAgentUi();
        var round = 0;
        var handler = new ScriptedHandler(() => Interlocked.Increment(ref round) == 1
            ? OneWriteCall()
            : """{"choices":[{"message":{"role":"assistant","content":"Готово."}}]}""");

        var options = Options();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };

        var agent = new Agent(
            new VeniceClient(http, options),
            new ToolRegistry([new RecordingTool("write_file", ran)]),
            options,
            ui,
            new SessionUndoTracker())
        {
            Guard = (_, _) => Task.FromResult(new SynGuardReport([false], null))
        };

        await agent.RunAsync("запиши файл");

        Assert.Equal(["write_file"], ran);
        var asked = Assert.Single(ui.Asked);
        Assert.True(asked.AlwaysAsk, "человека спросили обычным вопросом вместо вопроса защитника");
    }

    [Fact]
    public async Task The_task_the_agent_was_given_reaches_the_check()
    {
        SynGuardRequest? seen = null;
        var round = 0;
        var handler = new ScriptedHandler(() => Interlocked.Increment(ref round) == 1
            ? TwoCalls()
            : """{"choices":[{"message":{"role":"assistant","content":"Готово."}}]}""");

        var options = Options();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };

        var agent = new Agent(
            new VeniceClient(http, options),
            new ToolRegistry([new RecordingTool("run_powershell", []), new RecordingTool("read_file", [])]),
            options,
            new SilentAgentUi(),
            new SessionUndoTracker())
        {
            Guard = (request, _) =>
            {
                seen = request;
                return Task.FromResult(new SynGuardReport([true, true], null));
            }
        };

        await agent.RunAsync("Поставь напоминание на 17:02");

        Assert.Equal("Поставь напоминание на 17:02", seen!.Value.Task);
    }

    [Fact]
    public async Task The_refusal_tells_the_agent_not_to_work_around_it()
    {
        var replies = new List<string>();
        var round = 0;
        var handler = new ScriptedHandler(() => Interlocked.Increment(ref round) == 1
            ? TwoCalls()
            : """{"choices":[{"message":{"role":"assistant","content":"Готово."}}]}""");

        var options = Options();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };

        var agent = new Agent(
            new VeniceClient(http, options),
            new ToolRegistry([new RecordingTool("run_powershell", []), new RecordingTool("read_file", [])]),
            options,
            new CapturingAgentUi(replies) { Answer = _ => false },
            new SessionUndoTracker())
        {
            Guard = (_, _) => Task.FromResult(new SynGuardReport([false, true], null))
        };

        await agent.RunAsync("наведи порядок");

        var refusal = Assert.Single(replies, text => text.StartsWith(SynGuard.BlockedMarker, StringComparison.Ordinal));
        Assert.Contains("Не повторяй вызов", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_the_guard_switched_off_nothing_is_asked_and_everything_runs()
    {
        var ran = new List<string>();
        var round = 0;
        var handler = new ScriptedHandler(() => Interlocked.Increment(ref round) == 1
            ? TwoCalls()
            : """{"choices":[{"message":{"role":"assistant","content":"Готово."}}]}""");

        var options = Options();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };

        var agent = new Agent(
            new VeniceClient(http, options),
            new ToolRegistry([new RecordingTool("run_powershell", ran), new RecordingTool("read_file", ran)]),
            options,
            new SilentAgentUi(),
            new SessionUndoTracker());

        await agent.RunAsync("наведи порядок");

        Assert.Equal(2, ran.Count);
    }

    [Fact]
    public async Task What_the_check_cost_lands_on_the_message_and_not_on_the_tool_row()
    {
        // Одна строка «Защита» на сообщение, а не по строке на каждый раунд агента: человеку
        // важно, сколько стоила защита, а не сколько раз она срабатывала.
        var round = 0;
        var handler = new ScriptedHandler(() => Interlocked.Increment(ref round) == 1
            ? TwoCalls()
            : """{"choices":[{"message":{"role":"assistant","content":"Готово."}}]}""");

        var options = Options();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };

        var call = new ToolCallRecord { Id = "c1", Name = "init_agent" };
        var assistant = new ChatDisplayMessage { Role = "assistant" };
        var agent = new Agent(
            new VeniceClient(http, options),
            new ToolRegistry([new RecordingTool("run_powershell", []), new RecordingTool("read_file", [])]),
            options,
            new SilentAgentUi(),
            new SessionUndoTracker())
        {
            Guard = (_, _) => Task.FromResult(new SynGuardReport(
                [true, true],
                new VeniceCost { Usd = 0.0002m, HasData = true }))
        };

        using (AgentRunScope.Push(new AgentRunContext
        {
            Call = call,
            Assistant = assistant,
            Observer = new SilentObserver(),
            SessionId = "s"
        }))
        {
            await agent.RunAsync("наведи порядок");
        }

        Assert.Equal(0.0002m, assistant.GuardCost!.Usd);
        Assert.Null(call.Cost);
    }

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>Раунд из run_powershell и read_file с готовым вердиктом защитника.</summary>
    private static async Task RunRoundAsync(SilentAgentUi ui, List<string> ran, SynGuardReport report)
    {
        var round = 0;
        var handler = new ScriptedHandler(() => Interlocked.Increment(ref round) == 1
            ? TwoCalls()
            : """{"choices":[{"message":{"role":"assistant","content":"Готово."}}]}""");

        var options = Options();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };

        var agent = new Agent(
            new VeniceClient(http, options),
            new ToolRegistry([new RecordingTool("run_powershell", ran), new RecordingTool("read_file", ran)]),
            options,
            ui,
            new SessionUndoTracker())
        {
            Guard = (_, _) => Task.FromResult(report)
        };

        await agent.RunAsync("наведи порядок");
    }

    private static string OneWriteCall() =>
        """
        {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
          {"id":"c1","type":"function","function":{"name":"write_file","arguments":"{}"}}]},
          "finish_reason":"tool_calls"}]}
        """;

    private static string TwoCalls() =>
        """
        {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
          {"id":"c1","type":"function","function":{"name":"run_powershell","arguments":"{}"}},
          {"id":"c2","type":"function","function":{"name":"read_file","arguments":"{}"}}]},
          "finish_reason":"tool_calls"}]}
        """;

    private sealed class ScriptedHandler(Func<string> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply(), Encoding.UTF8, "application/json")
            });
    }

    private sealed class RecordingTool(string name, List<string> ran) : ITool
    {
        public string Name => name;

        public string Description => "stub";

        public JsonElement ParametersSchema =>
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        public Task<ToolResult> ExecuteAsync(
            JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            lock (ran)
            {
                ran.Add(name);
            }

            return Task.FromResult(ToolResult.Ok("готово"));
        }
    }

    private class SilentAgentUi : IAgentUi
    {
        /// <summary>Что человек отвечает на вопрос о подтверждении. По умолчанию — «да».</summary>
        public Func<DangerousActionInfo, bool> Answer { get; init; } = _ => true;

        /// <summary>Вопросы, дошедшие до человека, по порядку.</summary>
        public List<DangerousActionInfo> Asked { get; } = [];

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

        public virtual void ToolResult(string name, ToolResult result)
        {
        }

        public Task<T> RunBusyAsync<T>(
            string message,
            Func<Task<T>> work,
            CancellationToken cancellationToken = default) => work();

        public Task<bool> ConfirmDangerousActionAsync(
            DangerousActionInfo info,
            CancellationToken cancellationToken = default)
        {
            lock (Asked)
            {
                Asked.Add(info);
            }

            return Task.FromResult(Answer(info));
        }
    }

    private sealed class CapturingAgentUi(List<string> replies) : SilentAgentUi
    {
        public override void ToolResult(string name, ToolResult result) => replies.Add(result.Output);
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

        public void OnError(string message)
        {
        }
    }
}
