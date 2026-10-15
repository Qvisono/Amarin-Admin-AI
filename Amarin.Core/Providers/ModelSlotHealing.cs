namespace Amarin.Core;

/// <summary>
/// Лечение выбора моделей по доехавшему каталогу провайдера.
/// </summary>
/// <remarks>
/// Убрать модель из списка мало: выбранная лежит в settings.json, и запросы продолжали уходить к
/// ней. Именно так вела себя inkling — она отвечала четырёхсоткой на каждый ход, а сменить её было
/// негде, потому что из списка она уже исчезла. Чинится при загрузке каталога, а не в каждом
/// ходе: подмена на лету была бы незаметной, а здесь человек видит в поле новую модель.
/// <para>
/// Отдельно от окна, чтобы правила проверялись без него: какой слот трогать, а какой нет, —
/// ровно то место, где ошибка молча уводит заголовки чатов или агента к другому провайдеру.
/// </para>
/// </remarks>
internal static class ModelSlotHealing
{
    /// <summary>Лечит слоты по каталогу провайдера. <c>true</c> — что-то поменялось.</summary>
    /// <param name="currentProvider">Провайдер выбранного ключа: ему принадлежат пустые слоты и «Авто».</param>
    /// <param name="hasKey">Есть ли у провайдера хоть один ключ.</param>
    /// <param name="keyExists">Жив ли ключ с этим идентификатором.</param>
    public static bool Heal(
        AppSettings settings,
        LlmProvider provider,
        IReadOnlyList<VeniceModelInfo> models,
        LlmProvider currentProvider,
        Func<LlmProvider, bool> hasKey,
        Func<string, bool> keyExists)
    {
        if (models.Count == 0)
        {
            return false;
        }

        var changed = false;
        foreach (var (slot, read, write) in ModelSlots.All)
        {
            var available = VeniceModelCatalog.ForSlot(models, slot);
            var current = read(settings) ?? "";
            var owner = Owner(current, currentProvider);

            if (owner != provider)
            {
                changed |= MoveFromKeyless(settings, slot, write, current, owner, provider, available, currentProvider, hasKey);
                continue;
            }

            var wrongType = slot is not (ModelSlot.Router or ModelSlot.SynGuard) &&
                            VeniceModelCatalog.IsDecisionModel(ReasoningPolicy.Find(models, current));
            if (!wrongType && VeniceModelCatalog.IsSelectable(available, current))
            {
                // Модель на месте, а вот назначенный ключ мог исчезнуть вместе с удалённой
                // строкой: возвращаем слот к ключу по умолчанию, а не к отказу в запросе.
                if (ModelSlots.ReadKey(settings, slot) is { } keyId && !keyExists(keyId))
                {
                    ModelSlots.WriteKey(settings, slot, null);
                    changed = true;
                }

                continue;
            }

            // У Venice пустая строка в слоте чата означает «взять модель из appsettings.json», и
            // обнулить слот достаточно. У остальных провайдеров такой модели нет — им слот
            // заполняется явно, иначе каждый ход уходил бы к чужому идентификатору.
            if (slot == ModelSlot.Chat && provider == LlmProvider.Venice)
            {
                settings.ChatModelId = "";
                ModelSlots.WriteKey(settings, slot, null);
                changed = true;
                continue;
            }

            var replacement = ModelSlotDefaults.Resolve(provider, slot, available);
            if (replacement.Length == 0)
            {
                continue;
            }

            write(settings, replacement);
            ModelSlots.WriteKey(settings, slot, null);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Чей слот. Пустой и «Авто» — ничьи: их лечит провайдер выбранного ключа.
    /// </summary>
    private static LlmProvider Owner(string modelId, LlmProvider currentProvider) =>
        string.IsNullOrWhiteSpace(modelId) || VeniceModelCatalog.IsAuto(modelId)
            ? currentProvider
            : ModelRef.Of(modelId);

    /// <summary>
    /// Слот, стоящий у провайдера без единого ключа, переезжает к провайдеру выбранного ключа —
    /// той же моделью, если она там есть.
    /// </summary>
    /// <remarks>
    /// Заводские модели записаны идентификаторами Venice, и человек только с ключом OpenRouter
    /// или Anthropic иначе получал бы «добавьте ключ Venice» на каждом слоте. Переезжает только
    /// слот провайдера, у которого ключей нет вовсе, — не тот, чей каталог не загрузился:
    /// отказ по стране стёр бы выбор, который сработает, как только сеть пустит. И только к
    /// провайдеру выбранного ключа: иначе слоты расползлись бы по тем, чей каталог доехал первым.
    /// </remarks>
    private static bool MoveFromKeyless(
        AppSettings settings,
        ModelSlot slot,
        Action<AppSettings, string> write,
        string current,
        LlmProvider owner,
        LlmProvider provider,
        IReadOnlyList<VeniceModelInfo> models,
        LlmProvider currentProvider,
        Func<LlmProvider, bool> hasKey)
    {
        if (provider != currentProvider || hasKey(owner))
        {
            return false;
        }

        var moved = ModelFamily.Find(ModelFamily.KeyOf(current), provider, models)
                    ?? ModelSlotDefaults.Resolve(provider, slot, models);
        if (moved.Length == 0)
        {
            return false;
        }

        write(settings, moved);
        ModelSlots.WriteKey(settings, slot, null);
        return true;
    }
}
