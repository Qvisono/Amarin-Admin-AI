using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Чем заполнить слот, когда выбранной модели в каталоге больше нет.
/// </summary>
/// <remarks>
/// До версии 1.23.0 запасное значение бралось из умолчаний — то есть из идентификаторов Venice.
/// На ключе OpenRouter ни один из них каталогу не подходил, и все восемь служебных слотов
/// схлопывались бы в первую попавшуюся строку каталога: маршрутизатор, заголовок, защита и три
/// уровня агента оказались бы одной моделью, возможно дорогой.
/// </remarks>
public sealed class ModelSlotDefaultsTests
{
    private static VeniceModelInfo Model(string id) => new() { Id = id };

    private static IReadOnlyList<VeniceModelInfo> Catalog() =>
    [
        Model("openrouter:anthropic/claude-sonnet-4.5"),
        Model("openrouter:google/gemini-2.5-flash"),
        Model("openrouter:cohere/command-r")
    ];

    [Fact]
    public void The_slots_do_not_all_collapse_onto_one_model()
    {
        var picked = ModelSlots.All
            .Select(slot => ModelSlotDefaults.Resolve(LlmProvider.OpenRouter, slot.Slot, Catalog()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(picked.Count > 1, "все слоты получили одну и ту же модель: " + string.Join(", ", picked));
    }

    /// <summary>Дешёвому слоту — дешёвая модель, тяжёлому — сильная. Иначе заголовки чатов разорят.</summary>
    [Fact]
    public void A_cheap_slot_gets_a_cheap_model()
    {
        Assert.Equal(
            "openrouter:google/gemini-2.5-flash",
            ModelSlotDefaults.Resolve(LlmProvider.OpenRouter, ModelSlot.Title, Catalog()));
        Assert.Equal(
            "openrouter:anthropic/claude-sonnet-4.5",
            ModelSlotDefaults.Resolve(LlmProvider.OpenRouter, ModelSlot.Heavy, Catalog()));
    }

    /// <summary>
    /// Идентификаторы у OpenRouter — слаги, которые он изредка переименовывает. Когда списка
    /// предпочтений в каталоге нет вовсе, подбор идёт по приметам, а не отдаёт пустоту.
    /// </summary>
    [Fact]
    public void An_unknown_catalogue_is_picked_through_by_hints()
    {
        IReadOnlyList<VeniceModelInfo> odd =
        [
            Model("openrouter:some/big-brain"),
            Model("openrouter:some/tiny-flash")
        ];

        Assert.Equal(
            "openrouter:some/tiny-flash",
            ModelSlotDefaults.Resolve(LlmProvider.OpenRouter, ModelSlot.Router, odd));
        Assert.Equal(
            "openrouter:some/big-brain",
            ModelSlotDefaults.Resolve(LlmProvider.OpenRouter, ModelSlot.Heavy, odd));
    }

    /// <summary>
    /// Каталог не доехал — судить не о чем, но и пустую строку возвращать нельзя: у этого
    /// провайдера её не из чего вывести, и запрос ушёл бы без модели.
    /// </summary>
    [Fact]
    public void Without_a_catalogue_there_is_still_a_model()
    {
        var picked = ModelSlotDefaults.Resolve(LlmProvider.OpenRouter, ModelSlot.Chat, catalog: null);

        Assert.NotEqual("", picked);
        Assert.Equal(LlmProvider.OpenRouter, ModelRef.Of(picked));
    }

    /// <summary>У Venice ничего не изменилось: умолчания те же, что и были в настройках.</summary>
    [Fact]
    public void Venice_keeps_its_shipped_defaults()
    {
        var defaults = new AppSettings();

        Assert.Equal(
            defaults.TitleModelId,
            ModelSlotDefaults.Resolve(LlmProvider.Venice, ModelSlot.Title, catalog: null));
        Assert.Equal(
            defaults.HeavyModelId,
            ModelSlotDefaults.Resolve(LlmProvider.Venice, ModelSlot.Heavy, catalog: null));
    }

    /// <summary>
    /// Последняя соломинка там, где в коде зашит идентификатор Venice. У Venice она его и
    /// возвращает, у остальных — подбирает свой: иначе на ключе OpenRouter запрос ушёл бы
    /// к модели Venice, то есть в четырёхсотку «модель не найдена».
    /// </summary>
    [Fact]
    public void A_hardcoded_venice_id_is_replaced_for_another_provider()
    {
        Assert.Equal(
            "openai-gpt-56-luna",
            ModelSlotDefaults.LastResort(LlmProvider.Venice, ModelSlot.Lite, "openai-gpt-56-luna"));

        var replaced = ModelSlotDefaults.LastResort(
            LlmProvider.OpenRouter, ModelSlot.Lite, "openai-gpt-56-luna");

        Assert.Equal(LlmProvider.OpenRouter, ModelRef.Of(replaced));
    }
}

/// <summary>Каталог знает, чей он, — по этому признаку выпадашка выбирает набор «Рекомендуемых».</summary>
public sealed class ProviderCatalogTests
{
    [Fact]
    public void The_catalogue_names_its_provider()
    {
        Assert.Equal(
            LlmProvider.OpenRouter,
            VeniceModelCatalog.ProviderOf([new VeniceModelInfo { Id = "openrouter:openai/gpt-5" }]));
        Assert.Equal(
            LlmProvider.Venice,
            VeniceModelCatalog.ProviderOf([new VeniceModelInfo { Id = "grok-4-6" }]));
    }

    /// <summary>Пустой каталог — судить не о чем, и остаётся Venice: он был единственным.</summary>
    [Fact]
    public void An_empty_catalogue_stays_venice() =>
        Assert.Equal(LlmProvider.Venice, VeniceModelCatalog.ProviderOf([]));

}
