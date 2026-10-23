using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Разговор чата с прошлыми агентами (1.37.0): сколько он живёт, где лежит и как не перепутать
/// агентов разных запусков.
/// </summary>
public sealed class AgentMemoryTests
{
    [Fact]
    public void Until_the_answer_ends_every_answer_gets_its_own_agents()
    {
        var book = new AgentMemoryBook();
        var chat = new ChatSession { Id = "chat" };

        var first = book.For(chat, AgentMemory.Answer);
        first.Keep(Conversation("a1"));

        Assert.Null(book.For(chat, AgentMemory.Answer).Find("a1"));
    }

    [Fact]
    public void While_the_app_is_open_a_later_answer_sees_the_earlier_agents()
    {
        var book = new AgentMemoryBook();
        book.For(new ChatSession { Id = "chat" }, AgentMemory.Session).Keep(Conversation("a1"));

        // Чат открыли заново — с диска пришёл новый объект, а агенты остались.
        Assert.NotNull(book.For(new ChatSession { Id = "chat" }, AgentMemory.Session).Find("a1"));
        Assert.Null(book.For(new ChatSession { Id = "other" }, AgentMemory.Session).Find("a1"));
    }

    [Fact]
    public void Saved_in_the_chat_the_agents_go_to_its_file_and_come_back_after_a_restart()
    {
        var chat = new ChatSession { Id = "chat" };
        new AgentMemoryBook().For(chat, AgentMemory.Chat).Keep(Conversation("a3"));

        var stored = Assert.Single(chat.Agents!);
        Assert.Equal("a3", stored.Id);
        Assert.Equal("task", stored.Prompt);

        // Перезапуск: книга новая, чат прочитан с диска.
        var json = JsonSerializer.Serialize(chat, AppJson.Options);
        var reread = JsonSerializer.Deserialize<ChatSession>(json, AppJson.Options)!;
        var restored = new AgentMemoryBook().For(reread, AgentMemory.Chat).Find("a3");
        Assert.NotNull(restored);
        Assert.Equal("lite", restored.Tier);
        Assert.Single(restored.History);
    }

    [Fact]
    public void Leaving_the_saved_mode_takes_the_agents_off_the_chat_file()
    {
        var chat = new ChatSession { Id = "chat" };
        var book = new AgentMemoryBook();
        book.For(chat, AgentMemory.Chat).Keep(Conversation("a1"));
        Assert.NotNull(chat.Agents);

        book.For(chat, AgentMemory.Session);

        Assert.Null(chat.Agents);
    }

    [Fact]
    public void The_chat_file_keeps_only_the_latest_agents()
    {
        var chat = new ChatSession { Id = "chat" };
        var agents = new AgentMemoryBook().For(chat, AgentMemory.Chat);
        for (var i = 1; i <= AgentMemoryBook.Limit + 5; i++)
        {
            var conversation = Conversation("a" + i);
            conversation.UpdatedAt = new DateTime(2026, 1, 1).AddMinutes(i);
            agents.Keep(conversation);
        }

        Assert.Equal(AgentMemoryBook.Limit, chat.Agents!.Count);
        Assert.DoesNotContain(chat.Agents, agent => agent.Id == "a1");
        Assert.Contains(chat.Agents, agent => agent.Id == "a" + (AgentMemoryBook.Limit + 5));
    }

    /// <summary>Счётчик реестра на каждом запуске идёт с a1, а агенты из файла помнят свои номера.</summary>
    [Fact]
    public void A_new_agent_never_takes_the_id_of_an_agent_the_chat_remembers()
    {
        var agents = new AgentConversations();
        agents.Keep(Conversation("a1"));
        agents.Keep(Conversation("a2"));
        var registry = new AgentRegistry();

        Assert.Equal("a3", agents.Allocate(registry.NextId));
    }

    [Fact]
    public void Two_chats_may_each_run_an_agent_with_the_same_id()
    {
        var registry = new AgentRegistry();
        using var one = new CancellationTokenSource();
        using var two = new CancellationTokenSource();

        var first = registry.Register("chat-1", "task", "lite", "model", one, "a1");
        var second = registry.Register("chat-2", "task", "lite", "model", two, "a1");

        Assert.Same(first, Assert.Single(registry.ListFor("chat-1")));
        Assert.Same(second, Assert.Single(registry.ListFor("chat-2")));
        registry.Unregister(first);
        Assert.Empty(registry.ListFor("chat-1"));
        Assert.Single(registry.ListFor("chat-2"));
    }

    [Fact]
    public async Task An_agent_of_an_earlier_answer_answers_when_the_chat_keeps_its_agents()
    {
        var bodies = new List<string>();
        var settings = Settings(AgentMemory.Session);
        var host = Host(settings, [Final("first-report"), Final("second-report")], bodies);
        var agents = new AgentConversations();

        await InChat(agents, () => host.RunAsync("task-text", null, CancellationToken.None));
        var result = await InChat(agents, () => host.ContinueAsync("a1", "hello", CancellationToken.None));

        Assert.True(result.Success, result.Output);
        Assert.Contains("second-report", result.Output, StringComparison.Ordinal);
        Assert.Equal(2, bodies.Count);
    }

    [Theory]
    [InlineData(AgentMemory.Answer, "этом ответе")]
    [InlineData(AgentMemory.Session, "не сохраняются")]
    [InlineData(AgentMemory.Chat, "последних разговоров")]
    public async Task An_unknown_agent_is_explained_by_the_lifetime_the_person_chose(AgentMemory mode, string expected)
    {
        var host = Host(Settings(mode), [], []);

        var result = await InChat(new AgentConversations(), () => host.ContinueAsync("a9", "hello", CancellationToken.None));

        Assert.False(result.Success);
        Assert.Contains(expected, result.Output, StringComparison.Ordinal);
        Assert.Contains("init_agent", result.Output, StringComparison.Ordinal);
    }

    /// <summary>В режиме «Сохранять в чате» разговоры сохраняются — отказ не должен говорить обратного.</summary>
    [Fact]
    public async Task A_chat_that_keeps_its_agents_is_never_told_they_are_not_kept()
    {
        var host = Host(Settings(AgentMemory.Chat), [], []);

        var result = await InChat(new AgentConversations(), () => host.ContinueAsync("a9", "hello", CancellationToken.None));

        Assert.DoesNotContain("не сохраняются", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_chat_is_told_how_long_its_agents_live()
    {
        var answer = ChatEngine.AgentLifetimeRules(AgentMemory.Answer);
        var session = ChatEngine.AgentLifetimeRules(AgentMemory.Session);
        var chat = ChatEngine.AgentLifetimeRules(AgentMemory.Chat);

        Assert.Contains("until your answer", answer, StringComparison.Ordinal);
        Assert.Contains("until the app closes", session, StringComparison.Ordinal);
        Assert.Contains("after the app restarts", chat, StringComparison.Ordinal);
        Assert.DoesNotContain(new MessageAgentTool(new AgentSlotLimiter(), null!).Description, "until your answer", StringComparison.Ordinal);
    }

    // ───────────────────────── обвязка ─────────────────────────

    private static AgentConversation Conversation(string id) => new()
    {
        Id = id,
        Prompt = "task",
        Tier = "lite",
        History = [new ChatMessage { Role = "user", Content = ChatContent.Text("task") }]
    };

    private static AppSettings Settings(AgentMemory mode)
    {
        var settings = AppSettings.CreateDefault();
        settings.AgentFastModelId = "agent-fast";
        settings.AgentLiteModelId = "agent-lite";
        settings.AgentHeavyModelId = "agent-heavy";
        settings.BypassModelId = "agent-bypass";
        settings.SynGuardEnabled = false;
        settings.AgentMemory = mode;
        return settings;
    }

    private static AgentHost Host(AppSettings settings, string[] replies, List<string> bodies)
    {
        var handler = new Replies(replies, bodies);
        return new AgentHost(
            new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "agent-lite", MaxToolRounds = 3 },
            new HttpClient { BaseAddress = new Uri("https://example.invalid/") },
            () => settings,
            new ConfirmationQueue(() => settings),
            new AgentRegistry())
        {
            Route = (_, _, _) => Task.FromResult(new AgentTierDecision("lite", null)),
            Http = _ => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") }
        };
    }

    private static async Task<ToolResult> InChat(AgentConversations agents, Func<Task<ToolResult>> run)
    {
        using (AgentRunScope.Push(new AgentRunContext
        {
            Call = new ToolCallRecord { Id = Guid.NewGuid().ToString("N"), Name = InitAgentTool.ToolName },
            Assistant = new ChatDisplayMessage { Role = "assistant" },
            Observer = SilentTurnObserver.Instance,
            SessionId = "chat",
            Conversations = agents
        }))
        {
            return await run();
        }
    }

    private static string Final(string text) =>
        """{"choices":[{"message":{"role":"assistant","content":""" + JsonSerializer.Serialize(text) +
        """},"finish_reason":"stop"}]}""";

    private sealed class Replies(string[] replies, List<string> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var reply = replies[Math.Min(bodies.Count, replies.Length) - 1];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }
}
