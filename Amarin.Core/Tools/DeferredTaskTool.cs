using System.Globalization;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Отложенные задачи чата: напоминание, поручение агенту, одобренная команда или возврат программ —
/// к сроку, по времени работы Windows, при включении компьютера или по условию, разово или с повтором.
/// </summary>
/// <remarks>
/// <para>
/// Только чат: задача помнит, в каком чате её поставили, — туда и придёт отчёт. Без чата
/// (рецепт, прогон инструментов) инструмент отказывает.
/// </para>
/// <para>
/// Команда и возврат программ выполнятся без человека, поэтому шлюз спрашивает его сейчас, при
/// постановке (<see cref="DangerousActionGuard"/>), а задача получает печать
/// (<see cref="DeferredBook.Seal"/>). Поручение агенту ничего не одобряет заранее: его действия
/// пройдут шлюз в своё время, как всегда.
/// </para>
/// </remarks>
public sealed class DeferredTaskTool : ITool
{
    public const string ToolName = "deferred_task";

    private readonly DeferredBook _book;
    private readonly Func<DeferredFacts> _facts;

    internal DeferredTaskTool(DeferredBook book, Func<DeferredFacts>? facts = null)
    {
        _book = book;
        _facts = facts ?? DeferredClock.Now;
    }

    public string Name => ToolName;

    public string Description =>
        "Schedule something for later or for a condition, and manage what is scheduled. action: remind (a reminder card " +
        "with a soft chime), run_agent (when the time comes the agent does the task and reports in this chat), " +
        "run_command (runs this exact PowerShell command; the user approves it now), restore_programs (reopens the programs " +
        "that were open when the PC was last shut down), list, cancel (by id). When: in (\"90m\", \"2h\", \"1d 3h\"), at " +
        "(\"18:30\" is the next such time, or \"2026-10-11 09:00\" local time), uptime_hours (Windows uptime as Task Manager " +
        "shows it), event (next_boot: the PC is turned on next time; next_start: the program starts next time), or condition " +
        "(read-only PowerShell that prints True or False, checked every check_every_minutes). repeat: once (default), every " +
        "(with every_minutes), daily or weekly (at is the time of day, days for weekly), every_start, every_boot. Pass times " +
        "the way the user said them; the result names the resolved time.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["remind", "run_agent", "run_command", "restore_programs", "list", "cancel"] },
            "title": { "type": "string", "description": "Short name of the task, in the user's language" },
            "text": { "type": "string", "description": "remind: the reminder text; run_agent: the task for the agent" },
            "command": { "type": "string", "description": "run_command: the exact PowerShell script" },
            "id": { "type": "string", "description": "cancel: the task id" },
            "in": { "type": "string", "description": "Delay: 90m, 2h, 1d 3h" },
            "at": { "type": "string", "description": "18:30 (next such time) or 2026-10-11 09:00 (local); time of day for daily/weekly" },
            "uptime_hours": { "type": "number", "description": "Fire when Windows uptime reaches this many hours" },
            "event": { "type": "string", "enum": ["next_boot", "next_start"] },
            "condition": { "type": "string", "description": "Read-only PowerShell that prints True or False" },
            "check_every_minutes": { "type": "integer", "description": "How often to check the condition (default 5)" },
            "repeat": { "type": "string", "enum": ["once", "every", "daily", "weekly", "every_start", "every_boot"] },
            "every_minutes": { "type": "integer", "description": "repeat=every: period in minutes" },
            "days": { "type": "array", "items": { "type": "string" }, "description": "repeat=weekly: mon, tue, wed, thu, fri, sat, sun" },
            "max_cost_usd": { "type": "number", "description": "run_agent: spending cap per run in USD (default 0.5, at most 5)" }
          },
          "required": ["action"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        Task.FromResult(Execute(arguments));

    private ToolResult Execute(JsonElement arguments)
    {
        var action = (FileToolPaths.String(arguments, "action") ?? "").Trim().ToLowerInvariant();
        return action switch
        {
            "list" => List(),
            "cancel" => Cancel(FileToolPaths.String(arguments, "id")),
            "remind" => Create(arguments, DeferredKind.Reminder),
            "run_agent" => Create(arguments, DeferredKind.Agent),
            "run_command" => Create(arguments, DeferredKind.Command),
            "restore_programs" => Create(arguments, DeferredKind.RestorePrograms),
            _ => ToolResult.Fail("action must be remind, run_agent, run_command, restore_programs, list or cancel.")
        };
    }

    private ToolResult Create(JsonElement arguments, DeferredKind kind)
    {
        if (AgentRunScope.Current?.SessionId is not { Length: > 0 } chat)
        {
            return ToolResult.Fail("deferred_task works only in a chat with the user.");
        }

        var text = FileToolPaths.String(arguments, "text")?.Trim() ?? "";
        var command = FileToolPaths.String(arguments, "command")?.Trim();
        if (kind is DeferredKind.Reminder or DeferredKind.Agent && text.Length == 0)
        {
            return ToolResult.Fail(kind == DeferredKind.Reminder ? "remind needs text: what to remind the user of." : "run_agent needs text: the task for the agent.");
        }

        if (kind == DeferredKind.Command && string.IsNullOrWhiteSpace(command))
        {
            return ToolResult.Fail("run_command needs command: the exact PowerShell script to run.");
        }

        if (_book.PendingCount >= DeferredLimits.MaxPending)
        {
            return ToolResult.Fail($"There are already {DeferredLimits.MaxPending} scheduled tasks. Cancel some (action=list, then cancel) before adding more.");
        }

        if (FileToolPaths.String(arguments, "condition") is { Length: > 0 } condition &&
            PowerShellAnalysis.Analyze(JsonSerializer.SerializeToElement(new { command = condition })) is { } analysis &&
            (analysis.IsWrite || analysis.Refusal is not null))
        {
            return ToolResult.Fail("The condition script must only read and print True or False; it may not change anything. Rewrite it with Get-/Test- commands.");
        }

        var facts = _facts();
        DeferredSchedule schedule;
        try
        {
            schedule = DeferredTimeParser.Resolve(arguments, kind, facts);
        }
        catch (DeferredInputException ex)
        {
            return ToolResult.Fail(ex.Message);
        }

        var title = FileToolPaths.String(arguments, "title")?.Trim() is { Length: > 0 } given
            ? given
            : Shorten(text.Length > 0 ? text : kind.ToString());
        var task = new DeferredTask
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            ChatId = chat,
            Title = Shorten(title),
            Kind = kind,
            Text = text,
            Command = kind == DeferredKind.Command ? command : null,
            Trigger = schedule.Trigger,
            Repeat = schedule.Repeat,
            CreatedUtc = facts.NowUtc,
            NextDueUtc = schedule.FirstDueUtc,
            Status = DeferredStatus.Pending,
            ReadOnly = ToolGate.ScopedReadOnly,
            MaxCostUsd = kind == DeferredKind.Agent ? Cap(arguments) : null
        };

        // Печать — на то, что выполнится без человека: его согласие шлюз взял только что.
        if (kind is DeferredKind.Command or DeferredKind.RestorePrograms)
        {
            task.Seal = _book.Seal(task);
        }

        var stored = _book.Add(task);
        var report = new StringBuilder()
            .Append("Scheduled task ").Append(stored.Id).Append(": \"").Append(stored.Title).Append("\" (").Append(Label(kind)).Append(").\n")
            .Append("Now: ").Append(DeferredTimeParser.Local(facts.NowUtc, facts)).Append(".\n")
            .Append("Fires: ").Append(DeferredTimeParser.Describe(stored, facts)).Append(".\n");
        if (kind == DeferredKind.Agent)
        {
            report.Append("The agent's report will arrive in this chat; spending cap per run: $")
                .Append((stored.MaxCostUsd ?? DeferredLimits.AgentRunCapUsd).ToString("0.00", CultureInfo.InvariantCulture)).Append(".\n");
        }

        report.Append("If the program is closed at that time, Windows starts it. Tell the user in one line what you scheduled and when.");
        return new ToolResult(true, report.ToString(), Deferred: new DeferredRef(stored.Id, stored.Title, kind));
    }

    private ToolResult List()
    {
        var facts = _facts();
        var tasks = _book.Snapshot();
        var active = tasks.Where(task => task.IsActive).OrderBy(task => task.NextDueUtc ?? DateTime.MaxValue).ToList();
        var recent = tasks.Where(task => !task.IsActive).OrderByDescending(task => task.LastFiredUtc ?? task.CreatedUtc).Take(10).ToList();
        if (active.Count == 0 && recent.Count == 0)
        {
            return ToolResult.Ok($"No scheduled tasks. Now: {DeferredTimeParser.Local(facts.NowUtc, facts)}.");
        }

        var text = new StringBuilder().Append("Now: ").Append(DeferredTimeParser.Local(facts.NowUtc, facts)).Append('\n');
        foreach (var task in active)
        {
            text.Append("- ").Append(task.Id).Append(" \"").Append(task.Title).Append("\" ").Append(Label(task.Kind))
                .Append(", ").Append(task.Status.ToString().ToLowerInvariant()).Append(": ").Append(DeferredTimeParser.Describe(task, facts)).Append('\n');
        }

        if (recent.Count > 0)
        {
            text.Append("Finished recently:\n");
            foreach (var task in recent)
            {
                text.Append("- ").Append(task.Id).Append(" \"").Append(task.Title).Append("\" ").Append(task.Status.ToString().ToLowerInvariant());
                if (task.LastFiredUtc is { } fired)
                {
                    text.Append(" at ").Append(DeferredTimeParser.Local(fired, facts));
                }

                text.Append('\n');
            }
        }

        return ToolResult.Ok(text.ToString().TrimEnd());
    }

    private ToolResult Cancel(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return ToolResult.Fail("cancel needs id; get it with action=list.");
        }

        var cancelled = false;
        var found = _book.Update(id.Trim(), task =>
        {
            if (task.IsActive)
            {
                task.Status = DeferredStatus.Cancelled;
                task.NextDueUtc = null;
                cancelled = true;
            }
        });

        return !found ? ToolResult.Fail($"There is no task {id}. Get the ids with action=list.")
            : cancelled ? ToolResult.Ok($"Cancelled task {id}.")
            : ToolResult.Ok($"Task {id} has already finished; nothing to cancel.");
    }

    private static decimal Cap(JsonElement arguments)
    {
        var cap = arguments.TryGetProperty("max_cost_usd", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var given)
            ? given
            : DeferredLimits.AgentRunCapUsd;
        return Math.Clamp(cap, 0.01m, DeferredLimits.MaxAgentRunCapUsd);
    }

    private static string Label(DeferredKind kind) => kind switch
    {
        DeferredKind.Reminder => "reminder",
        DeferredKind.Agent => "agent task",
        DeferredKind.Command => "command",
        _ => "reopen programs"
    };

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 80 ? line : line[..79] + "…";
    }
}
