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
    /// <summary>Поиск в интернете.</summary>
    public const string WebSearch = "web-search-request";

    /// <summary>Чтение страницы целиком.</summary>
    public const string Scrape = "augment-scrape-request";

    /// <summary>Скрытая сводка переписки.</summary>
    public const string ChatSummary = "chat-summary-request";

    /// <summary>Поиск по содержимому чатов через модель.</summary>
    public const string ChatSearch = "chat-search-request";

    /// <summary>Придуманный заголовок чата.</summary>
    public const string ChatTitle = "chat-title-request";

    /// <summary>Выбор модели под задачу — и в чате, и у агента.</summary>
    public const string Router = "router-request";

    /// <summary>Проверка SynGuard.</summary>
    public const string Guard = "synguard-request";

    /// <summary>Перевод строк интерфейса на новый язык.</summary>
    public const string Translate = "ui-translate-request";

    /// <summary>Разъяснение опасного действия или записи журнала.</summary>
    public const string Explain = "explain-request";

    /// <summary>Разбор сообщения, дописанного человеком во время работы агентов.</summary>
    public const string FollowUp = "follow-up-request";

    /// <summary>Бриф к инфографике. Сама картинка уходит в статью картинок своим sku.</summary>
    public const string Infographic = "infographic-request";

    /// <summary>
    /// Наши собственные пометки — разбираются точным совпадением, а не вхождением подстроки.
    /// </summary>
    /// <remarks>
    /// Вхождением искать нельзя: <c>search</c> из таблицы <see cref="Kinds"/> перехватил бы
    /// <see cref="ChatSearch"/>, и поиск по чатам попал бы в строку поиска в интернете. А строки
    /// эти известны заранее — гадать по ним незачем.
    /// <para>
    /// Четыре подписи взяты из разбивки цены над ответом (<c>S.Cost.*</c>) намеренно: одно и то же
    /// действие не должно называться в программе двумя разными словами.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> Own = new(StringComparer.OrdinalIgnoreCase)
    {
        [WebSearch] = "S.Spend.Kind.WebSearch",
        [Scrape] = "S.Spend.Kind.Scrape",
        [ChatSummary] = "S.Cost.Summary",
        [ChatSearch] = "S.Spend.Kind.ChatSearch",
        [ChatTitle] = "S.Cost.ChatTitle",
        [Router] = "S.Cost.Router",
        [Guard] = "S.Cost.Guard",
        [Translate] = "S.Spend.Kind.Translate",
        [Explain] = "S.Spend.Kind.Explain",
        [FollowUp] = "S.Spend.Kind.FollowUp",
        [Infographic] = "S.Spend.Kind.Infographic"
    };

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

        // Раньше разбора Venice: наша пометка — точная строка, и гадать по ней не нужно.
        if (Own.TryGetValue(raw, out var own))
        {
            return new SkuIdentity(null, own, raw);
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
