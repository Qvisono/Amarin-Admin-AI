namespace Amarin.Core;

internal static class VeniceModelFallback
{
    public static readonly string[] FallbackModels =
    [
        "claude-sonnet-5",
        "grok-4-6",
        "openai-gpt-53-codex",
        "kimi-k2-7-code"
    ];

    /// <summary>
    /// Запасные модели OpenRouter.
    /// </summary>
    /// <remarks>
    /// Свои, а не общие с Venice: платить можно одним ключом, и замена моделью чужого
    /// провайдера ушла бы на сервер, к которому этого ключа не пускают, — то есть перегрузку
    /// сменил бы отказ в доступе.
    /// </remarks>
    private static readonly string[] OpenRouterFallbackModels =
    [
        "openrouter:anthropic/claude-sonnet-4.5",
        "openrouter:google/gemini-2.5-flash",
        "openrouter:openai/gpt-4.1-mini"
    ];

    private static string[] ChainFor(LlmProvider provider) =>
        provider == LlmProvider.OpenRouter ? OpenRouterFallbackModels : FallbackModels;

    /// <summary>Запрошенная модель, а следом запасные на случай её перегрузки.</summary>
    /// <remarks>
    /// «Авто» головой цепочки быть не может: это не модель, а просьба выбрать её за
    /// пользователя, и Venice отвечает на неё 404 «Specified model not found: auto».
    /// Ход помнит запрошенную модель как есть — значит отсеивать «авто» надо здесь.
    /// <para>
    /// Провайдер определяется по самой модели, а не по активному ключу: так функция остаётся
    /// чистой, а ход, начатый до смены ключа, доигрывает замены внутри своего провайдера.
    /// </para>
    /// </remarks>
    /// <param name="fallbackProvider">
    /// Чьи запасные брать, когда по самой модели судить нельзя — «авто» или пусто.
    /// </param>
    public static IReadOnlyList<string> BuildChain(
        string primaryModel,
        LlmProvider fallbackProvider = LlmProvider.Venice)
    {
        var chain = new List<string>();

        if (!string.IsNullOrWhiteSpace(primaryModel) && !VeniceModelCatalog.IsAuto(primaryModel))
        {
            chain.Add(primaryModel);
        }

        foreach (var fallback in ChainFor(ModelRef.Of(primaryModel, fallbackProvider)))
        {
            if (!chain.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            {
                chain.Add(fallback);
            }
        }

        return chain;
    }

    /// <summary>
    /// Продолжение цепочки с <paramref name="currentModel"/>: уже отказавшие модели пропускаются.
    /// </summary>
    /// <remarks>
    /// Модели может не быть в цепочке вовсе — так приходит всякий, кто просил модель не из
    /// списка запасных: выбранная маршрутизатором «Авто», модель заголовка чата, модель брифа.
    /// Тогда запрос обязан уйти именно на неё, а запасные пробуются следом. Прежде перебор в
    /// этом случае молча начинался с головы цепочки, и запрос уходил на чужую модель — а при
    /// «Авто» на саму «auto», то есть в 404.
    /// </remarks>
    public static IEnumerable<string> GetModelsFrom(
        string currentModel,
        string primaryModel,
        LlmProvider fallbackProvider = LlmProvider.Venice)
    {
        var chain = BuildChain(
            primaryModel,
            ModelRef.Of(currentModel, ModelRef.Of(primaryModel, fallbackProvider)));

        for (var i = 0; i < chain.Count; i++)
        {
            if (chain[i].Equals(currentModel, StringComparison.OrdinalIgnoreCase))
            {
                return chain.Skip(i);
            }
        }

        return string.IsNullOrWhiteSpace(currentModel) || VeniceModelCatalog.IsAuto(currentModel)
            ? chain
            : chain.Prepend(currentModel);
    }

    public static bool IsModelOverloaded(VeniceApiException exception)
    {
        var message = exception.Message;

        return message.Contains("overload", StringComparison.OrdinalIgnoreCase)
            || message.Contains("перегруж", StringComparison.OrdinalIgnoreCase)
            || message.Contains("(503)", StringComparison.OrdinalIgnoreCase)
            || message.Contains("(429)", StringComparison.OrdinalIgnoreCase)
            || message.Contains("capacity", StringComparison.OrdinalIgnoreCase)
            || message.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
            || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
    }
}