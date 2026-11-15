namespace Amarin.Core;

internal static class VeniceModelCatalog
{
    public const string AutoId = "auto";

    /// <summary>
    /// Чей это каталог. Пустой — судить не о чем, и остаётся Venice: он был единственным.
    /// </summary>
    public static LlmProvider ProviderOf(IReadOnlyList<VeniceModelInfo>? catalog) =>
        catalog is { Count: > 0 } ? ModelRef.Of(catalog[0].Id) : LlmProvider.Venice;

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

    public static bool IsDecisionModel(VeniceModelInfo? model) =>
        model is not null && ModelRef.Of(model.Id) == LlmProvider.OpenRouter &&
        string.Equals(model.Type, "decisions", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Модели для поля выбора слота: Decisions доступны маршрутизатору и SynGuard (остальные слоты
    /// ведут разговор), а модели, запрещённые политикой организации (1.37.0), не видны никому.
    /// </summary>
    public static IReadOnlyList<VeniceModelInfo> ForSlot(
        IReadOnlyList<VeniceModelInfo> models, ModelSlot slot, bool includeRoutingSources = false)
    {
        var shown = slot is ModelSlot.Router or ModelSlot.SynGuard || !models.Any(IsDecisionModel)
            ? models : [.. models.Where(model => !IsDecisionModel(model))];
        var policy = OrganizationPolicy.Current;
        return policy.AllowedModels.Count == 0 && policy.AllowedProviders.Count == 0
            ? shown
            : [.. shown.Where(model => policy.AllowsModel(model.Id) ||
                (includeRoutingSources && CanUseOpenRouterRouting(model) &&
                 policy.AllowsProvider(LlmProvider.OpenRouter) &&
                 ModelRef.OpenRouterRoutingVariants.Any(route =>
                     policy.AllowsModel(ModelRef.WithOpenRouterRouting(model.Id, route)))))];
    }

    /// <summary>У каких записей меню может предложить маршрутизацию OpenRouter.</summary>
    public static bool CanUseOpenRouterRouting(VeniceModelInfo model)
    {
        if (ModelRef.Of(model.Id) != LlmProvider.OpenRouter || IsDecisionModel(model) ||
            ModelRef.IsBatchOnly(model.Id) || ModelRef.OpenRouterRouting(model.Id).Length != 0)
        {
            return false;
        }

        var bare = ModelRef.Bare(model.Id);
        return bare.Contains('/', StringComparison.Ordinal) && !bare.StartsWith('~') &&
               !bare.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<VeniceModelInfo> FilterAgentic(
        IEnumerable<VeniceModelInfo> models, bool includeDecisions = false)
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

            if (IsDecisionModel(model))
            {
                if (includeDecisions)
                {
                    result.Add(model);
                }

                continue;
            }

            var caps = model.ModelSpec?.Capabilities;
            if (caps is not { SupportsFunctionCalling: true })
            {
                continue;
            }

            // Размышление требуется только от моделей Venice: у него рассуждают все, кто вообще
            // годится для инструментов, и отсутствие признака означает старую слабую модель.
            // У OpenRouter это не так — Gemini Flash и Command-R инструменты вызывают прекрасно,
            // а не рассуждают, и то же требование выбросило бы из списка половину хорошего.
            if (ModelRef.Of(model.Id) == LlmProvider.Venice && !caps.SupportsReasoning)
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

        var requested = id.Trim();
        if (ModelRef.OpenRouterRouting(requested).Length != 0 && !OrganizationPolicy.Current.AllowsModel(requested))
        {
            return false;
        }

        var found = Find(catalog, requested);
        return found is not null &&
               (requested.Equals(found.Id, StringComparison.OrdinalIgnoreCase) || CanUseOpenRouterRouting(found));
    }

    public static VeniceModelInfo? Find(IEnumerable<VeniceModelInfo>? catalog, string? id)
    {
        if (catalog is null || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var requested = id.Trim();
        var catalogId = ModelRef.WithoutOpenRouterRouting(requested);
        VeniceModelInfo? fallback = null;
        foreach (var model in catalog)
        {
            if (requested.Equals(model.Id, StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }

            if (catalogId.Equals(model.Id, StringComparison.OrdinalIgnoreCase))
            {
                fallback ??= model;
            }
        }

        return fallback;
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

    public static string BuildTooltip(VeniceModelInfo model, string? modelId = null)
    {
        modelId ??= model.Id;
        var parts = new List<string> { modelId };
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

        var line = string.Join(" · ", parts);

        // Пометка «(batch)» в имени модели не говорит человеку главного — что ответ придёт
        // из очереди и ждать его можно часами. Цена вдвое ниже, и без этой строки выбор
        // выглядит просто выгодным.
        return ModelRef.IsBatchOnly(modelId)
            ? line + Environment.NewLine + Loc.Get("S.Models.BatchHint")
            : line;
    }

    public static string GetDisplayName(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) || IsAuto(modelId))
        {
            return Loc.Get("S.Models.Auto");
        }

        // Приставка провайдера — наша выдумка для настроек и переписок, человеку её показывать
        // незачем. У OpenRouter впереди ещё и производитель через косую черту, а его в списке
        // уже рисует логотип: «Openrouter:anthropic/claude Sonnet 4.5» вместо «Claude Sonnet
        // 4.5» вылезало бы и в выпадашке, и в подписи агента, и в справке о моделях внутри
        // системного промпта.
        var bare = ModelRef.Bare(modelId);
        var slash = bare.LastIndexOf('/');
        if (slash >= 0 && slash < bare.Length - 1)
        {
            bare = bare[(slash + 1)..];
        }

        return bare.Trim().ToLowerInvariant() switch
        {
            // Номер версии у Venice и Anthropic пишется через тире: без строки «claude-haiku-5-5»
            // стал бы «Claude Haiku 5 5».
            "claude-haiku-5-5" => "Claude Haiku 5.5",
            "claude-sonnet-5-5" => "Claude Sonnet 5.5",
            "claude-opus-5-5" => "Claude Opus 5.5",
            "grok-4-7" => "Grok 4.7",
            "claude-sonnet-5" => "Claude Sonnet 5",
            "grok-4-6" => "Grok 4.6",
            "grok-4-3" => "Grok 4.3",
            "openai-gpt-53-codex" => "GPT-5.3 Codex",
            "openai-gpt-56-luna" => "GPT-5.6 Luna",
            "kimi-k2-7-code" => "Kimi K2.7 Code",
            "deepseek-v4-flash-0731" => "DeepSeek V4 Flash",
            // Короткое имя обязано нести «Fast»: без него эта модель и deepseek-v4-flash-0731
            // выглядят в настройках одинаково, и понять, какая где выбрана, нельзя.
            "deepseek-v4-flash-0731-fast" => "DeepSeek V4 Flash Fast",
            "minimax-m3-preview" => "MiniMax M3 Preview",
            "qwen-3-7-plus" => "Qwen 3.7 Plus",
            _ => Humanize(bare.Trim())
        };
    }

    public static string GetLogoLetter(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) ||
            modelId.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return "A";
        }

        // Без приставки: иначе у всех моделей OpenRouter буква была бы одна и та же — «O».
        var id = ModelRef.Bare(modelId).Trim();
        foreach (var ch in id)
        {
            if (char.IsLetterOrDigit(ch))
            {
                return char.ToUpperInvariant(ch).ToString();
            }
        }

        return "?";
    }

    /// <summary>Ключ логотипа по автору и семейству модели, без приставки провайдера.</summary>
    public static string? GetLogoResourceKey(string modelId) => ModelLogoCatalog.GetResourceKey(modelId);

    private static string Humanize(string modelId)
    {
        var parts = modelId.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(static part =>
            part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
