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
        var text = SynGuard.BuildUserMessage(
        [
            new SynGuardCall("run_powershell", """{"command":"Get-Process"}"""),
            new SynGuardCall("scheduled_task", """{"action":"create"}""")
        ]);

        Assert.Contains("1. run_powershell", text, StringComparison.Ordinal);
        Assert.Contains("2. scheduled_task", text, StringComparison.Ordinal);
        Assert.Contains("Get-Process", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_argument_is_clipped_so_the_check_does_not_pay_for_the_whole_file()
    {
        var huge = new string('x', 20_000);
        var text = SynGuard.BuildUserMessage([new SynGuardCall("write_file", huge)]);

        Assert.True(text.Length < 6_000, $"запрос вышел на {text.Length} символов");
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

    // ───────────────────────── что делает агент ─────────────────────────

    [Fact]
    public async Task A_flagged_call_never_runs_while_its_neighbour_does()
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
            new SessionUndoTracker())
        {
            // Первый вызов опасен, второй — нет.
            Guard = (_, _) => Task.FromResult(new SynGuardReport([false, true], null))
        };

        await agent.RunAsync("наведи порядок");

        Assert.Equal(["read_file"], ran);
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
            new CapturingAgentUi(replies),
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
            CancellationToken cancellationToken = default) => Task.FromResult(true);
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
