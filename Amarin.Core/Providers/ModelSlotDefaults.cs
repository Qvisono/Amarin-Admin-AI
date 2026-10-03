namespace Amarin.Core;

/// <summary>
/// Чем заполнить слот модели, когда выбранного в каталоге больше нет.
/// </summary>
/// <remarks>
/// До версии 1.23.0 запасное значение бралось из <c>new AppSettings()</c> — то есть из
/// идентификаторов Venice. На ключе OpenRouter ни один из них каталогу не подходит, и все
/// восемь служебных слотов схлопывались бы в первую попавшуюся строку каталога: маршрутизатор,
/// заголовок, защита и три уровня агента оказались бы одной моделью, возможно дорогой.
/// <para>
/// Поэтому список предпочтений свой у каждого провайдера и у каждого слота, а сверяется он
/// с живым каталогом: идентификаторы у OpenRouter — слаги, которые он изредка меняет, и
/// зашитая строка когда-нибудь перестанет существовать. Если не подошло ничего, слот
/// заполняется по приметам — дешёвый ищет дешёвую модель, тяжёлый берёт первую, — лишь бы не
/// назначить одну и ту же модель на всё сразу.
/// </remarks>
public static class ModelSlotDefaults
{
    /// <summary>Слоты, которым нужна быстрая и дешёвая модель, а не самая умная.</summary>
    private static readonly ModelSlot[] CheapSlots =
    [
        ModelSlot.Lite, ModelSlot.Router, ModelSlot.Title, ModelSlot.AgentFast,
        ModelSlot.AgentLite, ModelSlot.SynGuard, ModelSlot.Summary
    ];

    /// <summary>Приметы дешёвой модели в идентификаторе — на случай, когда список не подошёл.</summary>
    private static readonly string[] CheapNeedles =
    [
        "flash", "mini", "haiku", "small", "lite", "nano", "turbo", "fast"
    ];

    private static readonly string[] OpenRouterStrong =
    [
        "openrouter:anthropic/claude-sonnet-4.5",
        "openrouter:openai/gpt-5",
        "openrouter:google/gemini-2.5-pro",
        "openrouter:x-ai/grok-4"
    ];

    private static readonly string[] OpenRouterCheap =
    [
        "openrouter:google/gemini-2.5-flash",
        "openrouter:openai/gpt-5-mini",
        "openrouter:anthropic/claude-haiku-4.5",
        "openrouter:openai/gpt-4.1-mini",
        "openrouter:mistralai/mistral-small-3.2-24b-instruct"
    ];

    /// <summary>
    /// Чем заполнить слот. Пустая строка означает «оставить как есть» и возвращается только
    /// для модели чата у Venice: там пустая строка — не отсутствие модели, а «взять ту, что
    /// указана в appsettings.json».
    /// </summary>
    /// <param name="catalog">
    /// Загруженный каталог. Пустой — подбирать не из чего, и слот остаётся нетронутым.
    /// </param>
    public static string Resolve(
        LlmProvider provider,
        ModelSlot slot,
        IReadOnlyList<VeniceModelInfo>? catalog)
    {
        if (provider == LlmProvider.Venice)
        {
            // Умолчания Venice — те же, что и были: они лежат в самих настройках.
            var shipped = Shipped(slot);
            return VeniceModelCatalog.IsSelectable(catalog, shipped) ? shipped : Pick(slot, catalog);
        }

        var preferred = IsCheap(slot) ? OpenRouterCheap : OpenRouterStrong;

        // Каталога нет — судить не о чем, и первое предпочтение лучше пустоты: пустая строка
        // означала бы «модель не выбрана», а у этого провайдера её не из чего вывести.
        if (catalog is null || catalog.Count == 0)
        {
            return preferred[0];
        }

        foreach (var candidate in preferred)
        {
            if (Contains(catalog, candidate))
            {
                return candidate;
            }
        }

        return Pick(slot, catalog);
    }

    /// <summary>
    /// Последняя соломинка там, где в коде зашит идентификатор Venice.
    /// </summary>
    /// <remarks>
    /// Такие места остались с тех пор, как провайдер был один: если слот в настройках пуст,
    /// брать модель неоткуда. Обычно слот не пуст — его заполняет лечение выбора моделей, —
    /// но лечение молчит, пока не доехал каталог, и в эту щель на ключе OpenRouter уходил бы
    /// запрос к модели Venice, то есть четырёхсотка «модель не найдена».
    /// </remarks>
    public static string LastResort(LlmProvider provider, ModelSlot slot, string veniceId) =>
        provider == LlmProvider.Venice ? veniceId : Resolve(provider, slot, catalog: null);

    /// <summary>Значение, с которым программа приезжает к человеку впервые.</summary>
    private static string Shipped(ModelSlot slot)
    {
        var defaults = new AppSettings();
        foreach (var (candidate, read, _) in ModelSlots.All)
        {
            if (candidate == slot)
            {
                return read(defaults) ?? "";
            }
        }

        return "";
    }

    /// <summary>
    /// Подбор по приметам, когда список предпочтений не пригодился: дешёвому слоту — модель
    /// с «flash» или «mini» в имени, остальным — первая в каталоге.
    /// </summary>
    private static string Pick(ModelSlot slot, IReadOnlyList<VeniceModelInfo>? catalog)
    {
        if (catalog is null || catalog.Count == 0)
        {
            return "";
        }

        if (IsCheap(slot))
        {
            foreach (var needle in CheapNeedles)
            {
                foreach (var model in catalog)
                {
                    if (model.Id.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    {
                        return model.Id;
                    }
                }
            }
        }

        return catalog[0].Id;
    }

    private static bool IsCheap(ModelSlot slot) => CheapSlots.Contains(slot);

    private static bool Contains(IReadOnlyList<VeniceModelInfo>? catalog, string id)
    {
        if (catalog is null)
        {
            return false;
        }

        foreach (var model in catalog)
        {
            if (id.Equals(model.Id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
