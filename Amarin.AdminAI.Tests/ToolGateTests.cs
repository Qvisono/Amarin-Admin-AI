using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Шлюз инструментов: чтение или запись, режимы доступа, выключенные инструменты, молчаливая
/// запись нового файла в «Загрузки» и «Рабочий стол», разрешения впрок — и инструменты чата,
/// которые прежде не проходили ничего.
/// </summary>
[Collection(SafeZoneCollection.Name)]
public sealed class ToolGateTests : IDisposable
{
    private readonly Func<IReadOnlyList<string>> _roots = SafeZone.Roots;
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "amarin-gate-" + Guid.NewGuid().ToString("N"));
    private readonly string _downloads;

    public ToolGateTests()
    {
        _downloads = Path.Combine(_temp, "Downloads");
        Directory.CreateDirectory(_downloads);
        SafeZone.Roots = () => [_downloads];
    }

    public void Dispose()
    {
        SafeZone.Roots = _roots;
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static AppSettings Mode(ApprovalMode mode) => new() { ApprovalMode = mode };

    // ───────────────────────── чтение или запись ─────────────────────────

    [Theory]
    [InlineData("filesystem", "read", ToolEffect.Read)]
    [InlineData("filesystem", "READ", ToolEffect.Read)]
    [InlineData("filesystem", "mkdir", ToolEffect.Write)]
    [InlineData("registry", "write", ToolEffect.Write)]
    [InlineData("registry", "list_subkeys", ToolEffect.Read)]
    [InlineData("change_rollback", "snapshot", ToolEffect.Read)]
    [InlineData("change_rollback", "restore", ToolEffect.Write)]
    [InlineData("scheduled_task", "run", ToolEffect.Write)]
    [InlineData("filesystem", "shred", ToolEffect.Write)]
    [InlineData("brand_new_tool", "list", ToolEffect.Write)]
    public void Unknown_means_write(string tool, string action, ToolEffect expected)
    {
        Assert.Equal(expected, ToolEffects.Classify(tool, Args(new { action })));
    }

    [Theory]
    [InlineData("read_file", ToolEffect.Read)]
    [InlineData("search_web", ToolEffect.Read)]
    [InlineData("init_agent", ToolEffect.Read)]
    [InlineData("write_file", ToolEffect.Write)]
    [InlineData("download_file", ToolEffect.Write)]
    [InlineData("run_powershell", ToolEffect.Write)]
    public void Tools_without_an_action_are_classified_by_name(string tool, ToolEffect expected)
    {
        Assert.Equal(expected, ToolEffects.Classify(tool, Args(new { })));
    }

    [Fact]
    public void Every_tool_the_program_ships_is_in_the_table()
    {
        // Таблица — список чтения. Инструмент, про который в ней забыли, молча стал бы записью:
        // это безопасно, но в «только чтение» он перестал бы работать. Лучше узнать здесь.
        Type[] types;
        try
        {
            types = typeof(ITool).Assembly.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            // Без настоящего WPF (прогон вне Windows) часть типов окна не грузится; инструменты
            // от него не зависят.
            types = ex.Types.OfType<Type>().ToArray();
        }

        var shipped = types
            .Where(type => typeof(ITool).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false })
            .Where(type => type.GetConstructor(Type.EmptyTypes) is not null)
            .Select(type => ((ITool)Activator.CreateInstance(type)!).Name)
            .ToList();
        var known = ToolEffects.KnownTools.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] writeByDesign = ["run_powershell", "write_file", "download_file"];

        Assert.NotEmpty(shipped);
        Assert.All(shipped, name => Assert.True(
            known.Contains(name) || writeByDesign.Contains(name),
            $"{name} нет в ToolEffects"));
    }

    // ───────────────────────── режимы ─────────────────────────

    [Fact]
    public void Read_only_refuses_a_write_and_lets_a_read_through()
    {
        var write = ToolGate.Check("registry", Args(new { action = "write", path = @"HKCU\Software\X" }),
            Mode(ApprovalMode.ReadOnly));
        var read = ToolGate.Check("registry", Args(new { action = "read", path = @"HKCU\Software\X" }),
            Mode(ApprovalMode.ReadOnly));

        Assert.NotNull(write.Refusal);
        Assert.Contains("registry (write)", write.Refusal, StringComparison.Ordinal);
        Assert.Null(read.Refusal);
        Assert.Null(read.Question);
    }

    [Fact]
    public void Ask_all_asks_about_what_normal_lets_through()
    {
        var args = Args(new { action = "mkdir", path = Path.Combine(_temp, "new") });

        Assert.Null(ToolGate.Check("filesystem", args, Mode(ApprovalMode.Normal)).Question);
        Assert.NotNull(ToolGate.Check("filesystem", args, Mode(ApprovalMode.AskAll)).Question);
    }

    [Fact]
    public void A_disabled_tool_is_refused_and_hidden_from_the_model()
    {
        var settings = new AppSettings { DisabledTools = ["Run_PowerShell"] };

        var check = ToolGate.Check("run_powershell", Args(new { command = "Get-Date" }), settings);
        var definitions = ToolGate.WithoutDisabled(
            [Definition("run_powershell"), Definition("read_file")], settings);

        Assert.NotNull(check.Refusal);
        Assert.Equal(["read_file"], definitions.Select(definition => definition.Function.Name).ToList());
    }

    [Fact]
    public void Old_settings_without_the_new_fields_read_as_before()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{"approvalMode":"AlwaysApprove"}""", AppJson.Options)!;

        Assert.Equal(ApprovalMode.AlwaysApprove, settings.ApprovalMode);
        Assert.Null(settings.DisabledTools);
        Assert.Null(ToolGate.Check("read_file", Args(new { path = "x" }), settings).Refusal);
    }

    // ───────────────────────── запись файла ─────────────────────────

    [Fact]
    public void A_new_file_in_downloads_goes_through_quietly_but_only_as_new()
    {
        var check = ToolGate.Check(
            "write_file", Args(new { path = Path.Combine(_downloads, "notes.txt"), content = "x" }), Mode(ApprovalMode.Normal));

        Assert.Null(check.Question);
        Assert.True(check.Arguments.GetProperty(SafeZone.CreateNewFlag).GetBoolean());
    }

    [Fact]
    public void Overwriting_or_writing_elsewhere_is_asked_about()
    {
        var existing = Path.Combine(_downloads, "old.txt");
        File.WriteAllText(existing, "old");

        var overwrite = ToolGate.Check("write_file", Args(new { path = existing, content = "new" }), Mode(ApprovalMode.Normal));
        var elsewhere = ToolGate.Check(
            "write_file", Args(new { path = Path.Combine(_temp, "a.txt"), content = "x" }), Mode(ApprovalMode.Normal));

        Assert.NotNull(overwrite.Question);
        Assert.Contains("old.txt", overwrite.Question!.ChangeSummary, StringComparison.Ordinal);
        Assert.NotNull(elsewhere.Question);
    }

    [Fact]
    public void The_model_cannot_set_the_quiet_write_flag_itself()
    {
        var check = ToolGate.Check(
            "write_file",
            JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["path"] = Path.Combine(_temp, "a.txt"),
                ["content"] = "x",
                [SafeZone.CreateNewFlag] = true
            }),
            Mode(ApprovalMode.Normal));

        Assert.False(check.Arguments.TryGetProperty(SafeZone.CreateNewFlag, out _));
        Assert.NotNull(check.Question);
    }

    [Fact]
    public async Task A_quiet_write_never_overwrites_even_if_the_file_appears_in_between()
    {
        // «Нового» проверяет сама запись, а не шлюз заранее: два параллельных вызова в чате иначе
        // оба увидели бы «файла нет».
        var path = Path.Combine(_downloads, "race.txt");
        var check = ToolGate.Check("write_file", Args(new { path, content = "second" }), Mode(ApprovalMode.Normal));
        File.WriteAllText(path, "first");

        var result = await new WriteFileTool().ExecuteAsync(check.Arguments);

        Assert.False(result.Success);
        Assert.Equal("first", File.ReadAllText(path));
    }

    [Fact]
    public void Copying_over_a_file_is_asked_about()
    {
        var source = Path.Combine(_temp, "s.txt");
        File.WriteAllText(source, "s");
        var target = Path.Combine(_temp, "t.txt");

        Assert.NotNull(ToolGate.Check(
            "filesystem", Args(new { action = "copy", path = source, destination = target }), Mode(ApprovalMode.Normal)).Question);
        Assert.Null(ToolGate.Check(
            "filesystem",
            Args(new { action = "copy", path = source, destination = Path.Combine(_downloads, "copy.txt") }),
            Mode(ApprovalMode.Normal)).Question);
    }

    [Fact]
    public async Task A_refusal_leaves_the_file_as_it_was()
    {
        var existing = Path.Combine(_temp, "keep.txt");
        File.WriteAllText(existing, "original");
        var check = ToolGate.Check("write_file", Args(new { path = existing, content = "evil" }), Mode(ApprovalMode.Normal));

        var decision = await ToolGate.DecideAsync(
            check, (_, _) => Task.FromResult(ConfirmationAnswer.Refused), guardApproved: false, CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Equal(ToolGate.DeniedReply, decision.Refusal);
        Assert.Equal("original", File.ReadAllText(existing));
    }

    // ───────────────────────── разрешения впрок ─────────────────────────

    private static DangerousActionInfo Info(string tool, bool alwaysAsk = false) =>
        new(tool, "summary", "details", DangerousRiskLevel.Medium, AlwaysAsk: alwaysAsk);

    [Fact]
    public async Task Allowing_for_the_turn_answers_the_rest_of_the_turn_and_no_more()
    {
        var queue = new ConfirmationQueue(AppSettings.CreateDefault);
        var first = queue.ConfirmDetailedAsync("Чат", Info("write_file"), "chat-1");
        Assert.True(queue.TryPeek(out var shown));

        queue.Complete(shown, approved: true, AllowanceScope.Turn);

        Assert.Equal(ApprovalSource.AllowTurn, (await first).Source);
        var again = await queue.ConfirmDetailedAsync("Чат", Info("write_file"), "chat-1");
        Assert.Equal(new ConfirmationAnswer(true, ApprovalSource.AllowTurn), again);

        // Другой чат и другой инструмент — спрашиваются.
        _ = queue.ConfirmDetailedAsync("Чат", Info("write_file"), "chat-2");
        Assert.True(queue.TryPeek(out var otherChat));
        Assert.Equal("chat-2", otherChat.SessionId);
        queue.CancelAll();

        queue.EndTurn("chat-1");
        _ = queue.ConfirmDetailedAsync("Чат", Info("write_file"), "chat-1");
        Assert.True(queue.TryPeek(out _), "разрешение до конца хода пережило конец хода");
        queue.CancelAll();
    }

    [Fact]
    public async Task Allowing_for_the_chat_answers_questions_already_waiting()
    {
        var queue = new ConfirmationQueue(AppSettings.CreateDefault);
        var first = queue.ConfirmDetailedAsync("Чат", Info("write_file"), "chat-1");
        var second = queue.ConfirmDetailedAsync("Чат", Info("write_file"), "chat-1");
        Assert.True(queue.TryPeek(out var shown));

        queue.Complete(shown, approved: true, AllowanceScope.Chat);

        Assert.Equal(ApprovalSource.AllowChat, (await first).Source);
        Assert.Equal(ApprovalSource.AllowChat, (await second).Source);
        queue.EndTurn("chat-1");
        Assert.Equal(ApprovalSource.AllowChat, queue.AllowedAhead("chat-1", "write_file"));
    }

    [Fact]
    public void Powershell_is_never_allowed_for_a_whole_chat()
    {
        var queue = new ConfirmationQueue(AppSettings.CreateDefault);
        _ = queue.ConfirmDetailedAsync("Агент", Info("run_powershell"), "chat-1");
        Assert.True(queue.TryPeek(out var shown));
        Assert.False(shown.CanAllowForChat);

        queue.Complete(shown, approved: true, AllowanceScope.Chat);
        queue.EndTurn("chat-1");

        Assert.Null(queue.AllowedAhead("chat-1", "run_powershell"));
    }

    [Fact]
    public void A_synguard_question_is_never_answered_ahead()
    {
        var queue = new ConfirmationQueue(AppSettings.CreateDefault);
        queue.Allow("chat-1", "write_file", AllowanceScope.Chat);

        var answer = queue.ConfirmDetailedAsync("Чат", Info("write_file", alwaysAsk: true), "chat-1");

        Assert.False(answer.IsCompleted);
        Assert.True(queue.TryPeek(out var shown));
        Assert.False(shown.CanAllowAhead);
        queue.CancelAll();
    }

    // ───────────────────────── чат ─────────────────────────

    [Theory]
    [InlineData(ApprovalMode.Normal)]
    [InlineData(ApprovalMode.AskAll)]
    public async Task Without_confirmation_write_file_from_the_chat_does_not_touch_an_existing_file(ApprovalMode mode)
    {
        // Прежде write_file из чата не спрашивал ничего и переписывал любой файл.
        var path = Path.Combine(_temp, "important.txt");
        File.WriteAllText(path, "original");
        var settings = Mode(mode);
        var queue = new ConfirmationQueue(() => settings);
        var asked = 0;
        queue.Changed += () =>
        {
            if (queue.TryPeek(out var request))
            {
                Interlocked.Increment(ref asked);
                queue.Complete(request, approved: false);
            }
        };

        var session = await RunChatAsync(settings, queue, path, "evil");

        Assert.Equal("original", File.ReadAllText(path));
        Assert.Equal(1, asked);
        var call = Assert.Single(Assert.Single(session.Messages.Last(m => m.Role == "assistant").ToolRounds).Calls);
        Assert.False(call.Success);
    }

    [Fact]
    public async Task A_chat_without_a_confirmation_queue_writes_nothing()
    {
        var path = Path.Combine(_downloads, "new.txt");

        await RunChatAsync(Mode(ApprovalMode.Normal), queue: null, path, "text");

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_new_file_in_downloads_is_written_from_the_chat_without_a_question()
    {
        var path = Path.Combine(_downloads, "report.txt");
        var settings = Mode(ApprovalMode.Normal);
        var queue = new ConfirmationQueue(() => settings);
        var asked = 0;
        queue.Changed += () =>
        {
            if (queue.TryPeek(out var request))
            {
                Interlocked.Increment(ref asked);
                queue.Complete(request, approved: false);
            }
        };

        await RunChatAsync(settings, queue, path, "report");

        Assert.Equal("report", File.ReadAllText(path));
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task Read_only_refuses_the_chat_write_without_asking()
    {
        var path = Path.Combine(_downloads, "blocked.txt");
        var settings = Mode(ApprovalMode.ReadOnly);
        var queue = new ConfirmationQueue(() => settings);
        var asked = 0;
        queue.Changed += () => Interlocked.Increment(ref asked);

        var session = await RunChatAsync(settings, queue, path, "x");

        Assert.False(File.Exists(path));
        Assert.Equal(0, asked);
        var call = Assert.Single(Assert.Single(session.Messages.Last(m => m.Role == "assistant").ToolRounds).Calls);
        Assert.Contains("Только чтение", call.ResultText ?? call.ResultPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_chat_asks_synguard_about_a_round_that_writes()
    {
        var path = Path.Combine(_downloads, "guarded.txt");
        var settings = Mode(ApprovalMode.Normal);
        var queue = new ConfirmationQueue(() => settings);
        var questions = new List<DangerousActionInfo>();
        queue.Changed += () =>
        {
            if (queue.TryPeek(out var request))
            {
                questions.Add(request.Info);
                queue.Complete(request, approved: false);
            }
        };
        SynGuardRequest? seen = null;

        await RunChatAsync(settings, queue, path, "x", guard: (request, _) =>
        {
            seen = request;
            return Task.FromResult(new SynGuardReport([false], null));
        });

        Assert.NotNull(seen);
        Assert.Equal("write_file", Assert.Single(seen!.Value.Calls).Name);
        Assert.True(Assert.Single(questions).AlwaysAsk);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_refused_chat_write_lands_in_the_audit_log()
    {
        var path = Path.Combine(_temp, "kept.txt");
        File.WriteAllText(path, "original");
        var settings = Mode(ApprovalMode.Normal);
        var queue = new ConfirmationQueue(() => settings);
        queue.Changed += () =>
        {
            if (queue.TryPeek(out var request))
            {
                queue.Complete(request, approved: false);
            }
        };
        var audit = new AuditLog(Path.Combine(_temp, "profile"));

        await RunChatAsync(settings, queue, path, "evil", audit: audit);

        var entry = Assert.Single(audit.ReadAll());
        Assert.Equal("write_file", entry.Tool);
        Assert.Equal(AuditOutcome.Refused, entry.Outcome);
        Assert.Equal(ApprovalSource.Human, entry.ApprovedBy);
        Assert.Equal(AuditGuard.Safe, entry.Guard);
        Assert.Equal("chat-1", entry.ChatId);
        Assert.Null(entry.Agent);
        Assert.Contains("kept.txt", entry.Args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_quiet_new_file_is_in_the_audit_log_as_done_without_a_question()
    {
        var path = Path.Combine(_downloads, "logged.txt");
        var settings = Mode(ApprovalMode.Normal);
        var audit = new AuditLog(Path.Combine(_temp, "profile"));

        await RunChatAsync(settings, new ConfirmationQueue(() => settings), path, "text", audit: audit);

        var entry = Assert.Single(audit.ReadAll());
        Assert.Equal(AuditOutcome.Ok, entry.Outcome);
        Assert.Equal(ApprovalSource.NotRequired, entry.ApprovedBy);
    }

    private static async Task<ChatSession> RunChatAsync(
        AppSettings settings,
        ConfirmationQueue? queue,
        string path,
        string content,
        Func<SynGuardRequest, CancellationToken, Task<SynGuardReport>>? guard = null,
        AuditLog? audit = null)
    {
        var responses = 0;
        var handler = new ScriptedHandler(_ => Interlocked.Increment(ref responses) == 1
            ? SseWithToolCall("write_file", JsonSerializer.Serialize(new { path, content }))
            : Sse("Готово."));
        var options = new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            MaxToolRounds = 3,
            Audit = audit
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var engine = new ChatEngine(
            new VeniceClient(http, options),
            options,
            () => settings,
            new ToolRegistry([new WriteFileTool()]),
            confirmations: queue)
        {
            Guard = guard ?? ((_, _) => Task.FromResult(new SynGuardReport([true], null)))
        };

        var session = new ChatSession { Id = "chat-1", SelectedModelId = "grok-4-6" };
        await engine.RunTurnAsync(session, "сохрани заметку", new SilentObserver(), CancellationToken.None);
        return session;
    }

    private static ToolDefinition Definition(string name) => new()
    {
        Function = new FunctionDefinition
        {
            Name = name,
            Description = "",
            Parameters = JsonDocument.Parse("{}").RootElement.Clone()
        }
    };

    private static HttpResponseMessage Sse(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "data: {\"model\":\"grok-4-6\",\"choices\":[{\"delta\":{\"content\":" + JsonSerializer.Serialize(text) + "}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n" +
            "data: [DONE]\n",
            Encoding.UTF8,
            "text/event-stream")
    };

    private static HttpResponseMessage SseWithToolCall(string tool, string arguments) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "data: {\"model\":\"grok-4-6\",\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\"," +
            "\"function\":{\"name\":\"" + tool + "\",\"arguments\":" + JsonSerializer.Serialize(arguments) + "}}]}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n" +
            "data: [DONE]\n",
            Encoding.UTF8,
            "text/event-stream")
    };

    private sealed class ScriptedHandler(Func<string, HttpResponseMessage> script) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return script(body);
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

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SafeZoneCollection
{
    public const string Name = "SafeZone";
}
