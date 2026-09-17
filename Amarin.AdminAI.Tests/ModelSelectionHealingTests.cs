using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Выбор модели, пропавшей из каталога.
/// </summary>
/// <remarks>
/// Убрать сломанную модель из списка оказалось мало: выбранная лежит в settings.json и в самом
/// чате, и запросы продолжали уходить к ней. Inkling так и вела себя — четырёхсотка на каждый
/// ход, а сменить модель негде, потому что из списка она уже исчезла.
/// </remarks>
public sealed class ModelSelectionHealingTests
{
    private static VeniceModelInfo Model(string id) => new()
    {
        Id = id,
        ModelSpec = new VeniceModelSpec
        {
            Offline = false,
            Capabilities = new VeniceModelCapabilities
            {
                SupportsFunctionCalling = true,
                SupportsReasoning = true
            }
        }
    };

    private static readonly VeniceModelInfo[] Catalog = [Model("grok-4-6"), Model("claude-sonnet-5")];

    [Fact]
    public void A_model_that_left_the_catalog_is_no_longer_selectable()
    {
        Assert.False(VeniceModelCatalog.IsSelectable(Catalog, "inkling"));
        Assert.True(VeniceModelCatalog.IsSelectable(Catalog, "grok-4-6"));
    }

    [Fact]
    public void The_id_is_matched_regardless_of_case()
    {
        Assert.True(VeniceModelCatalog.IsSelectable(Catalog, "GROK-4-6"));
        Assert.True(VeniceModelCatalog.IsSelectable(Catalog, "  grok-4-6  "));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("Auto")]
    [InlineData("")]
    [InlineData(null)]
    public void Auto_and_the_empty_choice_survive(string? id)
    {
        // «Авто» — не модель, а просьба её выбрать, в каталоге её нет и быть не должно.
        // Пустая строка означает «бери модель из настроек» и тоже законна.
        Assert.True(VeniceModelCatalog.IsSelectable(Catalog, id));
    }

    [Fact]
    public void An_empty_catalog_judges_nothing()
    {
        // Список не доехал — сеть, ключ, что угодно. Если счесть это «модели больше нет»,
        // разовый сбой сети стёр бы человеку все семь выбранных моделей разом.
        Assert.True(VeniceModelCatalog.IsSelectable([], "inkling"));
        Assert.True(VeniceModelCatalog.IsSelectable(null, "inkling"));
    }
}
