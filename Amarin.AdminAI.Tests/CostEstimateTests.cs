using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Оценка цены до отправки (E2): цены из обоих каталогов и сама оценка.</summary>
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

    [Fact]
    public void The_estimate_spans_the_candidates_and_skips_unpriced_ones()
    {
        var estimate = CostEstimator.Estimate(10_000, 1_000, [new ModelPrice(1m, 2m), null, new ModelPrice(3m, 16m)])!.Value;

        Assert.Equal(0.012m, estimate.Low);
        Assert.Equal(0.046m, estimate.High);
        Assert.True(estimate.IsRange);
        Assert.Equal("≈ $0.01–0.05", CostEstimator.Format(estimate));
        Assert.Null(CostEstimator.Estimate(10, 10, [null]));
    }

    [Theory]
    [InlineData(0.004, 0.004, "< $0.01")]
    [InlineData(0.03, 0.03, "≈ $0.03")]
    [InlineData(0.002, 0.09, "≈ $0.002–0.09")]
    public void The_label_is_short(double low, double high, string expected)
    {
        Assert.Equal(expected, CostEstimator.Format(new CostEstimate((decimal)low, (decimal)high, 0, 0)));
    }

    [Fact]
    public void The_answer_length_follows_this_chats_recent_answers()
    {
        var session = new ChatSession();
        Assert.Equal(CostEstimator.DefaultOutputTokens, CostEstimator.TypicalOutputTokens(session));

        session.Messages.Add(new ChatDisplayMessage { Role = "user", Text = new string('q', 4000) });
        session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Text = new string('a', 400) });
        session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Text = new string('a', 1200) });

        Assert.Equal(200, CostEstimator.TypicalOutputTokens(session));
    }
}
