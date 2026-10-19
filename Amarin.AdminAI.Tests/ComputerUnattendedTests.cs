using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Управление компьютером без человека (1.37.0): слова человека у отложенной задачи под печатью,
/// галочка мыши у задачи расписания под печатью, «только чтение, кроме мыши» и сброс галочки
/// из чужого архива.
/// </summary>
public sealed class ComputerUnattendedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-unattended-" + Guid.NewGuid().ToString("N"));

    public ComputerUnattendedTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static IDisposable Chat(string id, string? words) => AgentRunScope.Push(new AgentRunContext
    {
        Call = new ToolCallRecord { Id = "call", Name = DeferredTaskTool.ToolName },
        Assistant = new ChatDisplayMessage { Role = "assistant" },
        Observer = SilentTurnObserver.Instance,
        SessionId = id,
        HumanWords = words
    });

    // ───────────────────────── отложенная задача ─────────────────────────

    [Fact]
    public async Task An_agent_task_keeps_the_persons_words_under_a_seal()
    {
        var book = new DeferredBook(_root);
        using (Chat("chat-1", "the person's words"))
        {
            var result = await new DeferredTaskTool(book).ExecuteAsync(Args(new { action = "run_agent", text = "the model's task", @in = "5m" }));
            Assert.True(result.Success, result.Output);
        }

        var task = Assert.Single(book.Snapshot());
        Assert.Equal("the person's words", task.HumanWords);
        Assert.True(book.Verify(task));
    }

    [Fact]
    public async Task Without_the_persons_words_an_agent_task_stays_unsealed_as_before()
    {
        var book = new DeferredBook(_root);
        using (Chat("chat-1", null))
        {
            _ = await new DeferredTaskTool(book).ExecuteAsync(Args(new { action = "run_agent", text = "task", @in = "5m" }));
        }

        var task = Assert.Single(book.Snapshot());
        Assert.Null(task.HumanWords);
        Assert.Null(task.Seal);
    }

    [Fact]
    public void Editing_the_words_or_the_task_by_hand_breaks_the_seal()
    {
        var book = new DeferredBook(_root);
        var task = new DeferredTask
        {
            Id = "t1",
            Kind = DeferredKind.Agent,
            Text = "task",
            HumanWords = "words",
            CreatedUtc = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc)
        };
        task.Seal = book.Seal(task);

        Assert.True(book.Verify(task));
        task.HumanWords = "other words";
        Assert.False(book.Verify(task));
        task.HumanWords = "words";
        task.Text = "other task";
        Assert.False(book.Verify(task));
    }

    [Fact]
    public void Seals_of_tasks_without_words_are_what_they_were()
    {
        // Команда 1.36: текст задачи под печать не входил и не входит — меняется только при словах.
        var book = new DeferredBook(_root);
        var command = new DeferredTask
        {
            Id = "c1",
            Kind = DeferredKind.Command,
            Command = "Get-Date",
            Text = "first",
            CreatedUtc = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)
        };
        var seal = book.Seal(command);
        command.Text = "second";

        Assert.Equal(seal, book.Seal(command));
        command.HumanWords = "words";
        Assert.NotEqual(seal, book.Seal(command));
    }

    [Fact]
    public void The_message_of_a_task_is_not_taken_for_the_persons_words()
    {
        var session = new ChatSession();
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Text = "earlier words" });
        session.Messages.Add(new ChatDisplayMessage
        {
            Role = "user",
            Text = "the model's task",
            Deferred = new DeferredMark("t1", "title", DateTime.UtcNow)
        });

        Assert.Null(ChatEngine.HumanWordsOf(session));

        session.Messages[^1] = new ChatDisplayMessage
        {
            Role = "user",
            Text = "the model's task",
            Deferred = new DeferredMark("t1", "title", DateTime.UtcNow, HumanWords: "the person's words")
        };
        Assert.Equal("the person's words", ChatEngine.HumanWordsOf(session));

        session.Messages.Add(new ChatDisplayMessage { Role = "user", Text = "newer words" });
        Assert.Equal("newer words", ChatEngine.HumanWordsOf(session));
    }

    // ───────────────────────── расписание ─────────────────────────

    private static ScheduledJob Job(bool computer) => new()
    {
        Id = "job1",
        Name = "Job",
        Prompt = "the person's task",
        Computer = computer
    };

    [Fact]
    public void The_mouse_switch_of_a_scheduled_job_counts_only_under_its_seal()
    {
        var book = new DeferredBook(_root);
        var job = Job(computer: true);

        Assert.False(book.VerifyJob(job));
        job.ComputerSeal = book.SealJob(job);
        Assert.True(book.VerifyJob(job));

        job.Prompt = "edited by hand";
        Assert.False(book.VerifyJob(job));
    }

    [Fact]
    public void A_seal_made_with_another_key_does_not_count()
    {
        var job = Job(computer: true);
        var otherRoot = Path.Combine(_root, "other");
        Directory.CreateDirectory(otherRoot);
        job.ComputerSeal = new DeferredBook(otherRoot).SealJob(job);

        Assert.False(new DeferredBook(_root).VerifyJob(job));
    }

    [Fact]
    public void A_job_without_the_switch_never_counts_whatever_its_seal()
    {
        var book = new DeferredBook(_root);
        var job = Job(computer: true);
        job.ComputerSeal = book.SealJob(job);
        job.Computer = false;

        Assert.False(book.VerifyJob(job));
    }

    [Fact]
    public void A_scheduled_run_with_the_switch_writes_only_with_the_mouse()
    {
        var press = Args(new { action = "click", element = "e1", explanation = "press" });
        var registry = Args(new { action = "write", path = @"HKCU\Software\X", value = "1" });
        var normal = new AppSettings();

        using (ToolGate.ForceReadOnly(ComputerTool.ToolName))
        {
            Assert.Null(ToolGate.Check(ComputerTool.ToolName, press, normal).Refusal);
            Assert.NotNull(ToolGate.Check("registry", registry, normal).Refusal);
            Assert.True(ToolGate.ScopedReadOnly);

            // «Только чтение» в настройках — выбор человека: исключение прогона его не снимает.
            Assert.NotNull(ToolGate.Check(ComputerTool.ToolName, press, new AppSettings { ApprovalMode = ApprovalMode.ReadOnly }).Refusal);
        }

        using (ToolGate.ForceReadOnly())
        {
            Assert.NotNull(ToolGate.Check(ComputerTool.ToolName, press, normal).Refusal);
        }

        Assert.False(ToolGate.ScopedReadOnly);
    }

    [Fact]
    public void The_scheduled_rule_with_the_mouse_states_rules_only()
    {
        Assert.Contains("use_computer", ScheduleReport.ComputerRule, StringComparison.Ordinal);
        Assert.Contains("STATUS: OK", ScheduleReport.ComputerRule, StringComparison.Ordinal);
        Assert.DoesNotMatch("[Ѐ-ӿ]", ScheduleReport.ComputerRule);
    }

    [Fact]
    public void A_job_from_an_archive_arrives_without_the_mouse()
    {
        var source = Path.Combine(_root, "source");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var job = Job(computer: true);
        job.ComputerSeal = new DeferredBook(source).SealJob(job);
        new ScheduleBook(source).Save([job]);

        var archive = Path.Combine(_root, "bundle" + DataBundle.FileExtension);
        new DataBundleExporter(source).Write(archive, DataCategory.Settings);
        var result = new DataBundleImporter(target).Apply(archive, DataCategory.Settings, DataImportMode.Merge);

        Assert.True(result.Ok, result.Error.ToString());
        var imported = Assert.Single(new ScheduleBook(target).Load());
        Assert.False(imported.Computer);
        Assert.Null(imported.ComputerSeal);
    }
}
