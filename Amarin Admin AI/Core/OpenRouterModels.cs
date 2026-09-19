using System.Text.Json.Serialization;

namespace Amarin.Core;

// ───────────────────────── GET /models ─────────────────────────

internal sealed class OpenRouterModelsResponse
{
    [JsonPropertyName("data")]
    public List<OpenRouterModel> Data { get; init; } = [];
}

internal sealed class OpenRouterModel
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("created")]
    public long? Created { get; init; }

    [JsonPropertyName("context_length")]
    public int? ContextLength { get; init; }

    [JsonPropertyName("architecture")]
    public OpenRouterArchitecture? Architecture { get; init; }

    /// <summary>
    /// Список имён параметров, которые модель принимает: <c>tools</c>, <c>reasoning</c> и прочее.
    /// Единственный способ узнать у OpenRouter, умеет ли модель вызывать инструменты.
    /// </summary>
    [JsonPropertyName("supported_parameters")]
    public List<string>? SupportedParameters { get; init; }
}

internal sealed class OpenRouterArchitecture
{
    [JsonPropertyName("input_modalities")]
    public List<string>? InputModalities { get; init; }

    [JsonPropertyName("output_modalities")]
    public List<string>? OutputModalities { get; init; }

    [JsonPropertyName("tokenizer")]
    public string? Tokenizer { get; init; }
}

// ───────────────────────── GET /key, GET /credits ─────────────────────────

internal sealed class OpenRouterKeyResponse
{
    [JsonPropertyName("data")]
    public OpenRouterKeyData? Data { get; init; }
}

internal sealed class OpenRouterKeyData
{
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>Сколько с ключа уже потрачено, в долларах.</summary>
    [JsonPropertyName("usage")]
    public decimal? Usage { get; init; }

    /// <summary>Потолок трат по этому ключу. <c>null</c> — потолка нет.</summary>
    [JsonPropertyName("limit")]
    public decimal? Limit { get; init; }

    /// <summary>Остаток до потолка. Приходит только когда потолок задан.</summary>
    [JsonPropertyName("limit_remaining")]
    public decimal? LimitRemaining { get; init; }

    [JsonPropertyName("is_free_tier")]
    public bool IsFreeTier { get; init; }
}

internal sealed class OpenRouterCreditsResponse
{
    [JsonPropertyName("data")]
    public OpenRouterCreditsData? Data { get; init; }
}

internal sealed class OpenRouterCreditsData
{
    [JsonPropertyName("total_credits")]
    public decimal? TotalCredits { get; init; }

    [JsonPropertyName("total_usage")]
    public decimal? TotalUsage { get; init; }
}

// ───────────────────────── перевод в общий вид ─────────────────────────

/// <summary>
/// Переводит ответы OpenRouter в те же типы, которыми программа описывает Venice.
/// </summary>
/// <remarks>
/// Так весь остальной код — выпадашка моделей, поиск по ней, чипы «зрение» и «код»,
/// подсказки, справка о моделях в системном промпте, кольцо контекста и плашка остатка —
/// продолжает работать без единой правки. Альтернатива, свой тип модели на провайдера,
/// потребовала бы ветвления в каждом из этих мест.
/// </remarks>
internal static class OpenRouterMapper
{
    /// <summary>
    /// Значения <c>reasoning_effort</c>, которые принимает OpenRouter. У Venice этот список
    /// приходит из каталога, здесь его приходится знать.
    /// </summary>
    private static readonly List<string> EffortOptions = ["low", "medium", "high"];

    public static IReadOnlyList<VeniceModelInfo> ToModels(OpenRouterModelsResponse response)
    {
        var models = new List<VeniceModelInfo>(response.Data.Count);
        foreach (var model in response.Data)
        {
            if (string.IsNullOrWhiteSpace(model.Id))
            {
                continue;
            }

            models.Add(ToModel(model));
        }

        return models;
    }

    private static VeniceModelInfo ToModel(OpenRouterModel model)
    {
        var parameters = model.SupportedParameters ?? [];
        var reasoning = Has(parameters, "reasoning") || Has(parameters, "include_reasoning");
        var effort = Has(parameters, "reasoning_effort") || reasoning;

        return new VeniceModelInfo
        {
            Id = ModelRef.Qualify(LlmProvider.OpenRouter, model.Id),
            Object = "model",
            Type = "text",
            OwnedBy = Vendor(model.Id),
            Created = model.Created,
            ContextLength = model.ContextLength,
            ModelSpec = new VeniceModelSpec
            {
                Name = DisplayName(model),
                Description = model.Description,
                AvailableContextTokens = model.ContextLength,
                Offline = false,
                Beta = false,
                Traits = Traits(model),
                Capabilities = new VeniceModelCapabilities
                {
                    SupportsFunctionCalling = Has(parameters, "tools"),
                    SupportsReasoning = reasoning,
                    SupportsReasoningEffort = effort,

                    // У OpenRouter нет запрета на усилие размышления рядом с инструментами —
                    // того самого, из-за которого Venice четырёхсотит семейство GPT-5.x.
                    // Явное «да» здесь нужно, чтобы решение не свалилось на разбор имени
                    // модели: имена у OpenRouter те же, а поведение другое.
                    SupportsReasoningEffortWithTools = true,
                    ReasoningEffortOptions = effort ? EffortOptions : null,
                    SupportsVision = Modality(model, "image"),
                    OptimizedForCode = LooksLikeCode(model)
                }
            }
        };
    }

    /// <summary>
    /// Имя для списка. OpenRouter пишет его как «Anthropic: Claude Sonnet 4.5», а в выпадашке
    /// производитель уже нарисован логотипом — второй раз называть его незачем, тем более что
    /// на 250 точках ширины имя от этого обрезается многоточием.
    /// </summary>
    private static string DisplayName(OpenRouterModel model)
    {
        var name = (model.Name ?? "").Trim();
        if (name.Length == 0)
        {
            return VeniceModelCatalog.GetDisplayName(ModelRef.Qualify(LlmProvider.OpenRouter, model.Id));
        }

        var colon = name.IndexOf(':');
        if (colon > 0 && colon < name.Length - 1)
        {
            var tail = name[(colon + 1)..].Trim();
            if (tail.Length > 0)
            {
                return tail;
            }
        }

        return name;
    }

    private static string? Vendor(string id)
    {
        var slash = id.IndexOf('/');
        return slash > 0 ? id[..slash] : null;
    }

    /// <summary>
    /// Приметы вместо списка свойств: у OpenRouter поля «на что модель заточена» нет вовсе,
    /// а чип «Код» в выпадашке без этого не отобрал бы ни одной модели и выглядел бы сломанным.
    /// </summary>
    private static bool LooksLikeCode(OpenRouterModel model)
    {
        var haystack = model.Id + " " + (model.Name ?? "");
        return haystack.Contains("code", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("coder", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("devstral", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string>? Traits(OpenRouterModel model)
    {
        var traits = new List<string>();
        if (Modality(model, "image"))
        {
            traits.Add("vision");
        }

        if (LooksLikeCode(model))
        {
            traits.Add("code");
        }

        return traits.Count > 0 ? traits : null;
    }

    private static bool Modality(OpenRouterModel model, string modality) =>
        model.Architecture?.InputModalities?.Any(
            item => string.Equals(item, modality, StringComparison.OrdinalIgnoreCase)) == true;

    private static bool Has(List<string> parameters, string name)
    {
        foreach (var parameter in parameters)
        {
            if (string.Equals(parameter, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Остаток по ключу в том же виде, в каком его отдаёт Venice, — чтобы страница настроек
    /// не разбиралась, чей это ключ.
    /// </summary>
    /// <param name="key">Ответ <c>GET /key</c>.</param>
    /// <param name="credits">
    /// Ответ <c>GET /credits</c>, если его спрашивали. Спрашивают только когда у ключа нет
    /// своего потолка трат: с потолком остаток до него точнее общего остатка счёта.
    /// </param>
    public static VeniceRateLimitsData ToRateLimits(
        OpenRouterKeyData? key,
        OpenRouterCreditsData? credits)
    {
        var remaining = key?.LimitRemaining
                        ?? (credits is null
                            ? null
                            : (credits.TotalCredits ?? 0m) - (credits.TotalUsage ?? 0m));

        return new VeniceRateLimitsData
        {
            // Ключ ответил — значит пускает. Отдельного признака доступа у OpenRouter нет.
            AccessPermitted = true,
            Balances = new VeniceKeyBalances { Usd = remaining },
            SpentUsd = key?.Usage ?? credits?.TotalUsage,
            ApiTier = new VeniceApiTier
            {
                Id = key?.IsFreeTier == true ? "free" : "paid",
                IsCharged = key?.IsFreeTier != true
            }
        };
    }
}
