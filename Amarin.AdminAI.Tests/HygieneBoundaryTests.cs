using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Граница чистки истории: пока кэш провайдера жив, начало запроса не меняется ни на байт.
/// </summary>
/// <remarks>
/// Нарушение не видно глазом: ответы идут, только вся история записывается в кэш заново. На настоящем
/// чате это стоило треть цены сообщения.
/// </remarks>
public sealed class HygieneBoundaryTests
{
    private const string Key = "chat-1";

    private static readonly string Doc = Path.Combine(DownloadPaths.DownloadsDirectory, "план.txt");

    private static readonly string LongWrite = new('x', ContextHygiene.LongArgument + 500);

    private static readonly string LongRead = string.Concat(Enumerable.Repeat("строка файла\n", 100));

    /// <summary>Настоящие часы со сдвигом: ответы движок помечает настоящим временем.</summary>
    private sealed class ShiftedTime : TimeProvider
    {
        public TimeSpan Shift { get; set; }

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Shift;
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Bodies)
            {
                Bodies.Add(body);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n" +
                    "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n" +
                    "data: [DONE]\n",
                    Encoding.UTF8,
                    "text/event-stream")
            };
        }
    }

    private static ChatMessage User(string text) => new() { Role = "user", Content = ChatContent.Text(text) };

    private static ChatMessage Assistant(string text) => new() { Role = "assistant", Content = ChatContent.Text(text) };

    private static ChatMessage Calls(string id, string name, object arguments) => new()
    {
        Role = "assistant",
        ToolCalls = [new ToolCall { Id = id, Function = new FunctionCall { Name = name, Arguments = JsonSerializer.Serialize(arguments) } }]
    };

    private static ChatMessage Result(string id, string name, string text) => new()
    {
        Role = "tool",
        ToolCallId = id,
        Name = name,
        Content = ChatContent.Text(text)
    };

    /// <summary>Чат с работой над файлом: на третьем ходе длинная запись ещё защищена, на четвёртом уже нет.</summary>
    private sealed class Talk
    {
        private readonly Recorder _recorder = new();
        private readonly ChatEngine _engine;

        public Talk()
        {
            var options = new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "grok-4-6", MaxToolRounds = 2 };
            var http = new HttpClient(_recorder) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
            var settings = AppSettings.CreateDefault();
            _engine = new ChatEngine(new VeniceClient(http, options), options, () => settings, new ToolRegistry([]))
            {
                Time = Clock
            };

            Session.ApiMessages.AddRange(
            [
                User("прочитай"),
                Calls("r1", "read_file", new { path = Doc }),
                Result("r1", "read_file", LongRead),
                Assistant("прочитал"),
                User("перепиши"),
                Calls("w1", "write_file", new { path = Doc, content = LongWrite }),
                Result("w1", "write_file", "ok"),
                Assistant("готово")
            ]);

            var earlier = DateTime.Now.AddMinutes(-1);
            Session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "прочитай", CreatedAt = earlier });
            Session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Id = "a1", Text = "прочитал", CreatedAt = earlier, Status = AssistantStatus.Complete });
            Session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u2", Text = "перепиши", CreatedAt = earlier });
            Session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Id = "a2", Text = "готово", CreatedAt = earlier, Status = AssistantStatus.Complete });
        }

        public ShiftedTime Clock { get; } = new();

        public ChatSession Session { get; } = new() { Id = Key, SelectedModelId = "grok-4-6" };

        /// <summary>Новый вопрос и сообщения запроса, с которым он ушёл.</summary>
        public async Task<List<string>> AskAsync(string text)
        {
            Session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = Guid.NewGuid().ToString("N"), Text = text, CreatedAt = DateTime.Now });
            Session.ApiMessages.Add(User(text));
            var before = _recorder.Bodies.Count;

            await _engine.GenerateAssistantAsync(Session, SilentTurnObserver.Instance, CancellationToken.None);

            var body = _recorder.Bodies.Skip(before).First(item => (string?)JsonNode.Parse(item)!["prompt_cache_key"] == Key);
            return [.. JsonNode.Parse(body)!["messages"]!.AsArray().Select(message => message!.ToJsonString())];
        }
    }

    private static bool Carries(List<string> messages, string text) =>
        messages.Any(message => message.Contains(text, StringComparison.Ordinal));

    [Theory]
    [InlineData(null, 0, true)]
    [InlineData(4.9, 3, false)]
    [InlineData(5.0, 0, true)]
    [InlineData(1.0, 4, true)]
    public void The_boundary_moves_when_the_cache_is_gone_or_held_for_too_long(double? minutes, int held, bool moves)
    {
        var pause = minutes is { } value ? TimeSpan.FromMinutes(value) : (TimeSpan?)null;

        Assert.Equal(moves, ContextHygiene.MayMoveBoundary(pause, held));
    }

    /// <summary>Чистка на полной истории сократила бы старый ход и сбила кэш всего, что за ним.</summary>
    [Fact]
    public async Task A_turn_within_the_cache_lifetime_repeats_the_previous_request_byte_for_byte()
    {
        var talk = new Talk();

        var first = await talk.AskAsync("третий");
        var second = await talk.AskAsync("четвёртый");

        Assert.True(Carries(first, LongWrite));
        Assert.Equal(first, second.Take(first.Count));
    }

    /// <summary>
    /// Запись в файл после границы не трогает чтение перед ней, хотя и делает его устаревшим: заглушка
    /// изменила бы начало запроса.
    /// </summary>
    [Fact]
    public void A_later_write_does_not_stub_a_read_before_the_boundary()
    {
        static List<ChatMessage> Outgoing() =>
        [
            new ChatMessage { Role = "system", Content = ChatContent.Text("system") },
            User("один"),
            Calls("r1", "read_file", new { path = Doc }),
            Result("r1", "read_file", LongRead),
            Assistant("прочитал"),
            User("два"),
            Assistant("да"),
            User("три"),
            Assistant("да"),
            User("четыре"),
            Calls("w1", "write_file", new { path = Doc, content = "новый текст" }),
            Result("w1", "write_file", "ok"),
            Assistant("готово"),
            User("пять")
        ];

        var held = Outgoing();
        ContextHygiene.Apply(held, 10);

        var whole = Outgoing();
        ContextHygiene.Apply(whole);

        Assert.Equal(LongRead, ChatContent.ReadText(held[3].Content));
        Assert.NotEqual(LongRead, ChatContent.ReadText(whole[3].Content));
    }

    [Fact]
    public async Task After_the_cache_is_gone_the_old_turns_are_cleaned()
    {
        var talk = new Talk();
        await talk.AskAsync("третий");
        await talk.AskAsync("четвёртый");

        talk.Clock.Shift = ContextHygiene.CacheLifetime + TimeSpan.FromMinutes(1);
        var cold = await talk.AskAsync("пятый");

        Assert.False(Carries(cold, LongWrite));
    }

    /// <summary>В быстром разговоре старые версии файла не остаются навсегда.</summary>
    [Fact]
    public async Task A_fast_conversation_is_cleaned_after_a_few_turns_anyway()
    {
        var talk = new Talk();
        await talk.AskAsync("начало");

        for (var turn = 1; turn < ContextHygiene.MaxHeldTurns; turn++)
        {
            Assert.True(Carries(await talk.AskAsync("ещё"), LongWrite));
        }

        Assert.False(Carries(await talk.AskAsync("и ещё"), LongWrite));
    }

    /// <summary>Удаление сообщений оставляет границу за концом истории: она просто ставится заново.</summary>
    [Fact]
    public async Task A_boundary_past_the_end_of_the_history_is_set_again()
    {
        var talk = new Talk { Session = { HygieneUpTo = 1000 } };

        await talk.AskAsync("третий");

        Assert.Equal(talk.Session.ApiMessages.Count - 1, talk.Session.HygieneUpTo);
    }

    /// <summary>Сжатие отрезает начало истории: граница внутри отрезанного ставится заново.</summary>
    [Fact]
    public async Task A_boundary_inside_the_compacted_part_is_set_again()
    {
        var talk = new Talk();
        var session = talk.Session;
        session.CompactedThrough = 4;
        session.CompactSummary = "сводка";
        session.CompactFingerprint = ContextCompaction.Fingerprint(session.ApiMessages, 4);
        session.HygieneUpTo = 2;

        await talk.AskAsync("третий");

        Assert.Equal(session.ApiMessages.Count - 1, session.HygieneUpTo);
    }
}
