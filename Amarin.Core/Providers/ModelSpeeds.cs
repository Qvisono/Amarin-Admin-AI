namespace Amarin.Core;

/// <summary>
/// Скорость модели (1.37.0): какие уровни обслуживания модель умеет и как их назвать на проводе.
/// </summary>
/// <remarks>
/// Ни один каталог этого не сообщает: ни <c>/models</c> OpenAI и Anthropic, ни общий список
/// OpenRouter. Поэтому таблица одна, по провайдеру и семейству, как у <see cref="ModelFamily"/>, —
/// и всё, чего в ней нет, скорости не получает: Venice, свои серверы человека, прочие провайдеры.
/// Модель, у которой уровня на деле не окажется, отвечает 400 о поле, и запрос повторяется без
/// него (<c>VeniceClient.Relaxed</c>, <c>AnthropicMessages.MayRelax</c>).
/// <para>
/// Fast mode у Claude есть только у официального API: <c>speed: "fast"</c> с бета-заголовком. У
/// OpenAI и OpenRouter это <c>service_tier</c>; быстрый уровень уходит старым именем
/// <c>priority</c> — его принимают оба, а новое <c>fast</c> знают не все версии API.
/// </para>
/// </remarks>
internal static class ModelSpeeds
{
    /// <summary>Дешевле и медленнее: цена очереди, ответ может ждать мощностей.</summary>
    public const string Flex = "flex";

    /// <summary>Быстрее обычного за двойную цену.</summary>
    public const string Fast = "fast";

    /// <summary>Самый быстрый уровень OpenAI.</summary>
    public const string Ultrafast = "ultrafast";

    /// <summary>Бета Anthropic для <c>speed: "fast"</c>.</summary>
    public const string AnthropicFastBeta = "fast-mode-2026-02-01";

    private static readonly string[] FlexFast = [Flex, Fast];

    private static readonly string[] All = [Flex, Fast, Ultrafast];

    private static readonly string[] FastOnly = [Fast];

    /// <summary>Уровни модели по возрастанию скорости. Пусто — у модели выбора нет.</summary>
    public static IReadOnlyList<string> For(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return [];
        }

        // Nitro сам выбирает самый быстрый маршрут: свой уровень обслуживания поверх него не нужен.
        if (ModelRef.IsNitro(modelId))
        {
            return [];
        }

        var provider = ModelRef.Of(modelId);
        var bare = ModelRef.Bare(modelId);
        var slash = bare.IndexOf('/');
        var vendor = slash > 0 ? bare[..slash].ToLowerInvariant() : "";

        // Хвост варианта OpenRouter (:thinking, :batch) семейство не меняет.
        var name = ModelFamily.Normalize(bare.Split(':')[0]);

        if (provider == LlmProvider.OpenAI)
        {
            return OpenAi(name);
        }

        if (provider == LlmProvider.Anthropic)
        {
            return Claude(name);
        }

        if (provider != LlmProvider.OpenRouter)
        {
            return [];
        }

        return vendor switch
        {
            "openai" => OpenAi(name),
            "anthropic" => Claude(name),
            "google" when name.StartsWith("gemini", StringComparison.Ordinal) => FlexFast,
            "x-ai" when name.StartsWith("grok", StringComparison.Ordinal) => FastOnly,
            _ => []
        };
    }

    /// <summary>Уровень из выбора, если модель его умеет; иначе <c>null</c> — обычная скорость.</summary>
    public static string? Pick(ReasoningChoice? choice, string? modelId)
    {
        var speed = choice?.Speed?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(speed) || !For(modelId).Contains(speed) ? null : speed;
    }

    /// <summary>Значение <c>service_tier</c> для OpenAI и OpenRouter.</summary>
    public static string? ServiceTier(ReasoningChoice? choice, string? modelId) =>
        Pick(choice, modelId) is { } speed ? (speed == Fast ? "priority" : speed) : null;

    /// <summary>
    /// Во сколько раз уровень дороже обычного — для оценки цены у провайдеров, которые её не
    /// называют. Ultrafast — по прайсу GPT-6 Astra (×6 к обычному).
    /// </summary>
    public static decimal PriceFactor(string? servedTier) => servedTier?.Trim().ToLowerInvariant() switch
    {
        Flex => 0.5m,
        "priority" or Fast => 2m,
        Ultrafast => 6m,
        _ => 1m
    };

    /// <summary>Подпись уровня для человека.</summary>
    public static string Label(string? speed) => speed?.Trim().ToLowerInvariant() switch
    {
        Flex => Loc.Get("S.Speed.Flex"),
        Fast => Loc.Get("S.Speed.Fast"),
        Ultrafast => Loc.Get("S.Speed.Ultrafast"),
        _ => Loc.Get("S.Speed.Standard")
    };

    /// <summary>
    /// GPT-5 и GPT-6 любого размера, o3 и o4-mini: Flex и Fast; GPT-6 Astra и GPT-6.1 Sol —
    /// ещё и Ultrafast.
    /// </summary>
    private static IReadOnlyList<string> OpenAi(string name)
    {
        if (name.StartsWith("gpt-6-astra", StringComparison.Ordinal) ||
            name.StartsWith("gpt-6-1-sol", StringComparison.Ordinal))
        {
            return All;
        }

        return name.StartsWith("gpt-5", StringComparison.Ordinal) ||
               name.StartsWith("gpt-6", StringComparison.Ordinal) ||
               name is "o3" or "o4-mini"
            ? FlexFast
            : [];
    }

    /// <summary>Fast mode Anthropic: Opus 5, Opus 5.5 и Opus 4.8.</summary>
    private static IReadOnlyList<string> Claude(string name) =>
        name.StartsWith("claude-opus-5", StringComparison.Ordinal) ||
        name.StartsWith("claude-opus-4-8", StringComparison.Ordinal)
            ? FastOnly
            : [];
}
