using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Отложенная задача агенту продолжается с инструментами после первого агента. С отчётом без
/// инструментов модель чата писала «запускаю агента ещё раз» и не могла его позвать: человека
/// рядом нет, и задача так и оставалась несделанной.
/// </summary>
public sealed class DeferredAgentLoopTests
{
    [Fact]
    public async Task After_the_first_agent_the_chat_of_a_task_for_later_can_call_the_agent_again()
    {
        var requests = 0;
        var (engine, probe) = Engine(() => ++requests == 1
            ? SseToolCall(InitAgentTool.ToolName, "{\"prompt\":\"again\"}")
            : Sse("done"));
        var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
        var task = new DeferredTask { Id = "t1", Title = "t", Kind = DeferredKind.Agent, Text = "task", CreatedUtc = DateTime.UtcNow };

        var outcome = await engine.RunDeferredAgentAsync(session, task, late: null, new SilentObserver(), CancellationToken.None);

        Assert.Equal(2, probe.Calls);
        // Человека нет: ни первый, ни второй агент не ждёт одобрения плана.
        Assert.Equal([false, false], probe.PlanAllowed);
        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("done", session.Messages.Last(message => message.Role == "assistant").Text);
    }

    [Fact]
    public async Task The_slash_command_still_ends_with_one_report_and_runs_nothing_more()
    {
        // Тот же ответ модели, что выше, — но у /agent отчёт идёт без инструментов: человек рядом и
        // попросит сам.
        var (engine, probe) = Engine(() => SseToolCall(InitAgentTool.ToolName, "{\"prompt\":\"again\"}"));

        await engine.RunAgentCommandAsync(
            new ChatSession { Id = "s", SelectedModelId = "grok-4-6" }, "/agent task", "task", "", new SilentObserver(), CancellationToken.None);

        Assert.Equal(1, probe.Calls);
    }

    private static (ChatEngine Engine, AgentProbe Probe) Engine(Func<HttpResponseMessage> reply)
    {
        var http = new HttpClient(new ScriptedHandler(reply)) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "m", MaxToolRounds = 4 };
        var probe = new AgentProbe();
        return (new ChatEngine(new VeniceClient(http, options), options, AppSettings.CreateDefault, new ToolRegistry([probe])), probe);
    }

    private static HttpResponseMessage Sse(string text) => Stream(
        "data: {\"choices\":[{\"delta\":{\"content\":" + JsonSerializer.Serialize(text) + "}}]}\n" +
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n" +
        "data: [DONE]\n");

    private static HttpResponseMessage SseToolCall(string tool, string arguments) => Stream(
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c" + Guid.NewGuid().ToString("N")[..6] + "\"," +
        "\"function\":{\"name\":\"" + tool + "\",\"arguments\":" + JsonSerializer.Serialize(arguments) + "}}]}}]}\n" +
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n" +
        "data: [DONE]\n");

    private static HttpResponseMessage Stream(string payload) =>
        new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "text/event-stream") };

    private sealed class ScriptedHandler(Func<HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply());
    }

    /// <summary>Встаёт на место <c>init_agent</c>: сколько раз позвали и можно ли было начать с плана.</summary>
    private sealed class AgentProbe : ITool
    {
        public int Calls => PlanAllowed.Count;

        public List<bool> PlanAllowed { get; } = [];

        public string Name => InitAgentTool.ToolName;

        public string Description => "probe";

        public JsonElement ParametersSchema => JsonSchema.Parse("""{"type":"object","properties":{}}""");

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            lock (PlanAllowed)
            {
                PlanAllowed.Add(AgentRunScope.Current?.PlanAllowed != false);
            }

            return Task.FromResult(ToolResult.Ok("Agent report: not finished."));
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
