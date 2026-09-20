using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Провайдер, зашитый в идентификатор модели.
/// </summary>
/// <remarks>
/// Выбранная модель лежит строкой в девяти слотах настроек, в каждой переписке и в статьях
/// расхода журнала трат. Приставка — единственное, что отличает там модель OpenRouter от
/// модели Venice, поэтому ошибка разбора не видна глазом: запрос просто уходит не туда.
/// </remarks>
public sealed class ModelRefTests
{
    [Theory]
    [InlineData("grok-4-6", "grok-4-6")]
    [InlineData("openrouter:anthropic/claude-sonnet-4.5", "anthropic/claude-sonnet-4.5")]
    [InlineData("  openrouter:openai/gpt-5  ", "openai/gpt-5")]
    [InlineData("", "")]
    public void On_the_wire_the_prefix_is_gone(string stored, string expected) =>
        Assert.Equal(expected, ModelRef.Bare(stored));

    /// <summary>
    /// У OpenRouter двоеточие есть и внутри самого идентификатора: <c>:thinking</c>,
    /// <c>:free</c>, <c>:nitro</c> — это варианты модели. Разрез по последнему двоеточию отбросил
    /// бы вариант и молча запросил другую модель, дешевле и глупее выбранной.
    /// </summary>
    [Theory]
    [InlineData("openrouter:anthropic/claude-3.7-sonnet:thinking", "anthropic/claude-3.7-sonnet:thinking")]
    [InlineData("openrouter:meta-llama/llama-3.1-8b-instruct:free", "meta-llama/llama-3.1-8b-instruct:free")]
    public void A_model_variant_survives_the_split(string stored, string expected) =>
        Assert.Equal(expected, ModelRef.Bare(stored));

    /// <summary>
    /// Пакетный вариант приходится узнавать в лицо: обычный <c>/chat/completions</c> отвечает
    /// ему 404 «This model is only available through the Batch API», и без этой приметы такая
    /// модель стоит в списке кнопкой, которая всегда возвращает ошибку.
    /// </summary>
    [Theory]
    [InlineData("openrouter:anthropic/claude-sonnet-4.5:batch")]
    [InlineData("openrouter:openai/gpt-5:BATCH")]
    [InlineData("  openrouter:openai/gpt-5:batch  ")]
    public void A_batch_variant_is_recognised(string id) =>
        Assert.True(ModelRef.IsBatchOnly(id));

    /// <summary>
    /// Остальные варианты — обычные модели, и спутать их с пакетным нельзя: <c>:free</c>
    /// и <c>:thinking</c> отвечают тому же эндпоинту, что и все.
    /// </summary>
    [Theory]
    [InlineData("openrouter:anthropic/claude-3.7-sonnet:thinking")]
    [InlineData("openrouter:meta-llama/llama-3.1-8b-instruct:free")]
    [InlineData("openrouter:some/batch-model")]
    [InlineData("grok-4-6")]
    [InlineData("")]
    [InlineData(":batch")]
    public void Everything_else_is_an_ordinary_model(string id) =>
        Assert.False(ModelRef.IsBatchOnly(id));

    /// <summary>
    /// Заявке в очередь модель называют обычным слагом: пакетность задаёт эндпоинт, а не имя,
    /// и <c>:batch</c> в поле <c>model</c> заявку отвергли бы.
    /// </summary>
    [Theory]
    [InlineData("openrouter:openai/gpt-5:batch", "openrouter:openai/gpt-5")]
    [InlineData("openrouter:openai/gpt-5", "openrouter:openai/gpt-5")]
    [InlineData("grok-4-6", "grok-4-6")]
    public void The_queue_is_told_the_plain_slug(string stored, string expected) =>
        Assert.Equal(expected, ModelRef.WithoutBatchVariant(stored));

    [Theory]
    [InlineData("grok-4-6", LlmProvider.Venice)]
    [InlineData("openrouter:openai/gpt-5", LlmProvider.OpenRouter)]
    [InlineData("OPENROUTER:openai/gpt-5", LlmProvider.OpenRouter)]
    public void The_identifier_names_its_provider(string id, LlmProvider expected) =>
        Assert.Equal(expected, ModelRef.Of(id));

    /// <summary>
    /// «Авто» — не модель, а просьба выбрать её; пустая строка — «возьми из настроек». Ни то ни
    /// другое о провайдере не говорит ничего, и решает вызывающий.
    /// </summary>
    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData("   ")]
    public void Auto_and_blank_take_the_provider_they_are_given(string id)
    {
        Assert.Equal(LlmProvider.OpenRouter, ModelRef.Of(id, LlmProvider.OpenRouter));
        Assert.Equal(LlmProvider.Venice, ModelRef.Of(id));
    }

    [Fact]
    public void Qualifying_twice_changes_nothing()
    {
        var once = ModelRef.Qualify(LlmProvider.OpenRouter, "openai/gpt-5");
        var twice = ModelRef.Qualify(LlmProvider.OpenRouter, once);

        Assert.Equal("openrouter:openai/gpt-5", once);
        Assert.Equal(once, twice);
    }

    /// <summary>Модели Venice приставки не получают: их идентификаторы уже лежат у людей на диске.</summary>
    [Fact]
    public void Venice_identifiers_stay_bare() =>
        Assert.Equal("grok-4-6", ModelRef.Qualify(LlmProvider.Venice, "grok-4-6"));

    [Fact]
    public void Auto_is_never_qualified() =>
        Assert.Equal("auto", ModelRef.Qualify(LlmProvider.OpenRouter, "auto"));

}

public sealed class ProviderSpecTests
{
    [Fact]
    public void Every_provider_has_a_description()
    {
        foreach (var provider in Enum.GetValues<LlmProvider>())
        {
            var spec = ProviderSpec.For(provider);
            Assert.Equal(provider, spec.Provider);
            Assert.False(string.IsNullOrWhiteSpace(spec.Name));
            Assert.False(string.IsNullOrWhiteSpace(spec.BaseUrl));
            Assert.False(string.IsNullOrWhiteSpace(spec.EnvironmentVariable));
            Assert.Contains(spec, ProviderSpec.All);
        }
    }

    /// <summary>
    /// Приставка Venice пустая, и такой обязана остаться: любая другая переписала бы
    /// идентификаторы моделей в настройках и переписках всех, кто обновится.
    /// </summary>
    [Fact]
    public void Venice_has_no_prefix() =>
        Assert.Equal("", ProviderSpec.For(LlmProvider.Venice).Prefix);

    [Fact]
    public void Prefixes_are_unique()
    {
        var prefixes = ProviderSpec.All
            .Where(spec => spec.Prefix.Length > 0)
            .Select(spec => spec.Prefix)
            .ToList();

        Assert.Equal(prefixes.Count, prefixes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// Картинки и чтение страниц есть только у Venice, журнал списаний — тоже. На этих трёх
    /// признаках стоят и отказ инструмента, и переход графика трат на собственный журнал.
    /// </summary>
    [Fact]
    public void Only_venice_draws_reads_and_reports()
    {
        var venice = ProviderSpec.For(LlmProvider.Venice);
        Assert.True(venice.HasImages);
        Assert.True(venice.HasScrape);
        Assert.True(venice.HasUsageHistory);

        var openRouter = ProviderSpec.For(LlmProvider.OpenRouter);
        Assert.False(openRouter.HasImages);
        Assert.False(openRouter.HasScrape);
        Assert.False(openRouter.HasUsageHistory);
    }
}

/// <summary>Имена, логотипы и статьи расхода на идентификаторах с приставкой.</summary>
public sealed class ProviderModelNamingTests
{
    /// <summary>
    /// Без снятия приставки имя собиралось бы из неё: «Openrouter:anthropic/claude Sonnet 4.5».
    /// Это видно не только в выпадашке — то же имя уходит в подпись агента, в заголовок статьи
    /// расхода и в справку о моделях внутри системного промпта.
    /// </summary>
    [Theory]
    [InlineData("openrouter:anthropic/claude-sonnet-4.5", "Claude Sonnet 4.5")]
    [InlineData("openrouter:openai/gpt-5-mini", "Gpt 5 Mini")]
    [InlineData("openrouter:x-ai/grok-4", "Grok 4")]
    public void The_prefix_never_reaches_the_name(string id, string expected) =>
        Assert.Equal(expected, VeniceModelCatalog.GetDisplayName(id));

    [Fact]
    public void Venice_names_are_untouched() =>
        Assert.Equal("Grok 4.6", VeniceModelCatalog.GetDisplayName("grok-4-6"));

    /// <summary>
    /// Логотип ищется по кускам идентификатора. Приставка обязана сниматься до перебора: игла,
    /// попавшая бы в неё, совпала бы разом со всеми моделями провайдера, и вместо своих
    /// логотипов они получили бы один общий.
    /// </summary>
    [Theory]
    [InlineData("openrouter:anthropic/claude-sonnet-4.5", "Claude")]
    [InlineData("openrouter:x-ai/grok-4", "Grok")]
    [InlineData("openrouter:openai/gpt-5", "OpenAI")]
    [InlineData("openrouter:google/gemini-2.5-flash", "GoogleGemini")]
    [InlineData("openrouter:mistralai/mistral-small", "Mistral")]
    [InlineData("openrouter:deepseek/deepseek-chat", "DeepSeek")]
    public void The_logo_is_the_model_maker_not_the_provider(string id, string expected) =>
        Assert.Equal(expected, VeniceModelCatalog.GetLogoResourceKey(id));

    /// <summary>Сама приставка ни с одной иглой совпадать не должна.</summary>
    [Fact]
    public void The_prefix_alone_matches_nothing() =>
        Assert.Null(VeniceModelCatalog.GetLogoResourceKey("openrouter:zzz/qqq"));

    /// <summary>Буква значка берётся у модели, а не у приставки — иначе она у всех одна.</summary>
    [Fact]
    public void The_letter_badge_comes_from_the_model() =>
        Assert.Equal("A", VeniceModelCatalog.GetLogoLetter("openrouter:anthropic/claude-sonnet-4.5"));
}

/// <summary>
/// Цепочка замен при перегрузке модели.
/// </summary>
/// <remarks>
/// Платить можно одним ключом: замена моделью чужого провайдера ушла бы на сервер, к которому
/// этот ключ не пускают, — то есть перегрузку сменил бы отказ в доступе.
/// </remarks>
public sealed class ProviderFallbackChainTests
{
    [Fact]
    public void An_openrouter_model_never_falls_back_to_venice()
    {
        var chain = VeniceModelFallback.BuildChain("openrouter:anthropic/claude-sonnet-4.5");

        Assert.All(chain, id => Assert.Equal(LlmProvider.OpenRouter, ModelRef.Of(id)));
        Assert.Equal("openrouter:anthropic/claude-sonnet-4.5", chain[0]);
    }

    [Fact]
    public void A_venice_model_never_falls_back_to_openrouter()
    {
        var chain = VeniceModelFallback.BuildChain("grok-4-6");

        Assert.All(chain, id => Assert.Equal(LlmProvider.Venice, ModelRef.Of(id)));
    }

    /// <summary>«Авто» о провайдере не говорит ничего — его называет вызывающий.</summary>
    [Fact]
    public void Auto_takes_the_chain_of_the_active_provider()
    {
        var chain = VeniceModelFallback.BuildChain("auto", LlmProvider.OpenRouter);

        Assert.NotEmpty(chain);
        Assert.All(chain, id => Assert.Equal(LlmProvider.OpenRouter, ModelRef.Of(id)));
        Assert.DoesNotContain(chain, VeniceModelCatalog.IsAuto);
    }

    /// <summary>
    /// Модель не из списка запасных — заголовок чата, брифинг, выбор маршрутизатора — обязана
    /// остаться головой цепочки, а не уступить её первой запасной.
    /// </summary>
    [Fact]
    public void A_model_outside_the_chain_still_goes_first()
    {
        var chain = VeniceModelFallback
            .GetModelsFrom("openrouter:cohere/command-r", "openrouter:cohere/command-r")
            .ToList();

        Assert.Equal("openrouter:cohere/command-r", chain[0]);
        Assert.All(chain, id => Assert.Equal(LlmProvider.OpenRouter, ModelRef.Of(id)));
    }
}

/// <summary>
/// Разбор статей расхода на идентификаторах с приставкой.
/// </summary>
/// <remarks>
/// Догадки в <see cref="VeniceSku"/> рассчитаны на то, как называет статьи биллинговая лента
/// Venice. Наша собственная пометка — точный идентификатор модели, и догадываться по нему не
/// только незачем, но и вредно.
/// </remarks>
public sealed class ProviderSkuTests
{
    /// <summary>
    /// Отрезание единицы измерения свернуло бы «…gemini-2.5-flash-image» в «…gemini-2.5-flash» —
    /// другую настоящую модель каталога, и деньги за картинки легли бы на её строку.
    /// </summary>
    [Fact]
    public void A_unit_suffix_is_not_stripped_off_a_qualified_id()
    {
        var identity = VeniceSku.Resolve("openrouter:google/gemini-2.5-flash-image", known: null);

        Assert.Equal("openrouter:google/gemini-2.5-flash-image", identity.ModelId);
        Assert.Null(identity.KindKey);
    }

    /// <summary>Слово «search» внутри имени модели — не статья «поиск в интернете».</summary>
    [Fact]
    public void A_model_named_search_is_still_a_model()
    {
        var identity = VeniceSku.Resolve("openrouter:perplexity/sonar-deep-research", known: null);

        Assert.Null(identity.KindKey);
        Assert.Equal("openrouter:perplexity/sonar-deep-research", identity.ModelId);
    }

    /// <summary>Своя пометка старше любого разбора — у обоих провайдеров.</summary>
    [Fact]
    public void Own_labels_still_win()
    {
        Assert.Equal("S.Spend.Kind.WebSearch", VeniceSku.Resolve(VeniceSku.WebSearch, null).KindKey);
        Assert.Equal("S.Cost.Router", VeniceSku.Resolve(VeniceSku.Router, null).KindKey);
    }

    /// <summary>А лента Venice по-прежнему разбирается догадками — на ней они и написаны.</summary>
    [Fact]
    public void The_venice_billing_feed_is_still_guessed()
    {
        var identity = VeniceSku.Resolve("grok-4-6-token", known: null);

        Assert.Equal("grok-4-6", identity.ModelId);
    }
}
