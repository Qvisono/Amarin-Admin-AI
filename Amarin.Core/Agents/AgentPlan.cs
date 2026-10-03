using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>Шаг плана агента: что он сделает, каким инструментом и меняет ли это систему.</summary>
public sealed record PlanStep(
    string Description,
    string Tool,
    string? Action = null,
    string? Target = null,
    string? Script = null,
    bool ChangesSystem = false);

/// <summary>План, который агент предлагает до первого изменения.</summary>
public sealed class AgentPlan
{
    public string Summary { get; init; } = "";

    public List<PlanStep> Steps { get; init; } = [];

    /// <summary>Сколько шагов меняют систему. Считается, а не хранится: в файле чата не нужно.</summary>
    [JsonIgnore]
    public int ChangingSteps => Steps.Count(step => step.ChangesSystem);
}

/// <summary>Что решил человек о плане.</summary>
public enum PlanVerdict
{
    Execute,
    Cancel,
    Amend
}

public sealed record PlanDecision(PlanVerdict Verdict, string Remark = "")
{
    public static PlanDecision Execute { get; } = new(PlanVerdict.Execute);

    public static PlanDecision Cancel { get; } = new(PlanVerdict.Cancel);
}

/// <summary>
/// Инструмент <c>submit_plan</c> фазы плана и правила: как разобрать план и какой вызов шаг
/// плана покрывает.
/// </summary>
/// <remarks>
/// <para>
/// Одобренный план — не «разрешить всё до конца хода». Вызов проходит без вопроса, только если
/// совпадает с шагом по инструменту, действию и цели, а у скрипта — по тексту с точностью до
/// пробелов. Шаг без цели и без скрипта покрывает только вызов, у которого цели нет вовсе
/// (сброс кэша DNS): иначе «registry write» без пути разрешал бы запись в любой раздел.
/// </para>
/// <para>
/// Отступление от плана спрашивается как обычно, вопрос SynGuard (<c>AlwaysAsk</c>) —
/// всегда: план одобрял человек, а SynGuard смотрит на то, что пришло на самом деле.
/// </para>
/// </remarks>
internal static partial class AgentPlans
{
    public const string SubmitTool = "submit_plan";

    /// <summary>Сколько шагов имеет смысл показывать человеку: длиннее — это уже не план.</summary>
    public const int MaxSteps = 30;

    /// <summary>Поля аргументов, в которых живёт цель вызова.</summary>
    internal static readonly string[] TargetFields =
    [
        "path", "service_name", "name", "adapter", "instance_id", "hostname", "task_name", "user", "group",
        "feature_name", "package_id", "url", "wifi_profile", "destination", "drive_letter", "process_name", "pid",
        "snapshot_id", "location"
    ];

    public static ToolDefinition Definition { get; } = new()
    {
        Function = new FunctionDefinition
        {
            Name = SubmitTool,
            Description =
                "Submit the plan for the user's approval before changing anything. List every step in order, " +
                "including read-only checks you still intend to run. The user approves, cancels or asks to revise it.",
            Parameters = JsonSchema.Parse("""
                {
                  "type": "object",
                  "properties": {
                    "summary": { "type": "string", "description": "One or two sentences: the problem and the fix, in the interface language" },
                    "steps": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "description": { "type": "string", "description": "What the step does, in the interface language" },
                          "tool": { "type": "string", "description": "Tool that will be called" },
                          "action": { "type": "string", "description": "The tool's action, if it has one" },
                          "target": { "type": "string", "description": "Exactly what is changed: path, service, adapter, device id, task name" },
                          "script": { "type": "string", "description": "For run_powershell: the exact script that will run" },
                          "changes_system": { "type": "boolean", "description": "true if the step changes anything on the PC" }
                        },
                        "required": ["description", "tool", "changes_system"]
                      }
                    }
                  },
                  "required": ["summary", "steps"]
                }
                """)
        }
    };

    /// <summary>Правило фазы плана — для системного сообщения. Без разбора примеров.</summary>
    public const string PlanningRule = """
        PLAN FIRST. Nothing on this PC may be changed until the user approves a plan.
        - Investigate with read-only tool calls only. Any call that would change the system is refused in this phase.
        - When you know what to do, call submit_plan once: every step in order, with the tool, its action, the
          exact target, and for run_powershell the exact script. Mark each step that changes the system.
        - After approval, carry out exactly the approved steps. Anything outside the plan will be asked about again.
        - If nothing needs to change, do not submit a plan: answer directly.
        """;

    /// <summary>План из аргументов <c>submit_plan</c>; null — план не разобрать.</summary>
    public static AgentPlan? Parse(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("steps", out var steps) ||
            steps.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var plan = new AgentPlan
        {
            Summary = Text(arguments, "summary") ?? ""
        };

        foreach (var item in steps.EnumerateArray().Take(MaxSteps))
        {
            if (item.ValueKind != JsonValueKind.Object || Text(item, "tool") is not { } tool)
            {
                continue;
            }

            plan.Steps.Add(new PlanStep(
                Text(item, "description") ?? tool,
                tool,
                Text(item, "action")?.ToLowerInvariant(),
                Text(item, "target"),
                Text(item, "script"),
                item.TryGetProperty("changes_system", out var changes) && changes.ValueKind == JsonValueKind.True));
        }

        return plan.Steps.Count == 0 ? null : plan;
    }

    /// <summary>Покрывает ли одобренный план этот вызов.</summary>
    public static bool Covers(AgentPlan plan, string toolName, JsonElement arguments) =>
        plan.Steps.Any(step => Covers(step, toolName, arguments));

    internal static bool Covers(PlanStep step, string toolName, JsonElement arguments)
    {
        if (!step.Tool.Equals(toolName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (toolName.Equals("run_powershell", StringComparison.OrdinalIgnoreCase))
        {
            return step.Script is { Length: > 0 } script &&
                   Text(arguments, "command") is { } command &&
                   Squash(script) == Squash(command);
        }

        var action = DangerousActionGuard.ActionOf(arguments);
        if (!string.IsNullOrEmpty(step.Action) && !step.Action.Equals(action, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var targets = TargetFields
            .Select(field => Text(arguments, field))
            .OfType<string>()
            .Where(value => value.Length > 0)
            .Select(Normalize)
            .ToList();

        if (string.IsNullOrWhiteSpace(step.Target))
        {
            return targets.Count == 0 && !string.IsNullOrEmpty(step.Action);
        }

        return targets.Contains(Normalize(step.Target));
    }

    /// <summary>Скрипт без различий в пробелах и переводах строк.</summary>
    internal static string Squash(string script) => Whitespace().Replace(script.Trim(), " ");

    private static string Normalize(string value) =>
        value.Trim().Trim('"', '\'').TrimEnd('\\', '/').ToLowerInvariant();

    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Для каких уровней агента включён план.</summary>
internal static class AgentPlanSettings
{
    public static readonly string[] AllTiers = [ChatCommands.Lite, ChatCommands.Fast, ChatCommands.Heavy];

    /// <summary>Уровни из настроек; нет поля — только heavy.</summary>
    public static IReadOnlyList<string> Tiers(AppSettings settings) =>
        settings.PlanFirstTiers is { } tiers
            ? tiers.Where(tier => AllTiers.Contains(tier, StringComparer.OrdinalIgnoreCase)).ToList()
            : [ChatCommands.Heavy];

    public static bool IsOn(AppSettings settings, string tier) =>
        Tiers(settings).Contains(tier.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Включает или выключает план для уровня; порядок уровней постоянный.</summary>
    public static void Set(AppSettings settings, string tier, bool on)
    {
        var current = Tiers(settings).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (on)
        {
            current.Add(tier);
        }
        else
        {
            current.Remove(tier);
        }

        settings.PlanFirstTiers = AllTiers.Where(current.Contains).ToList();
    }
}

/// <summary>Просьба рассмотреть план: чей, какой и куда ответить.</summary>
internal sealed class PlanReviewRequest
{
    public required string AgentLabel { get; init; }

    public required AgentPlan Plan { get; init; }

    public string? SessionId { get; init; }

    public required TaskCompletionSource<PlanDecision> Completion { get; init; }
}

/// <summary>
/// Очередь планов на рассмотрение — по одному на экране. Адресная, как очередь подтверждений:
/// отмена хода одного чата снимает только его планы.
/// </summary>
internal sealed class PlanReviewQueue
{
    private readonly List<PlanReviewRequest> _queue = [];
    private readonly Lock _gate = new();

    public event Action? Changed;

    public async Task<PlanDecision> ReviewAsync(
        string agentLabel,
        AgentPlan plan,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new PlanReviewRequest
        {
            AgentLabel = agentLabel,
            Plan = plan,
            SessionId = sessionId,
            Completion = new TaskCompletionSource<PlanDecision>(TaskCreationOptions.RunContinuationsAsynchronously)
        };

        lock (_gate)
        {
            _queue.Add(request);
        }

        Changed?.Invoke();
        await using var registration = cancellationToken.Register(() => Cancel(request));
        return await request.Completion.Task.ConfigureAwait(false);
    }

    public bool TryPeek([NotNullWhen(true)] out PlanReviewRequest? request)
    {
        lock (_gate)
        {
            request = _queue.FirstOrDefault();
            return request is not null;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    public void Complete(PlanReviewRequest request, PlanDecision decision)
    {
        bool removed;
        lock (_gate)
        {
            removed = _queue.Remove(request);
        }

        if (removed)
        {
            request.Completion.TrySetResult(decision);
            Changed?.Invoke();
        }
    }

    public void CancelForSession(string sessionId)
    {
        List<PlanReviewRequest> dropped;
        lock (_gate)
        {
            dropped = _queue.Where(request => request.SessionId == sessionId).ToList();
            _queue.RemoveAll(request => request.SessionId == sessionId);
        }

        foreach (var request in dropped)
        {
            request.Completion.TrySetCanceled();
        }

        if (dropped.Count > 0)
        {
            Changed?.Invoke();
        }
    }

    public void CancelAll()
    {
        List<PlanReviewRequest> dropped;
        lock (_gate)
        {
            dropped = [.. _queue];
            _queue.Clear();
        }

        foreach (var request in dropped)
        {
            request.Completion.TrySetCanceled();
        }

        if (dropped.Count > 0)
        {
            Changed?.Invoke();
        }
    }

    private void Cancel(PlanReviewRequest request)
    {
        bool removed;
        lock (_gate)
        {
            removed = _queue.Remove(request);
        }

        request.Completion.TrySetCanceled();
        if (removed)
        {
            Changed?.Invoke();
        }
    }
}
