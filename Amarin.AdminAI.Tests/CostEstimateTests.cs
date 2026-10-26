using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Цены моделей из обоих каталогов: по ним оценивается ответ провайдера без своей цены.</summary>
public sealed class CostEstimateTests
{
    [Fact]
    public void Venice_prices_are_read_from_model_spec_in_dollars_per_million()
    {
        // Форма — по спецификации Venice (model_spec.pricing.input/output.usd).
        const string json = """
            {"data":[
              {"id":"text-model","type":"text","model_spec":{"pricing":{"input":{"usd":0.7,"diem":7},"output":{"usd":2.8,"diem":28}}}},
              {"id":"string-price","type":"text","model_spec":{"pricing":{"input":{"usd":"0.5"},"output":{"usd":"1.5"}}}},
              {"id":"image-model","type":"image","model_spec":{"pricing":{"generation":{"usd":0.01}}}},
              {"id":"odd-price","type":"text","model_spec":{"pricing":{"input":{"usd":{"weird":1}},"output":{"usd":[1]}}}}
            ]}
            """;

        var models = JsonSerializer.Deserialize(json, VeniceJsonContext.Default.VeniceModelsListResponse)!.Data;

        Assert.Equal(new ModelPrice(0.7m, 2.8m), CostEstimator.PriceOf(models[0]));
        Assert.Equal(new ModelPrice(0.5m, 1.5m), CostEstimator.PriceOf(models[1]));
        Assert.Null(CostEstimator.PriceOf(models[2]));
        Assert.Null(CostEstimator.PriceOf(models[3]));
    }

    [Fact]
    public void OpenRouter_prices_per_token_become_prices_per_million_and_router_placeholders_are_dropped()
    {
        // Форма — по официальному SDK OpenRouter: pricing.prompt/completion строками за токен.
        const string json = """
            {"data":[
              {"id":"anthropic/claude-sonnet-4.5","pricing":{"prompt":"0.000003","completion":"0.000015"}},
              {"id":"openrouter/auto","pricing":{"prompt":"-1","completion":"-1"}},
              {"id":"no/pricing"}
            ]}
            """;

        var response = JsonSerializer.Deserialize(json, VeniceJsonContext.Default.OpenRouterModelsResponse)!;
        var models = OpenRouterMapper.ToModels(response);

        Assert.Equal(new ModelPrice(3m, 15m), CostEstimator.PriceOf(models[0]));
        Assert.Null(CostEstimator.PriceOf(models[1]));
        Assert.Null(CostEstimator.PriceOf(models[2]));
    }
}
