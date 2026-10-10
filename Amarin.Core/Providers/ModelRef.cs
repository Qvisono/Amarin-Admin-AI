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
    /// <summary>Приставка пакетного варианта — то, чем OpenRouter помечает такие модели.</summary>
    private const string BatchVariant = ":batch";

    /// <summary>
    /// Чья это модель. Пустая строка и «авто» — не модели, о провайдере они не говорят
    /// ничего, поэтому для них возвращается <paramref name="fallback"/>.
    /// </summary>
    /// <param name="fallback">
    /// Чем считать «авто» и пустую строку. Обычно — провайдер активного ключа.
    /// </param>
    public static LlmProvider Of(string? modelId, LlmProvider fallback = default)
    {
        var id = (modelId ?? "").Trim();
        if (id.Length == 0 || VeniceModelCatalog.IsAuto(id))
        {
            return fallback;
        }

        if (CustomPrefixLength(id) is > 0 and var length)
        {
            return LlmProvider.Parse(id[..(length - 1)]);
        }

        return FindPrefix(id)?.Provider ?? LlmProvider.Venice;
    }

    /// <summary>Идентификатор без приставки — ровно то, что уходит на провод.</summary>
    public static string Bare(string? modelId)
    {
        var id = (modelId ?? "").Trim();
        if (CustomPrefixLength(id) is > 0 and var length)
        {
            return id[length..];
        }

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

        var prefix = provider.IsCustom ? provider.Id + ":" : ProviderSpec.For(provider).Prefix;
        return prefix.Length == 0 ? Bare(id) : prefix + Bare(id);
    }

    /// <summary>
    /// Длина приставки своего сервера (<c>custom-3f2a9c:</c>) вместе с двоеточием. Ноль — это
    /// не свой сервер.
    /// </summary>
    /// <remarks>
    /// По форме, без справочника серверов: идентификатор модели разбирается и там, где ключей
    /// нет вовсе (разбивка трат, логотипы), а сервер, чей ключ уже удалён, всё равно должен
    /// узнаваться своим, а не превращаться в Venice. Сам идентификатор сервера двоеточий не
    /// содержит — его выдаёт программа.
    /// </remarks>
    private static int CustomPrefixLength(string id)
    {
        if (!id.StartsWith("custom-", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var colon = id.IndexOf(':', StringComparison.Ordinal);
        return colon > "custom-".Length ? colon + 1 : 0;
    }

    /// <summary>
    /// Пакетный вариант модели у OpenRouter: <c>…:batch</c>.
    /// </summary>
    /// <remarks>
    /// Половинная цена достаётся ценой асинхронности: такой идентификатор принимает только
    /// <c>/batches</c>, куда задание кладут файлом и ждут результата часами, а обычный
    /// <c>/chat/completions</c> отвечает на него 404 «This model is only available through the
    /// Batch API». Для разговора, где один ход упирается в десяток последовательных запросов
    /// с вызовами инструментов между ними, это не другой эндпоинт, а другой способ работы, —
    /// поэтому <see cref="VeniceClient"/> ведёт такой запрос не через <c>/chat/completions</c>,
    /// а через очередь: кладёт заявку, опрашивает её до готовности и отдаёт ответ обычным
    /// <see cref="ChatCompletionResponse"/>. Близнец без пометки лежит в том же списке — это
    /// та же модель по обычной цене и с обычным ожиданием.
    /// <para>
    /// Разрез идёт с конца, а не через <see cref="Bare"/>: вариант всегда последний,
    /// и по строке достаточно пройти один раз, без выделения памяти. Другие варианты
    /// (<c>:free</c>, <c>:thinking</c>, <c>:nitro</c>) — обычные модели, их трогать нельзя.
    /// </para>
    /// </remarks>
    public static bool IsBatchOnly(string? modelId)
    {
        var id = (modelId ?? "").AsSpan().Trim();
        return id.Length > BatchVariant.Length &&
               id.EndsWith(BatchVariant, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Идентификатор без пометки пакетного варианта.
    /// </summary>
    /// <remarks>
    /// Нужен заявке в очередь: пакетность там задаёт сам эндпоинт, а <c>model</c> ожидается
    /// обычным слагом. Приставку провайдера этот метод не трогает — её снимает
    /// <see cref="Bare"/>, и порядок вызовов значения не имеет.
    /// </remarks>
    public static string WithoutBatchVariant(string? modelId)
    {
        var id = (modelId ?? "").Trim();
        return IsBatchOnly(id) ? id[..^BatchVariant.Length] : id;
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
