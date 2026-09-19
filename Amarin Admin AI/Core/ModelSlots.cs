namespace Amarin.Core;

/// <summary>Куда в настройках записана выбранная модель.</summary>
/// <remarks>
/// Имена членов попадают в <c>settings.json</c> ключами словаря
/// <see cref="AppSettings.ModelKeys"/>: дописывать можно, переименовывать нельзя.
/// </remarks>
public enum ModelSlot
{
    Chat,
    Lite,
    Heavy,
    Router,
    Title,
    AgentFast,
    AgentLite,
    AgentHeavy,
    SynGuard,

    /// <summary>Скрытая сводка переписки. До 1.23.0 делил слот с быстрым агентом.</summary>
    Summary
}

/// <summary>
/// Все слоты моделей одним списком: как прочитать и как записать модель и ключ.
/// </summary>
/// <remarks>
/// Один список на всю программу, а не свой у каждого, кому понадобились слоты. Прежде такой
/// список жил в лечении выбора моделей, и второй, для обмена при смене провайдера, разошёлся
/// бы с ним при добавлении десятого слота — а разошёлся бы молча.
/// </remarks>
public static class ModelSlots
{
    public static readonly (ModelSlot Slot, Func<AppSettings, string> Read, Action<AppSettings, string> Write)[] All =
    [
        (ModelSlot.Chat, s => s.ChatModelId, (s, v) => s.ChatModelId = v),
        (ModelSlot.Lite, s => s.LiteModelId, (s, v) => s.LiteModelId = v),
        (ModelSlot.Heavy, s => s.HeavyModelId, (s, v) => s.HeavyModelId = v),
        (ModelSlot.Router, s => s.RouterModelId, (s, v) => s.RouterModelId = v),
        (ModelSlot.Title, s => s.TitleModelId, (s, v) => s.TitleModelId = v),
        (ModelSlot.AgentFast, s => s.AgentFastModelId, (s, v) => s.AgentFastModelId = v),
        (ModelSlot.AgentLite, s => s.AgentLiteModelId, (s, v) => s.AgentLiteModelId = v),
        (ModelSlot.AgentHeavy, s => s.AgentHeavyModelId, (s, v) => s.AgentHeavyModelId = v),
        (ModelSlot.SynGuard, s => s.SynGuardModelId, (s, v) => s.SynGuardModelId = v),
        (ModelSlot.Summary, s => s.SummaryModelId, (s, v) => s.SummaryModelId = v)
    ];

    /// <summary>
    /// Служебные слоты — все, кроме модели чата.
    /// </summary>
    /// <remarks>
    /// Чат стоит особняком: у него есть и переопределение на переписку, и пустая строка как
    /// «взять из appsettings», поэтому лечит его вызывающий отдельно.
    /// </remarks>
    public static IEnumerable<(ModelSlot Slot, Func<AppSettings, string> Read, Action<AppSettings, string> Write)>
        Service => All.Where(static slot => slot.Slot != ModelSlot.Chat);

    /// <summary>Ключ, назначенный слоту. <c>null</c> — ключ по умолчанию для провайдера модели.</summary>
    public static string? ReadKey(AppSettings settings, ModelSlot slot) =>
        settings.ModelKeys is { } keys && keys.TryGetValue(slot.ToString(), out var id) &&
        !string.IsNullOrWhiteSpace(id)
            ? id
            : null;

    /// <summary>
    /// Назначает слоту ключ. Пустое значение убирает запись, а не пишет пустую строку.
    /// </summary>
    /// <remarks>
    /// «Ключа нет» и «ключ по умолчанию» — одно и то же состояние, и хранить его двумя способами
    /// значит однажды сравнить их между собой и ошибиться. Пустой словарь тоже убирается: иначе
    /// в <c>settings.json</c> у всех, кто ни разу не выбирал ключ, завёлся бы пустой объект.
    /// </remarks>
    public static void WriteKey(AppSettings settings, ModelSlot slot, string? keyId)
    {
        var name = slot.ToString();

        if (string.IsNullOrWhiteSpace(keyId))
        {
            if (settings.ModelKeys?.Remove(name) == true && settings.ModelKeys.Count == 0)
            {
                settings.ModelKeys = null;
            }

            return;
        }

        var keys = settings.ModelKeys ??= new Dictionary<string, string>(StringComparer.Ordinal);
        keys[name] = keyId;
    }

    /// <summary>Модель слота вместе с её ключом.</summary>
    public static ModelBinding Binding(AppSettings settings, ModelSlot slot)
    {
        var read = All.First(entry => entry.Slot == slot).Read;
        return new ModelBinding(read(settings) ?? "", ReadKey(settings, slot));
    }

    /// <summary>
    /// Чем вести поиск в интернете. Пустая привязка — искать там же, где идёт разговор.
    /// </summary>
    /// <remarks>
    /// Человек выбирает провайдера и ключ, а модель подбирается здесь: отдельной «ручки поиска»
    /// у провайдеров нет, поиск едет надстройкой на обычном запросе, и какая модель его везёт,
    /// человека не касается. Берём слот «лёгкие задачи», если он того же провайдера: эту модель
    /// человек выбрал сам, и она уже сверена с живым каталогом. Иначе — подбор по умолчанию.
    /// </remarks>
    public static WebSearchPlan WebSearch(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Движок остаётся в силе и без закреплённого провайдера: если ход и так идёт
        // у OpenRouter, искать дорогим движком вместо выбранного человеком незачем.
        var engine = settings.WebSearchEngine;
        var mode = settings.WebSearchEngineMode;

        if (settings.WebSearchProvider is not { } provider)
        {
            return new WebSearchPlan(ModelBinding.Empty, engine, mode);
        }

        var lite = settings.LiteModelId;
        var model = !string.IsNullOrWhiteSpace(lite) &&
                    !VeniceModelCatalog.IsAuto(lite) &&
                    ModelRef.Of(lite) == provider
            ? lite
            : ModelSlotDefaults.Resolve(provider, ModelSlot.Lite, catalog: null);

        return new WebSearchPlan(new ModelBinding(model, settings.WebSearchKeyId), engine, mode);
    }

    /// <summary>Записывает и модель, и ключ разом: порознь их забывают записать по одному.</summary>
    public static void WriteBinding(AppSettings settings, ModelSlot slot, ModelBinding binding)
    {
        All.First(entry => entry.Slot == slot).Write(settings, binding.ModelId ?? "");
        WriteKey(settings, slot, binding.KeyId);
    }
}
