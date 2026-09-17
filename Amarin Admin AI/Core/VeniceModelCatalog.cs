namespace Amarin.Core;

/// <param name="TitleKey">Ключ строки, а не сама строка: список собирается один раз при
/// загрузке типа, до того как выбран язык, — готовый текст остался бы русским навсегда.</param>
internal sealed record ModelTierGroup(string TitleKey, string SubtitleKey, string[] Models)
{
    public string Title => Loc.Get(TitleKey);

    public string Subtitle => Loc.Get(SubtitleKey);
}

internal static class VeniceModelCatalog
{
    public const string AutoId = "auto";

    /// <summary>
    /// Preset models for the Recommended tab, grouped by reliability / cost tier.
    /// Titles match the picker mockup headers.
    /// </summary>
    public static readonly ModelTierGroup[] Tiers =
    [
        new(
            "S.Models.TierFlagship",
            "S.Models.TierFlagshipDesc",
            ["claude-sonnet-5", "grok-4-6"]),
        new(
            "S.Models.TierMiddle",
            "S.Models.TierMiddleDesc",
            ["openai-gpt-53-codex", "kimi-k2-7-code"]),
        new(
            "S.Models.TierBudget",
            "S.Models.TierBudgetDesc",
            ["minimax-m3-preview", "qwen-3-7-plus"])
    ];

    private static readonly string[] ExcludedModels =
    [
        "grok-41-fast",

        // Inkling заявляет supportsFunctionCalling, но 400-ит на любом наборе инструментов:
        // Venice не может собрать для него грамматику вызовов (см. VeniceToolSupport).
        // Проверено запросами — падает и на одном инструменте без параметров, и при любом
        // имени. Инструменты уходят в каждом ходе, так что в списке это была бы кнопка,
        // которая всегда возвращает ошибку. Строку убрать, когда Venice починит.
        "inkling"
    ];

    private static bool IsExcluded(string model) =>
        ExcludedModels.Contains(model, StringComparer.OrdinalIgnoreCase);

    public static bool IsAuto(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId) &&
        modelId.Equals(AutoId, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<VeniceModelInfo> FilterAgentic(IEnumerable<VeniceModelInfo> models)
    {
        var result = new List<VeniceModelInfo>();
        foreach (var model in models)
        {
            if (string.IsNullOrWhiteSpace(model.Id) || IsExcluded(model.Id))
            {
                continue;
            }

            // Модель уже отказала на грамматике вызовов в этом запуске — второй раз её не
            // предлагаем. Список сбрасывается при перезапуске, поэтому почин на стороне
            // Venice подхватится сам.
            if (VeniceToolSupport.IsBroken(model.Id))
            {
                continue;
            }

            if (model.ModelSpec?.Offline == true)
            {
                continue;
            }

            var caps = model.ModelSpec?.Capabilities;
            if (caps is not { SupportsFunctionCalling: true, SupportsReasoning: true })
            {
                continue;
            }

            result.Add(model);
        }

        return result;
    }

    /// <summary>Можно ли ещё выбрать эту модель, судя по загруженному каталогу.</summary>
    /// <remarks>
    /// «Авто» — не модель, а просьба выбрать её, в каталоге её нет и быть не должно.
    /// Пустой каталог означает, что список не доехал (сеть, ключ), и тогда судить не о чем:
    /// иначе разовый сбой сети стёр бы человеку все выбранные модели.
    /// </remarks>
    public static bool IsSelectable(IReadOnlyList<VeniceModelInfo>? catalog, string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || IsAuto(id))
        {
            return true;
        }

        if (catalog is null || catalog.Count == 0)
        {
            return true;
        }

        foreach (var model in catalog)
        {
            if (id.Trim().Equals(model.Id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasVision(VeniceModelInfo model) =>
        model.ModelSpec?.Capabilities?.SupportsVision == true;

    public static bool HasCode(VeniceModelInfo model)
    {
        if (model.ModelSpec?.Capabilities?.OptimizedForCode == true)
        {
            return true;
        }

        var traits = model.ModelSpec?.Traits;
        if (traits is null)
        {
            return false;
        }

        foreach (var trait in traits)
        {
            if (!string.IsNullOrWhiteSpace(trait) &&
                trait.Contains("code", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool MatchesSearch(VeniceModelInfo model, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var q = query.Trim();
        if (model.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = model.ModelSpec?.Name;
        return !string.IsNullOrWhiteSpace(name) &&
               name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<VeniceModelInfo> FilterAllTab(
        IEnumerable<VeniceModelInfo> models,
        string? search,
        bool vision,
        bool code)
    {
        foreach (var model in models)
        {
            if (vision && !HasVision(model))
            {
                continue;
            }

            if (code && !HasCode(model))
            {
                continue;
            }

            if (!MatchesSearch(model, search))
            {
                continue;
            }

            yield return model;
        }
    }

    public static string GetListDisplayName(VeniceModelInfo model)
    {
        if (!string.IsNullOrWhiteSpace(model.ModelSpec?.Name))
        {
            return model.ModelSpec.Name.Trim();
        }

        return GetDisplayName(model.Id);
    }

    public static string FormatContext(int? tokens)
    {
        if (tokens is null or <= 0)
        {
            return "";
        }

        if (tokens >= 1_000_000)
        {
            var millions = tokens.Value / 1_000_000d;
            return millions % 1 == 0
                ? $"{(int)millions}M"
                : $"{millions.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}M";
        }

        var thousands = (int)Math.Round(tokens.Value / 1000d, MidpointRounding.AwayFromZero);
        return $"{thousands}k";
    }

    public static string BuildTooltip(VeniceModelInfo model)
    {
        var parts = new List<string> { model.Id };
        var context = FormatContext(model.ModelSpec?.AvailableContextTokens ?? model.ContextLength);
        if (context.Length > 0)
        {
            parts.Add(context);
        }

        var caps = new List<string>();
        if (model.ModelSpec?.Capabilities?.SupportsReasoning == true)
        {
            caps.Add("Reason");
        }

        if (HasVision(model))
        {
            caps.Add("Vision");
        }

        if (HasCode(model))
        {
            caps.Add("Code");
        }

        if (caps.Count > 0)
        {
            parts.Add(string.Join(", ", caps));
        }

        return string.Join(" · ", parts);
    }

    public static string GetDisplayName(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) || IsAuto(modelId))
        {
            return Loc.Get("S.Models.Auto");
        }

        return modelId.Trim().ToLowerInvariant() switch
        {
            "claude-sonnet-5" => "Claude Sonnet 5",
            "grok-4-6" => "Grok 4.6",
            "grok-4-3" => "Grok 4.3",
            "openai-gpt-53-codex" => "GPT-5.3 Codex",
            "openai-gpt-56-luna" => "GPT-5.6 Luna",
            "kimi-k2-7-code" => "Kimi K2.7 Code",
            "deepseek-v4-flash-0731-fast" => "DeepSeek V4 Flash",
            "minimax-m3-preview" => "MiniMax M3 Preview",
            "qwen-3-7-plus" => "Qwen 3.7 Plus",
            _ => Humanize(modelId.Trim())
        };
    }

    public static string GetLogoLetter(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) ||
            modelId.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return "A";
        }

        var id = modelId.Trim();
        foreach (var ch in id)
        {
            if (char.IsLetterOrDigit(ch))
            {
                return char.ToUpperInvariant(ch).ToString();
            }
        }

        return "?";
    }

    /// <summary>
    /// Model id fragment → logo key in AiLogos.*.xaml, most specific first.
    /// <para>
    /// Order is load-bearing: "grok-imagine-image" must reach Grok before anything matches on
    /// "image", and a vendor's own name has to lose to its model family — "qwen-image" is Qwen,
    /// not Alibaba. Add new entries above the vendor fallbacks at the bottom.
    /// </para>
    /// </summary>
    private static readonly (string Needle, string Key)[] LogoKeys =
    [
        // Chat models
        ("claude", "Claude"),
        ("grok", "Grok"),
        ("deepseek", "DeepSeek"),
        ("kimi", "Kimi"),
        ("moonshot", "Moonshot"),
        ("minimax", "MiniMax"),
        ("mimo", "MiMo"),
        ("glm", "GLM"),
        ("qwen", "Qwen"),
        ("gemma", "Gemma"),
        ("gemini", "GoogleGemini"),
        ("llama", "Llama"),
        ("nemotron", "NVIDIA"),
        ("nvidia", "NVIDIA"),
        ("hunyuan", "Hunyuan"),
        ("baichuan", "Baichuan"),
        ("arcee", "Arcee"),
        ("aion", "AionLabs"),
        ("mercury", "Inception"),
        ("inception", "Inception"),
        ("spark", "Spark"),
        ("perplexity", "Perplexity"),
        ("sonar", "Perplexity"),
        ("cohere", "Cohere"),
        ("command-r", "Cohere"),

        // Mistral's family names share no common substring.
        ("mistral", "Mistral"),
        ("ministral", "Mistral"),
        ("magistral", "Mistral"),
        ("codestral", "Mistral"),
        ("devstral", "Mistral"),
        ("pixtral", "Mistral"),

        // OpenAI
        ("openai", "OpenAI"),
        ("gpt", "OpenAI"),
        ("codex", "OpenAI"),

        // Image models
        ("nano-banana", "Google"),
        ("flux", "Flux"),
        // ByteDance's own sub-brands must win over the plain "seed" family below them —
        // "seedream-3-5".Contains("seed") is also true, and the loop returns the first hit.
        ("seedream", "ByteDance"),
        ("seedance", "ByteDance"),
        ("bytedance", "ByteDance"),
        ("doubao", "ByteDance"),
        ("seed", "Seed"),
        ("venice-sd", "Stability"),
        ("stable-diffusion", "Stability"),
        ("sdxl", "Stability"),

        // Video models
        ("kling", "Kling"),
        ("pixverse", "PixVerse"),
        ("hailuo", "Hailuo"),
        ("runway", "Runway"),
        ("luma", "Luma"),
        ("pika", "Pika"),
        ("vidu", "Vidu"),

        // Vendor fallbacks for ids that name the maker rather than the model.
        ("anthropic", "Anthropic"),
        ("google", "Google"),
        ("alibaba", "Alibaba"),
    ];

    public static string? GetLogoResourceKey(string modelId)
    {
        if (IsAuto(modelId))
        {
            return "Auto";
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var id = modelId.Trim().ToLowerInvariant();
        foreach (var (needle, key) in LogoKeys)
        {
            if (id.Contains(needle, StringComparison.Ordinal))
            {
                return key;
            }
        }

        return null;
    }

    private static string Humanize(string modelId)
    {
        var parts = modelId.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(static part =>
            part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
