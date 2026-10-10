using System.Text.RegularExpressions;

namespace Amarin.Core;

/// <summary>
/// Одна и та же модель у разных провайдеров.
/// </summary>
/// <remarks>
/// Claude Haiku 5.5 у Venice — <c>claude-haiku-5-5</c>, у OpenRouter —
/// <c>anthropic/claude-haiku-5.5</c>, у Anthropic напрямую — снова <c>claude-haiku-5-5</c>, а у
/// перепродавца — как он сам назвал. Заводские модели записаны идентификаторами Venice, и без
/// этого сопоставления человек только с ключом OpenRouter или Anthropic получал бы вместо них
/// первую попавшуюся модель каталога.
/// <para>
/// Семейство — имя без различий в написании (<see cref="Normalize"/>): регистр, точка или тире
/// в номере версии, дата снимка и приставка производителя. Где имя у провайдера своё, известные
/// написания перечислены явно: по ним же модель находится, когда каталог ещё не загружен.
/// </para>
/// </remarks>
internal static partial class ModelFamily
{
    /// <summary>Известные идентификаторы семейства у провайдеров, где имя не выводится из ключа.</summary>
    private static readonly Dictionary<string, (LlmProvider Provider, string Id)[]> Known = new(StringComparer.Ordinal)
    {
        ["claude-haiku-5-5"] =
        [
            (LlmProvider.OpenRouter, "openrouter:anthropic/claude-haiku-5.5"),
            (LlmProvider.Anthropic, "anthropic:claude-haiku-5-5")
        ],
        ["claude-sonnet-5-5"] =
        [
            (LlmProvider.OpenRouter, "openrouter:anthropic/claude-sonnet-5.5"),
            (LlmProvider.Anthropic, "anthropic:claude-sonnet-5-5")
        ],
        ["deepseek-v4-flash-0731"] =
        [
            (LlmProvider.OpenRouter, "openrouter:deepseek/deepseek-v4-flash-0731")
        ],
        ["grok-4-7"] =
        [
            (LlmProvider.OpenRouter, "openrouter:x-ai/grok-4.7")
        ]
    };

    /// <summary>
    /// Другие имена того же семейства. У DeepSeek напрямую Flash — это <c>deepseek-flash</c>:
    /// снимки там не называют, под именем всегда последний.
    /// </summary>
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.Ordinal)
    {
        ["deepseek-v4-flash-0731"] = ["deepseek-v4-flash", "deepseek-flash"]
    };

    /// <summary>Семейство модели по её идентификатору у любого провайдера.</summary>
    public static string KeyOf(string? modelId) =>
        string.IsNullOrWhiteSpace(modelId) ? "" : Normalize(ModelRef.Bare(modelId));

    /// <summary>
    /// Модель того же семейства у провайдера. <c>null</c> — такой у него нет или, без каталога,
    /// её имя неизвестно.
    /// </summary>
    /// <param name="catalog">
    /// Каталог провайдера. Пустой — судить можно только по известным написаниям.
    /// </param>
    public static string? Find(string family, LlmProvider provider, IReadOnlyList<VeniceModelInfo>? catalog)
    {
        if (string.IsNullOrWhiteSpace(family) || VeniceModelCatalog.IsAuto(family))
        {
            return null;
        }

        // Идентификаторы Venice и есть ключи семейств.
        var known = provider == LlmProvider.Venice
            ? family
            : Known.GetValueOrDefault(family)?.FirstOrDefault(entry => entry.Provider == provider).Id;

        if (catalog is not { Count: > 0 })
        {
            return known;
        }

        if (known is not null && catalog.Any(model => model.Id.Equals(known, StringComparison.OrdinalIgnoreCase)))
        {
            return known;
        }

        var names = new HashSet<string>(StringComparer.Ordinal) { family };
        names.UnionWith(Aliases.GetValueOrDefault(family) ?? []);

        // Первое совпадение по семейству, затем по синонимам: «deepseek-v4-flash» без даты — тот же
        // Flash, но точное имя снимка лучше.
        return catalog.FirstOrDefault(model => KeyOf(model.Id) == family)?.Id
               ?? catalog.FirstOrDefault(model => names.Contains(KeyOf(model.Id)))?.Id;
    }

    /// <summary>
    /// Имя модели без различий в написании: у Anthropic <c>claude-haiku-5-5</c>, у OpenRouter
    /// <c>claude-haiku-5.5</c>, у снимков — дата в конце, у шлюзов — приставка производителя.
    /// </summary>
    public static string Normalize(string id)
    {
        var name = id.Trim().ToLowerInvariant();
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        name = name.Replace('.', '-');
        return DateSuffix().Replace(name, "");
    }

    [GeneratedRegex(@"-(\d{8}|\d{4}-\d{2}-\d{2})$")]
    private static partial Regex DateSuffix();
}
