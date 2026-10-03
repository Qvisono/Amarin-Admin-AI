namespace Amarin.Core;

/// <summary>
/// Выбор модели вместе с ключом, которым за неё платят.
/// </summary>
/// <remarks>
/// До версии 1.23.0 выбором была одна строка — идентификатор модели, — а ключ был один на всю
/// программу, и он же задавал провайдера. Из-за этого девять слотов обесценивались разом при
/// смене ключа. Теперь провайдера задаёт приставка идентификатора (<see cref="ModelRef"/>),
/// а ключ выбирается отдельно на каждый слот: заголовки чатов могут уходить бесплатной модели
/// одного провайдера, пока разговор идёт у другого.
/// <para>
/// <see cref="KeyId"/> пуст — «ключ по умолчанию для провайдера этой модели»
/// (<see cref="ApiKeyProvider.DefaultFor"/>). Именно поэтому старые <c>settings.json</c> и
/// переписки открываются без миграции: ключ в них не записан, и подбирается он сам.
/// </para>
/// </remarks>
public readonly record struct ModelBinding(string ModelId, string? KeyId)
{
    /// <summary>Ничего не выбрано: и модель, и ключ берутся по умолчанию.</summary>
    public static ModelBinding Empty => new("", null);

    /// <summary>Модель без выбранного ключа — ключ подберётся по провайдеру.</summary>
    public static ModelBinding Of(string? modelId) => new(modelId ?? "", null);

    /// <summary>Чьему серверу уйдёт запрос этой привязки.</summary>
    public LlmProvider Provider => ModelRef.Of(ModelId);
}

/// <summary>
/// Что человек выбрал для поиска в интернете. Пустой провайдер — как у модели хода.
/// </summary>
public readonly record struct WebSearchTarget(
    LlmProvider? Provider,
    string? KeyId,
    string? Engine,
    string? Mode);

/// <summary>
/// Готовый к отправке поиск: чем искать, чьим ключом платить и каким движком.
/// </summary>
/// <remarks>
/// Модель здесь уже подобрана (<see cref="ModelSlots.WebSearch"/>) — человек её не выбирает.
/// Пустая привязка означает «как у модели хода»; движок при этом остаётся в силе, если ход
/// всё равно окажется у OpenRouter.
/// </remarks>
public readonly record struct WebSearchPlan(ModelBinding Binding, string? Engine, string? Mode)
{
    /// <summary>Провайдер не закреплён — искать там же, где идёт разговор.</summary>
    public bool FollowsTurn =>
        string.IsNullOrWhiteSpace(Binding.ModelId) || VeniceModelCatalog.IsAuto(Binding.ModelId);
}
