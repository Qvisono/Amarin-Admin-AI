namespace Amarin.Core;

/// <summary>За что списали: модель либо служебная статья расхода.</summary>
/// <param name="ModelId">Найденная модель, либо <c>null</c>, если это не модель.</param>
/// <param name="KindKey">Ключ подписи для служебной статьи, либо <c>null</c>.</param>
/// <param name="Sku">Исходная строка Venice — для подсказки строки.</param>
public sealed record SkuIdentity(string? ModelId, string? KindKey, string Sku);

/// <summary>
/// Разбор <c>sku</c> из журнала трат Venice.
/// </summary>
/// <remarks>
/// Venice называет товар вроде <c>zai-org-glm-5-1-llm-output-mtoken</c>: модель плюс хвост
/// с единицей измерения. Вход и выход одной модели обязаны сойтись в одну строку разбивки —
/// иначе у человека с единственной моделью получилось бы две строки, дающие в сумме итог,
/// и он честно решил бы, что программа считает дважды.
/// </remarks>
internal static class VeniceSku
{
    /// <summary>
    /// Хвосты единиц измерения. Длинные раньше коротких: <c>-llm-input-mtoken</c> обязан
    /// сработать до <c>-mtoken</c>, иначе от модели откусят только половину хвоста.
    /// </summary>
    private static readonly string[] Units =
    [
        "-llm-input-cached-mtoken",
        "-llm-input-write-cache-mtoken",
        "-llm-reasoning-mtoken",
        "-llm-output-mtoken",
        "-llm-input-mtoken",
        "-input-mtoken",
        "-output-mtoken",
        "-mtoken",
        "-ktoken",
        "-token",
        "-request",
        "-second",
        "-minute",
        "-image",
        "-call"
    ];

    /// <summary>
    /// Служебные статьи: то, что списывается не за модель. Ищутся по куску имени, потому что
    /// точный набор sku Venice не публикует и меняет его чаще, чем выходит эта программа.
    /// </summary>
    private static readonly (string Needle, string Key)[] Kinds =
    [
        ("websearch", "S.Spend.Kind.WebSearch"),
        ("web-search", "S.Spend.Kind.WebSearch"),
        ("search", "S.Spend.Kind.WebSearch"),
        ("scrape", "S.Spend.Kind.Scrape"),
        ("augment", "S.Spend.Kind.Scrape"),
        ("upscale", "S.Spend.Kind.Image"),
        ("inpaint", "S.Spend.Kind.Image"),
        ("image", "S.Spend.Kind.Image"),
        ("video", "S.Spend.Kind.Video"),
        ("speech", "S.Spend.Kind.Audio"),
        ("audio", "S.Spend.Kind.Audio"),
        ("tts", "S.Spend.Kind.Audio"),
        ("embedding", "S.Spend.Kind.Embedding")
    ];

    /// <summary>
    /// Сводит sku к модели или к служебной статье.
    /// </summary>
    /// <param name="known">
    /// Каталог моделей. Он неполон намеренно — там только текстовые модели с инструментами, —
    /// поэтому картиночные и не-агентные сюда не попадут и уйдут в служебные статьи или
    /// в «прочее». Второй запрос к сети ради красивых имён того не стоит.
    /// </param>
    public static SkuIdentity Resolve(string? sku, IReadOnlyList<VeniceModelInfo>? known)
    {
        var raw = (sku ?? "").Trim();
        if (raw.Length == 0)
        {
            return new SkuIdentity(null, "S.Spend.Kind.Unknown", "");
        }

        var lowered = raw.ToLowerInvariant();
        var stem = StripUnit(lowered);

        if (MatchModel(stem, known) is { } model)
        {
            return new SkuIdentity(model, null, raw);
        }

        foreach (var (needle, key) in Kinds)
        {
            if (lowered.Contains(needle, StringComparison.Ordinal))
            {
                return new SkuIdentity(null, key, raw);
            }
        }

        // Ни модель, ни знакомая статья: остаток всё равно показываем как имя — «прочее»
        // на всю незнакомую половину счёта выглядело бы так, будто деньги прячут.
        return stem.Length > 0
            ? new SkuIdentity(stem, null, raw)
            : new SkuIdentity(null, "S.Spend.Kind.Unknown", raw);
    }

    internal static string StripUnit(string lowered)
    {
        foreach (var unit in Units)
        {
            if (lowered.EndsWith(unit, StringComparison.Ordinal))
            {
                return lowered[..^unit.Length];
            }
        }

        return lowered;
    }

    /// <summary>
    /// Ищет модель каталога в остатке sku. Выигрывает самый длинный подошедший идентификатор:
    /// иначе <c>grok-4-6</c> съел бы траты <c>grok-4-6-fast</c>, и быстрая модель выглядела бы
    /// бесплатной.
    /// </summary>
    private static string? MatchModel(string stem, IReadOnlyList<VeniceModelInfo>? known)
    {
        if (known is null || known.Count == 0 || stem.Length == 0)
        {
            return null;
        }

        string? best = null;
        foreach (var model in known)
        {
            var id = model.Id?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            if (stem.Equals(id, StringComparison.Ordinal) ||
                stem.EndsWith("-" + id, StringComparison.Ordinal))
            {
                if (best is null || id.Length > best.Length)
                {
                    best = model.Id;
                }
            }
        }

        return best;
    }

    /// <summary>Подпись строки разбивки.</summary>
    public static string Title(SkuIdentity identity) =>
        identity.KindKey is { } key
            ? Loc.Get(key)
            : VeniceModelCatalog.GetDisplayName(identity.ModelId ?? "");

    /// <summary>
    /// Ключ группировки: по нему вход и выход одной модели складываются в одну строку.
    /// </summary>
    public static string GroupKey(SkuIdentity identity) =>
        identity.ModelId ?? identity.KindKey ?? "unknown";
}
