using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Прерванный ход и продолжение его с того же места.
/// </summary>
/// <remarks>
/// Прежде единственной кнопкой была «Повторить», а она усекает переписку до последней просьбы
/// человека — вместе с работой инструментов, за которую уже заплачено. Здесь проверяется, что
/// продолжение не выбрасывает ничего: ни блоков инструментов, ни денег, ни времени, — и что
/// стенограмма после обрыва остаётся такой, что её вообще можно продолжить.
/// </remarks>
public sealed class ResumeTurnTests
{
    private static AgentOptions Options() => new()
    {
        ApiKey = "test",
        BaseUrl = "https://api.venice.ai/api/v1",
        Model = "grok-4-6",
        MaxToolRounds = 4
    };

    private static ChatEngine Engine(HttpMessageHandler handler, ITool tool, out HttpClient http)
    {
        var options = Options();
        http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var settings = AppSettings.CreateDefault();
        return new ChatEngine(
            new VeniceClient(http, options), options, () => settings, new ToolRegistry([tool]));
    }

    [Fact]
    public async Task A_turn_cancelled_inside_a_tool_round_leaves_a_transcript_that_can_be_continued()
    {
        // Отмена приходит, пока инструмент работает, — самый неприятный момент: в стенограмме
        // уже лежит assistant с tool_calls, и без ответов на них следующий запрос отвергнет
        // сам API.
        using var cancellation = new CancellationTokenSource();
        var tool = new SlowTool(cancellation);
        var handler = new ScriptedHandler(_ => SseWithToolCall("Смотрю.", "read_file"));

        var engine = Engine(handler, tool, out var http);
        using (http)
        {
            var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
            await engine.RunTurnAsync(
                session, "проверь диск C", new SilentObserver(), cancellation.Token);

            for (var i = 0; i < session.ApiMessages.Count; i++)
            {
                if (session.ApiMessages[i].ToolCalls is { Count: > 0 })
                {
                    Assert.True(
                        i + 1 < session.ApiMessages.Count && session.ApiMessages[i + 1].Role == "tool",
                        "у вызова инструмента нет ответа - продолжить такую стенограмму нельзя");
                }
            }

            var assistant = session.Messages.Last(m => m.Role == "assistant");
            Assert.Equal(AssistantStatus.Cancelled, assistant.Status);
            Assert.Single(assistant.ToolRounds);
        }
    }

    [Fact]
    public async Task Continuing_writes_into_the_same_message_and_keeps_the_tool_work()
    {
        using var cancellation = new CancellationTokenSource();
        var tool = new SlowTool(cancellation);
        var handler = new ScriptedHandler(_ => SseWithToolCall("Смотрю.", "read_file"));

        var engine = Engine(handler, tool, out var http);
        using (http)
        {
            var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
            await engine.RunTurnAsync(
                session, "проверь диск C", new SilentObserver(), cancellation.Token);

            var assistant = session.Messages.Last(m => m.Role == "assistant");
            var before = session.Messages.Count;
            var rounds = assistant.ToolRounds.Count;

            handler.Script = _ => Sse("Диск в порядке.");
            await engine.ResumeAssistantAsync(
                session, assistant, new SilentObserver(), CancellationToken.None);

            Assert.Equal(before, session.Messages.Count);
            Assert.Same(assistant, session.Messages.Last(m => m.Role == "assistant"));
            Assert.Equal(rounds, assistant.ToolRounds.Count);
            Assert.Equal(AssistantStatus.Complete, assistant.Status);
            Assert.Equal("Диск в порядке.", assistant.Text);
        }
    }

    [Fact]
    public async Task Continuing_keeps_the_money_the_interrupted_turn_had_already_spent()
    {
        // ApplyCosts — пересчёт, а не прибавление. Без возврата уже списанного строка «Модель»
        // обнулилась бы, и деньги прерванного хода молча пропали бы из счёта.
        var handler = new ScriptedHandler(_ => Sse("Готово."));
        var engine = Engine(handler, new SilentTool(), out var http);
        using (http)
        {
            var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
            var assistant = new ChatDisplayMessage
            {
                Role = "assistant",
                Id = "a1",
                RequestedModelId = "grok-4-6",
                ResolvedModelId = "grok-4-6",
                Status = AssistantStatus.Cancelled,
                Text = "я начал отвеч",
                Duration = TimeSpan.FromSeconds(42),
                ModelCost = new VeniceCost { Usd = 0.02m, HasData = true },
                Cost = new VeniceCost { Usd = 0.12m, HasData = true },
                ToolRounds =
                [
                    new ToolRound
                    {
                        Calls =
                        [
                            new ToolCallRecord
                            {
                                Id = "c1",
                                Name = "generate_image",
                                Status = ToolCallStatus.Done,
                                Success = true,
                                Cost = new VeniceCost { Usd = 0.10m, HasData = true }
                            }
                        ]
                    }
                ]
            };
            session.Messages.Add(assistant);

            await engine.ResumeAssistantAsync(
                session, assistant, new SilentObserver(), CancellationToken.None);

            Assert.True(assistant.ModelCost!.Usd >= 0.02m, "разговор прерванного хода пропал из счёта");
            Assert.True(assistant.Cost!.Usd >= 0.12m, "итог после продолжения меньше, чем был до него");
            Assert.True(
                assistant.Duration >= TimeSpan.FromSeconds(42),
                "часы ответа обнулились вместе с продолжением");
        }
    }

    [Fact]
    public async Task Cancelling_stops_the_spinner_inside_the_agent_too()
    {
        // Раунды вложенного агента прежде не обходились вовсе: агента обрывали на середине его
        // собственного инструмента, и его строка оставалась крутиться в «выполняется» навсегда —
        // ход закончился, а дописать её было уже некому.
        using var cancellation = new CancellationTokenSource();
        var tool = new NestedAgentTool(cancellation);
        var handler = new ScriptedHandler(_ => SseWithToolCall("Запускаю агента.", "read_file"));

        var engine = Engine(handler, tool, out var http);
        using (http)
        {
            var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
            await engine.RunTurnAsync(
                session, "почини звук", new SilentObserver(), cancellation.Token);

            var assistant = session.Messages.Last(m => m.Role == "assistant");
            Assert.Equal(AssistantStatus.Cancelled, assistant.Status);

            var nested = assistant.ToolRounds
                .SelectMany(round => round.Calls)
                .Select(call => call.NestedAgent)
                .OfType<AgentRunRecord>()
                .SelectMany(agent => agent.ToolRounds)
                .SelectMany(round => round.Calls)
                .ToList();

            Assert.NotEmpty(nested);
            Assert.DoesNotContain(
                nested,
                call => call.Status is ToolCallStatus.Pending or ToolCallStatus.Running);
        }
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
        public Func<HttpRequestMessage, HttpResponseMessage> Script { get; set; } = script;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(Script(request));
    }

    /// <summary>Отменяет ход изнутри собственного вызова — так же, как кнопка «стоп».</summary>
    private sealed class SlowTool(CancellationTokenSource cancellation) : ITool
    {
        public string Name => "read_file";

        public string Description => "stub";

        public JsonElement ParametersSchema =>
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        public Task<ToolResult> ExecuteAsync(
            JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ToolResult.Ok("не дойдёт"));
        }
    }

    private sealed class SilentTool : ITool
    {
        public string Name => "read_file";

        public string Description => "stub";

        public JsonElement ParametersSchema =>
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        public Task<ToolResult> ExecuteAsync(
            JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Ok("содержимое"));
    }

    /// <summary>
    /// Стоит вместо <c>init_agent</c>: вешает на свою строку запись агента с начатым, но не
    /// законченным вызовом — и обрывает ход ровно в этот момент.
    /// </summary>
    private sealed class NestedAgentTool(CancellationTokenSource cancellation) : ITool
    {
        public string Name => "read_file";

        public string Description => "stub";

        public JsonElement ParametersSchema =>
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        public Task<ToolResult> ExecuteAsync(
            JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            AgentRunScope.Current!.Call.NestedAgent = new AgentRunRecord
            {
                ModelId = "grok-4-6",
                DisplayName = "Агент grok-4-6",
                Status = AgentRunStatus.Running,
                ToolRounds =
                [
                    new ToolRound
                    {
                        Calls =
                        [
                            new ToolCallRecord
                            {
                                Id = "n1",
                                Name = "run_powershell",
                                Status = ToolCallStatus.Running
                            }
                        ]
                    }
                ]
            };

            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ToolResult.Ok("не дойдёт"));
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

        public void OnError(string message)
        {
        }
    }
}
