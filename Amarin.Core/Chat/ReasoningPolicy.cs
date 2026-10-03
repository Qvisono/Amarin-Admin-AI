using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Amarin.Core;

/// <summary>Чего попросил человек — до подгонки под конкретную модель.</summary>
public readonly record struct ReasoningChoice(bool DisableThinking, string? Effort)
{
    public static ReasoningChoice Disabled { get; } = new(true, null);
}

/// <summary>Per-slot thinking settings in <see cref="AppSettings"/>.</summary>
public sealed class ReasoningSettings
{
    public bool DisableThinking { get; set; } = true;

    public string? ReasoningEffort { get; set; }

    public ReasoningChoice ToChoice() =>
        new(DisableThinking, string.IsNullOrWhiteSpace(ReasoningEffort) ? null : ReasoningEffort.Trim());

    public void Apply(ReasoningChoice choice)
    {
        DisableThinking = choice.DisableThinking;
        ReasoningEffort = string.IsNullOrWhiteSpace(choice.Effort) ? null : choice.Effort.Trim();
    }
}

/// <summary>Wire fields produced for one model from a <see cref="ReasoningChoice"/>.</summary>
public sealed class ReasoningWire
{
    /// <summary>
    /// <c>venice_parameters.disable_thinking</c> — выключатель Venice для моделей, думающих
    /// блоками <c>&lt;think&gt;</c> (Qwen и подобные). Grok и Claude его не учитывают.
    /// </summary>
    public bool? DisableThinking { get; init; }

    public string? ReasoningEffort { get; init; }
}

/// <summary>
/// Переводит возможности модели из <c>/models</c> в варианты интерфейса и тело запроса чата.
/// Неподдержанную силу размышления Venice не подменяет, а отвечает 400, поэтому всё, чего нет
/// в списке модели, снимается или заменяется — и никогда не угадывается в большую сторону.
/// </summary>
internal static class ReasoningPolicy
{
    public const string None = "none";

    /// <summary>Самый широкий общий набор — только когда флаг есть, а списка нет.</summary>
    public static readonly string[] CommonEfforts = ["low", "medium", "high"];

    private static readonly string[] PreferredDefaults = ["medium", "low", "high"];

    /// <summary>
    /// Модели, ответившие 400 на инструменты вместе с силой размышления не none. Живёт до конца
    /// процесса, чтобы следующий ход не предлагал тот же сломанный вариант.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> ToolsEffortBlocked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// GPT-5.4+ / GPT-5.6 / GPT-6 в Chat Completions не принимают инструменты, если
    /// <c>reasoning_effort</c> не <c>none</c>. Без поля тоже отказ — по умолчанию у них medium.
    /// </summary>
    private static readonly Regex ToolsEffortBlockedFamily = new(
        @"gpt-5[.-]?[456](?:$|[^0-9])|gpt-6(?:$|[^0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static VeniceModelInfo? Find(IEnumerable<VeniceModelInfo>? models, string? id)
    {
        if (models is null || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        foreach (var model in models)
        {
            if (model.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }
        }

        return null;
    }

    public static bool SupportsEffort(VeniceModelInfo? model)
    {
        var caps = model?.ModelSpec?.Capabilities;
        if (caps is null)
        {
            return false;
        }

        if (caps.SupportsReasoningEffort)
        {
            return true;
        }

        return caps.ReasoningEffortOptions is { Count: > 0 };
    }

    /// <summary>
    /// Чат и агенты всегда шлют инструменты. Часть моделей заявляет силу размышления, но отвечает
    /// 400, когда в <c>/chat/completions</c> она не none вместе с инструментами.
    /// </summary>
    public static bool AllowsEffortWithTools(VeniceModelInfo? model, string? modelId = null)
    {
        var id = FirstNonEmpty(model?.Id, modelId);
        if (IsRememberedBlocked(id))
        {
            return false;
        }

        var flagged = model?.ModelSpec?.Capabilities?.SupportsReasoningEffortWithTools;
        if (flagged == false)
        {
            return false;
        }

        if (flagged == true)
        {
            return true;
        }

        return !FamilyBlocksEffortWithTools(id);
    }

    public static void RememberToolsBlockEffort(string? modelId)
    {
        var id = modelId?.Trim();
        if (!string.IsNullOrWhiteSpace(id))
        {
            ToolsEffortBlocked[id] = 0;
        }
    }

    public static bool IsToolsReasoningEffortConflict(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("function tools with reasoning_effort", StringComparison.OrdinalIgnoreCase)
               || (message.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase)
                   && message.Contains("function tools", StringComparison.OrdinalIgnoreCase)
                   && message.Contains("/v1/chat/completions", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Уровни на плашке. <c>none</c> — это переключатель «Без размышления», а не вариант.
    /// <paramref name="withTools"/> — true для чата и агентов: их запросы всегда с инструментами.
    /// </summary>
    public static IReadOnlyList<string> VisibleEffortOptions(VeniceModelInfo? model, bool withTools = false)
    {
        if (withTools && !AllowsEffortWithTools(model))
        {
            return [];
        }

        if (model?.ModelSpec?.Capabilities is not { } capabilities || !SupportsEffort(model))
        {
            return [];
        }

        var raw = capabilities.ReasoningEffortOptions;
        if (raw is { Count: > 0 })
        {
            var visible = new List<string>(raw.Count);
            foreach (var option in raw)
            {
                if (string.IsNullOrWhiteSpace(option) ||
                    option.Equals(None, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                visible.Add(option.Trim());
            }

            return visible;
        }

        return CommonEfforts;
    }

    public static bool HasNoneOption(VeniceModelInfo? model)
    {
        var raw = model?.ModelSpec?.Capabilities?.ReasoningEffortOptions;
        if (raw is null)
        {
            return false;
        }

        foreach (var option in raw)
        {
            if (option.Equals(None, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string? ClampEffort(string? requested, VeniceModelInfo? model, bool withTools = false)
    {
        var options = VisibleEffortOptions(model, withTools);
        if (options.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(requested))
        {
            var match = Match(options, requested);
            if (match is not null)
            {
                return match;
            }
        }

        var declared = model?.ModelSpec?.Capabilities?.DefaultReasoningEffort;
        if (!string.IsNullOrWhiteSpace(declared) &&
            !declared.Equals(None, StringComparison.OrdinalIgnoreCase))
        {
            var fromDefault = Match(options, declared);
            if (fromDefault is not null)
            {
                return fromDefault;
            }
        }

        foreach (var preferred in PreferredDefaults)
        {
            var hit = Match(options, preferred);
            if (hit is not null)
            {
                return hit;
            }
        }

        return options[0];
    }

    public static ReasoningWire ToWire(
        VeniceModelInfo? model,
        ReasoningChoice? choice,
        bool withTools = false,
        string? modelId = null)
    {
        // У моделей класса GPT-5.6 по умолчанию medium, и без reasoning_effort при инструментах
        // всё равно 400: в /chat/completions допустим только none.
        if (withTools && !AllowsEffortWithTools(model, modelId))
        {
            return new ReasoningWire
            {
                DisableThinking = choice is null || choice.Value.DisableThinking ? true : null,
                ReasoningEffort = None
            };
        }

        // «Без размышления» — это venice_parameters.disable_thinking, а не reasoning.enabled (его
        // нет в схеме запроса) и не сила «none» (её берут лишь некоторые модели класса GPT, а
        // неподдержанное значение — это 400).
        if (choice is null || choice.Value.DisableThinking)
        {
            return new ReasoningWire { DisableThinking = true };
        }

        if (!SupportsEffort(model))
        {
            return new ReasoningWire();
        }

        var effort = ClampEffort(choice.Value.Effort, model, withTools);
        if (effort is null)
        {
            return new ReasoningWire();
        }

        return new ReasoningWire { ReasoningEffort = effort };
    }

    public static string EffortLabel(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return "";
        }

        return effort.Trim().ToLowerInvariant() switch
        {
            "minimal" => "Minimal",
            "low" => "Low",
            "medium" => "Medium",
            "high" => "High",
            "xhigh" => "xHigh",
            "max" => "Max",
            _ => char.ToUpperInvariant(effort.Trim()[0]) + effort.Trim()[1..].ToLowerInvariant()
        };
    }

    public static string ButtonText(
        ReasoningChoice choice,
        VeniceModelInfo? model,
        bool autoMode,
        bool withTools = false)
    {
        if (choice.DisableThinking)
        {
            return Loc.Get("S.Reasoning.Off");
        }

        // «Авто» своим словом, а не общим «Обычное»: уровень здесь берётся от лёгкой и тяжёлой
        // модели и заранее не известен, так что называть его конкретной ступенью — врать.
        if (autoMode)
        {
            return Loc.Get("S.Reasoning.Auto");
        }

        var options = VisibleEffortOptions(model, withTools);
        if (options.Count == 0)
        {
            return Loc.Get("S.Reasoning.On");
        }

        var effort = ClampEffort(choice.Effort, model, withTools);
        return string.IsNullOrWhiteSpace(effort) ? Loc.Get("S.Reasoning.On") : EffortLabel(effort);
    }

    public static bool FamilyBlocksEffortWithTools(string? modelId)
    {
        // Запрет живёт у Venice, а не у моделей: тот же GPT-5.x за OpenRouter прекрасно
        // принимает усилие рядом с инструментами. Разбор имени модели этой разницы не видит —
        // имена у провайдеров одни и те же, — поэтому чужие идентификаторы сюда не пускаем.
        if (ModelRef.Of(modelId) != LlmProvider.Venice)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        return ToolsEffortBlockedFamily.IsMatch(modelId.Trim());
    }

    private static bool IsRememberedBlocked(string? id) =>
        !string.IsNullOrWhiteSpace(id) && ToolsEffortBlocked.ContainsKey(id);

    private static string? FirstNonEmpty(string? left, string? right)
    {
        if (!string.IsNullOrWhiteSpace(left))
        {
            return left.Trim();
        }

        return string.IsNullOrWhiteSpace(right) ? null : right.Trim();
    }

    private static string? Match(IReadOnlyList<string> options, string value)
    {
        foreach (var option in options)
        {
            if (option.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }
        }

        return null;
    }
}
