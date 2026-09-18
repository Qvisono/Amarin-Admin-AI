using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Разбор <c>sku</c> из журнала трат: что из списаний — модель, а что служебная статья.
/// </summary>
/// <remarks>
/// Venice называет товар вроде <c>zai-org-glm-5-1-llm-output-mtoken</c> и набор этих имён
/// не публикует. Промах разбора не виден глазами: строка разбивки просто называется иначе,
/// а деньги в ней те же — поэтому проверяется и то, что ни один цент не теряется.
/// </remarks>
public sealed class VeniceSkuTests
{
    private static readonly List<VeniceModelInfo> Catalogue =
    [
        new() { Id = "grok-4-6" },
        new() { Id = "grok-4-6-fast" },
        new() { Id = "claude-sonnet-5" }
    ];

    /// <summary>
    /// Вход и выход обязаны дать один и тот же ключ группировки: иначе у человека с одной
    /// моделью вышло бы две строки, дающие в сумме итог.
    /// </summary>
    [Fact]
    public void Input_and_output_of_one_model_group_together()
    {
        var input = VeniceSku.Resolve("grok-4-6-llm-input-mtoken", Catalogue);
        var output = VeniceSku.Resolve("grok-4-6-llm-output-mtoken", Catalogue);

        Assert.Equal("grok-4-6", input.ModelId);
        Assert.Equal(VeniceSku.GroupKey(input), VeniceSku.GroupKey(output));
    }

    [Theory]
    [InlineData("grok-4-6-llm-reasoning-mtoken")]
    [InlineData("grok-4-6-llm-input-cached-mtoken")]
    [InlineData("grok-4-6-mtoken")]
    [InlineData("grok-4-6-request")]
    public void Every_unit_suffix_is_stripped(string sku) =>
        Assert.Equal("grok-4-6", VeniceSku.Resolve(sku, Catalogue).ModelId);

    /// <summary>
    /// Самый длинный подошедший идентификатор выигрывает. Иначе <c>grok-4-6</c> съел бы траты
    /// <c>grok-4-6-fast</c>, и быстрая модель выглядела бы бесплатной.
    /// </summary>
    [Fact]
    public void The_longest_matching_model_wins() =>
        Assert.Equal("grok-4-6-fast", VeniceSku.Resolve("grok-4-6-fast-llm-output-mtoken", Catalogue).ModelId);

    /// <summary>Venice предваряет id вендором, а каталог держит короткое имя.</summary>
    [Fact]
    public void A_vendor_prefix_does_not_hide_the_model() =>
        Assert.Equal("claude-sonnet-5", VeniceSku.Resolve("anthropic-claude-sonnet-5-llm-output-mtoken", Catalogue).ModelId);

    [Theory]
    [InlineData("web-search-request", "S.Spend.Kind.WebSearch")]
    [InlineData("augment-scrape-request", "S.Spend.Kind.Scrape")]
    [InlineData("nano-banana-pro-image", "S.Spend.Kind.Image")]
    [InlineData("kokoro-tts-second", "S.Spend.Kind.Audio")]
    [InlineData("text-embedding-3-token", "S.Spend.Kind.Embedding")]
    public void Service_charges_are_named_by_what_they_are(string sku, string key) =>
        Assert.Equal(key, VeniceSku.Resolve(sku, Catalogue).KindKey);

    /// <summary>
    /// Каталог держит только текстовые модели с инструментами, и незнакомое имя — обычное
    /// дело. Прятать такие деньги в «прочее» нельзя: человек решил бы, что от него что-то
    /// скрывают.
    /// </summary>
    [Fact]
    public void An_unknown_model_keeps_its_own_name()
    {
        var identity = VeniceSku.Resolve("some-new-model-9-llm-output-mtoken", Catalogue);

        Assert.Null(identity.KindKey);
        Assert.Equal("some-new-model-9", identity.ModelId);
        Assert.Equal("some-new-model-9-llm-output-mtoken", identity.Sku);
    }

    /// <summary>Каталог не загрузился — разбор всё равно обязан работать.</summary>
    [Fact]
    public void An_empty_catalogue_does_not_break_the_parse()
    {
        Assert.Equal("grok-4-6", VeniceSku.Resolve("grok-4-6-llm-output-mtoken", null).ModelId);
        Assert.Equal("S.Spend.Kind.WebSearch", VeniceSku.Resolve("web-search-request", []).KindKey);
    }

    [Fact]
    public void An_empty_sku_is_named_unknown() =>
        Assert.Equal("S.Spend.Kind.Unknown", VeniceSku.Resolve("  ", Catalogue).KindKey);

    [Fact]
    public void A_known_model_is_shown_by_its_pretty_name() =>
        Assert.Equal("Grok 4.6", VeniceSku.Title(VeniceSku.Resolve("grok-4-6-llm-output-mtoken", Catalogue)));
}
