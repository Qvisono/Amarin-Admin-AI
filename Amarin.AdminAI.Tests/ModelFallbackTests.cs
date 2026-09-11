using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Цепочка запасных моделей. Её не покрывал ни один тест, и в ней жила поломка «Авто»:
/// запрос уходил на модель с именем <c>auto</c>, а Venice отвечает на неё 404.
/// </summary>
public sealed class ModelFallbackTests
{
    private const string Lite = "openai-gpt-56-luna";

    [Fact]
    public void Auto_never_enters_the_chain()
    {
        // «auto» — не модель, а просьба выбрать её за пользователя. Корнем цепочки она
        // оказывалась потому, что ход помнит запрошенную модель как есть.
        Assert.DoesNotContain(VeniceModelCatalog.AutoId, VeniceModelFallback.BuildChain("auto"));
        Assert.DoesNotContain(VeniceModelCatalog.AutoId, VeniceModelFallback.GetModelsFrom(Lite, "auto"));
        Assert.DoesNotContain(VeniceModelCatalog.AutoId, VeniceModelFallback.GetModelsFrom("auto", "grok-4-6"));
    }

    [Fact]
    public void A_model_outside_the_chain_is_tried_first()
    {
        // Ровно тот случай, что ломал «Авто»: маршрутизатор выбрал дешёвую модель, а её нет
        // среди запасных. Перебор начинался с головы цепочки — то есть с «auto», — и первый же
        // запрос возвращал 404 «Specified model not found: auto».
        var chain = VeniceModelFallback.GetModelsFrom(Lite, "auto").ToList();

        Assert.Equal(Lite, chain[0]);
        Assert.Contains("grok-4-6", chain);
    }

    [Fact]
    public void A_model_outside_the_chain_does_not_hijack_the_request()
    {
        // То же самое тише: запрос заголовка чата или брифа уходил не на ту модель, которую
        // просили, а на основную модель приложения, и никто этого не видел.
        var chain = VeniceModelFallback.GetModelsFrom("some-other-model", "grok-4-6").ToList();

        Assert.Equal("some-other-model", chain[0]);
        Assert.Contains("grok-4-6", chain);
    }

    [Fact]
    public void The_chain_resumes_after_the_model_that_already_failed()
    {
        // Смысл перебора: продолжить с того места, где остановились, а не начать сначала —
        // иначе после отказа первый же повтор уходит на уже упавшую модель.
        var full = VeniceModelFallback.BuildChain("claude-sonnet-5").ToList();
        var resumed = VeniceModelFallback.GetModelsFrom("grok-4-6", "claude-sonnet-5").ToList();

        Assert.Equal("grok-4-6", resumed[0]);
        Assert.DoesNotContain("claude-sonnet-5", resumed);
        Assert.Equal(full.Skip(full.IndexOf("grok-4-6")), resumed);
    }

    [Fact]
    public void The_requested_model_stays_at_the_head_of_the_chain()
    {
        var chain = VeniceModelFallback.BuildChain("grok-4-6").ToList();

        Assert.Equal("grok-4-6", chain[0]);
        Assert.Equal(chain.Count, chain.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
