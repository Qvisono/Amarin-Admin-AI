using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Amarin.Core;

/// <summary>Чего попросил человек — до подгонки под конкретную модель.</summary>
/// <param name="Speed">
/// Скорость модели (1.37.0, <see cref="ModelSpeeds"/>): <c>flex</c>, <c>fast</c>, <c>ultrafast</c>.
/// Пусто — обычная. Едет вместе с размышлением, потому что выбирается в том же месте и для того
/// же слота; на провод уходит, только если модель её умеет.
/// </param>
public readonly record struct ReasoningChoice(bool DisableThinking, string? Effort, string? Speed = null)
{
    public static ReasoningChoice Disabled { get; } = new(true, null);
}

/// <summary>Per-slot thinking settings in <see cref="AppSettings"/>.</summary>
public sealed class ReasoningSettings
{
    public bool DisableThinking { get; set; } = true;

    public string? ReasoningEffort { get; set; }

    /// <summary>Скорость модели слота (1.37.0). Пусто — обычная.</summary>
    public string? Speed { get; set; }

    public ReasoningChoice ToChoice() =>
        new(DisableThinking, Trimmed(ReasoningEffort), Trimmed(Speed));

    public void Apply(ReasoningChoice choice)
    {
        DisableThinking = choice.DisableThinking;
        ReasoningEffort = Trimmed(choice.Effort);
        Speed = Trimmed(choice.Speed);
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

    /// <summary>
    /// Можно ли у модели выключить размышление. Нельзя — когда каталог OpenRouter называет его
    /// обязательным или каталог Anthropic не знает ни <c>disabled</c>, ни <c>between_tools</c>.
    /// Модель, о которой каталог ничего не сказал, выключать можно, как и прежде.
    /// </summary>
    /// <remarks>
    /// До 1.37.0-beta.7 переключатель «Размышление» был у всех моделей, и у Sonnet 5.5 или Opus 5.5
    /// «без размышления» молча становилось нижним уровнем: на кнопке одно, на проводе другое.
    /// </remarks>
    public static bool CanDisable(VeniceModelInfo? model)
    {
        var capabilities = model?.ModelSpec?.Capabilities;
        if (capabilities is null)
        {
            return true;
        }

        if (capabilities.ReasoningMandatory)
        {
            return false;
        }

        return capabilities.ThinkingModes is not { } modes ||
               modes.Any(mode => mode.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
                                 mode.Equals("between_tools", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Выбор, каким он уйдёт модели: «без размышления» у модели, которая его не выключает, — её
    /// нижний уровень. Так его и показывают кнопка и спидометр, так его и шлёт запрос.
    /// </summary>
    public static ReasoningChoice Effective(ReasoningChoice choice, VeniceModelInfo? model, bool withTools = false)
    {
        if (!choice.DisableThinking || CanDisable(model))
        {
            return choice;
        }

        var options = VisibleEffortOptions(model, withTools);
        return options.Count == 0
            ? choice
            : choice with { DisableThinking = false, Effort = options.MinBy(EffortRank) };
    }

    /// <summary>Уровни размышления по возрастанию — по этой шкале ищется ближайший.</summary>
    private static readonly string[] EffortScale = ["minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>Место уровня на шкале; незнакомый — в конце.</summary>
    public static int EffortRank(string? effort)
    {
        var index = Array.FindIndex(EffortScale, level => level.Equals(effort?.Trim(), StringComparison.OrdinalIgnoreCase));
        return index < 0 ? EffortScale.Length : index;
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

            // Просимого уровня у модели нет — ближайший по шкале, при равенстве нижний: «medium»
            // у модели с low/high/max становится low, а не дорогим high, который заявлен у неё
            // уровнем по умолчанию.
            if (Nearest(options, requested) is { } nearest)
            {
                return nearest;
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

    private static string? Nearest(IReadOnlyList<string> options, string requested)
    {
        var wanted = EffortRank(requested);
        if (wanted >= EffortScale.Length)
        {
            return null;
        }

        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var option in options)
        {
            var rank = EffortRank(option);
            if (rank >= EffortScale.Length)
            {
                continue;
            }

            var distance = Math.Abs(rank - wanted);
            if (distance < bestDistance || (distance == bestDistance && best is not null && rank < EffortRank(best)))
            {
                best = option;
                bestDistance = distance;
            }
        }

        return best;
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
        choice = Effective(choice, model, withTools);
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
        // Запрет живёт у сервера, а не у моделей: OpenAI и всё, что передаёт ему запрос как есть
        // (Venice), отвечает на усилие рядом с инструментами отказом, а тот же GPT-5.x за
        // OpenRouter его прекрасно принимает. Разбор имени модели этой разницы не видит — имена у
        // провайдеров одни и те же, — поэтому решает описание провайдера.
        if (!ProviderSpec.For(ModelRef.Of(modelId)).BlocksEffortWithTools)
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
