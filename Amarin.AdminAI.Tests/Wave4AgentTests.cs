using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

public sealed class Wave4AgentTests
{
    [Fact]
    public void Forced_agent_model_is_grok_and_has_logo()
    {
        Assert.Equal("grok-4-3", AgentHost.ForcedAgentModelId);
        Assert.Equal("Grok", VeniceModelCatalog.GetLogoResourceKey(AgentHost.ForcedAgentModelId));
        Assert.Equal("Grok 4.3", VeniceModelCatalog.GetDisplayName(AgentHost.ForcedAgentModelId));
    }

    [Fact]
    public void Slot_limiter_accepts_four_and_rejects_fifth()
    {
        var limiter = new AgentSlotLimiter();
        Assert.True(limiter.TryEnter());
        Assert.True(limiter.TryEnter());
        Assert.True(limiter.TryEnter());
        Assert.True(limiter.TryEnter());
        Assert.False(limiter.TryEnter());
        Assert.Equal(4, limiter.Active);
        limiter.Exit();
        Assert.True(limiter.TryEnter());
    }

    [Fact]
    public async Task Fifth_init_agent_fails_while_four_run()
    {
        var limiter = new AgentSlotLimiter();
        var gate = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var host = new FakeAgentHost(_ =>
        {
            Interlocked.Increment(ref started);
            return gate.Task;
        });
        var tool = new InitAgentTool(limiter, host);
        var args = JsonSchema.Parse("""{"prompt":"check network"}""");

        var running = Enumerable.Range(0, 4)
            .Select(_ => tool.ExecuteAsync(args.Clone(), CancellationToken.None))
            .ToArray();

        var waitUntil = DateTime.UtcNow.AddSeconds(2);
        while (Volatile.Read(ref started) < 4 && DateTime.UtcNow < waitUntil)
        {
            await Task.Delay(10);
        }

        Assert.Equal(4, started);
        var fifth = await tool.ExecuteAsync(args.Clone(), CancellationToken.None);
        Assert.False(fifth.Success);
        Assert.Contains("4 агента", fifth.Output, StringComparison.Ordinal);

        gate.SetResult(ToolResult.Ok("done"));
        var results = await Task.WhenAll(running);
        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(0, limiter.Active);
    }

    [Fact]
    public async Task Confirmation_queue_always_approve_does_not_block()
    {
        var settings = new AppSettings { ApprovalMode = ApprovalMode.AlwaysApprove };
        var queue = new ConfirmationQueue(() => settings);
        var info = DangerousActionGuard.DescribeDetailed(
            "filesystem",
            JsonSchema.Parse("""{"action":"write","path":"C:\\temp\\a.txt"}"""));
        var shown = 0;
        queue.Changed += () => shown++;
        var approved = await queue.ConfirmAsync("Агент Grok 4.6", info);
        Assert.True(approved);
        Assert.Equal(0, shown);
        Assert.False(queue.TryPeek(out _));
    }

    [Fact]
    public async Task Confirmation_queue_shows_second_after_first_completes()
    {
        var settings = new AppSettings { ApprovalMode = ApprovalMode.Normal };
        var queue = new ConfirmationQueue(() => settings);
        var info = DangerousActionGuard.DescribeDetailed(
            "filesystem",
            JsonSchema.Parse("""{"action":"write","path":"C:\\temp\\a.txt"}"""));

        var first = queue.ConfirmAsync("Агент 1", info);
        var second = queue.ConfirmAsync("Агент 2", info);
        await Task.Delay(30);

        Assert.True(queue.TryPeek(out var peek));
        Assert.Equal("Агент 1", peek.AgentLabel);

        queue.CompleteCurrent(true);
        Assert.True(await first);

        await Task.Delay(30);
        Assert.True(queue.TryPeek(out peek));
        Assert.Equal("Агент 2", peek.AgentLabel);
        queue.CompleteCurrent(false);
        Assert.False(await second);
        Assert.False(queue.TryPeek(out _));
    }

    [Fact]
    public async Task Init_agent_rejects_a_call_without_a_task()
    {
        var tool = new InitAgentTool(new AgentSlotLimiter(), new FakeAgentHost(_ => Task.FromResult(ToolResult.Ok("x"))));

        Assert.False((await tool.ExecuteAsync(JsonSchema.Parse("""{"notes":"побыстрее"}"""))).Success);
        Assert.False((await tool.ExecuteAsync(JsonSchema.Parse("""{"prompt":"   "}"""))).Success);
    }

    private sealed class FakeAgentHost : IAgentHost
    {
        private readonly Func<string, Task<ToolResult>> _run;

        public FakeAgentHost(Func<string, Task<ToolResult>> run) => _run = run;

        public Task<ToolResult> RunAsync(string prompt, string? notes, CancellationToken cancellationToken) =>
            _run(prompt);
    }
}
