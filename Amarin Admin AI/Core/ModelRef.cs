namespace Amarin.Core;

/// <summary>
/// Идентификатор модели вместе с провайдером: <c>grok-4-6</c> — Venice,
/// <c>openrouter:anthropic/claude-sonnet-4.5</c> — OpenRouter.
/// </summary>
/// <remarks>
/// Провайдер едет в самой строке, а не отдельным полем, потому что выбранная модель лежит
/// строкой в девяти слотах <see cref="AppSettings"/>, в каждой переписке
/// (<c>ChatSession.SelectedModelId</c>) и в статьях расхода журнала трат. Протащить туда пару
/// значило бы переписать все три формата на диске; приставка же оставляет записи Venice
/// нетронутыми — файлы, написанные прежними версиями, читаются как были.
/// <para>
/// Разрез идёт по <b>первому</b> двоеточию. У OpenRouter бывают варианты модели —
/// <c>anthropic/claude-3.7-sonnet:thinking</c>, <c>:free</c>, <c>:nitro</c>, — и разрез по
/// последнему двоеточию молча отбросил бы вариант, то есть запросил другую модель.
/// </para>
/// </remarks>
public static class ModelRef
{
    /// <summary>
    /// Чья это модель. Пустая строка и «авто» — не модели, о провайдере они не говорят
    /// ничего, поэтому для них возвращается <paramref name="fallback"/>.
    /// </summary>
    /// <param name="fallback">
    /// Чем считать «авто» и пустую строку. Обычно — провайдер активного ключа.
    /// </param>
    public static LlmProvider Of(string? modelId, LlmProvider fallback = LlmProvider.Venice)
    {
        var id = (modelId ?? "").Trim();
        if (id.Length == 0 || VeniceModelCatalog.IsAuto(id))
        {
            return fallback;
        }

        return FindPrefix(id)?.Provider ?? LlmProvider.Venice;
    }

    /// <summary>Идентификатор без приставки — ровно то, что уходит на провод.</summary>
    public static string Bare(string? modelId)
    {
        var id = (modelId ?? "").Trim();
        var spec = FindPrefix(id);
        return spec is null ? id : id[spec.Prefix.Length..];
    }

    /// <summary>
    /// Приписывает провайдера к идентификатору. Повторный вызов ничего не меняет, поэтому
    /// звать можно и там, где неизвестно, приписан ли он уже.
    /// </summary>
    public static string Qualify(LlmProvider provider, string? modelId)
    {
        var id = (modelId ?? "").Trim();

        // «Авто» — не модель, а просьба выбрать её; приписывать провайдера не к чему.
        if (id.Length == 0 || VeniceModelCatalog.IsAuto(id))
        {
            return id;
        }

        var prefix = ProviderSpec.For(provider).Prefix;
        return prefix.Length == 0 ? Bare(id) : prefix + Bare(id);
    }

    private static ProviderSpec? FindPrefix(string id)
    {
        foreach (var spec in ProviderSpec.All)
        {
            if (spec.Prefix.Length > 0 &&
                id.StartsWith(spec.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return spec;
            }
        }

        return null;
    }
}
