using System.Globalization;
using System.Net;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Цена Polza.ai переводится из рублей один раз, до общего учёта ответа и токенов.</summary>
public sealed class PolzaCostTests
{
    private const string Model = "polza:openai/gpt-test";

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response());
    }

    private static VeniceClient Client(
        string usage,
        bool stream,
        List<VeniceCost> spent,
        Func<decimal?>? rate,
        LlmProvider? provider = null,
        string? earlierUsage = null)
    {
        var actualProvider = provider ?? LlmProvider.Parse("polza");
        var options = new AgentOptions
        {
            Keys = new ApiKeyProvider("test-key", actualProvider),
            Model = Model,
            RublesPerUsd = rate,
            SpendSink = (_, cost, _) => spent.Add(cost)
        };
        var http = new HttpClient(new Handler(() => Response(usage, stream, earlierUsage)));
        return new VeniceClient(http, options)
        {
            // Без курса нельзя подменять неизвестную долларовую цену оценкой из чужого каталога.
            ResolveModelInfo = _ => new VeniceModelInfo
            {
                Id = Model,
                ModelSpec = new VeniceModelSpec
                {
                    Pricing = new VeniceModelPricing
                    {
                        Input = new VeniceUnitPrice { Usd = 100m },
                        Output = new VeniceUnitPrice { Usd = 200m },
                        CacheInput = new VeniceUnitPrice { Usd = 1m }
                    }
                }
            }
        };
    }

    private static HttpResponseMessage Response(string usage, bool stream, string? earlierUsage)
    {
        var content = stream
            ? "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n" +
              (earlierUsage is null ? "" : "data: {\"choices\":[],\"usage\":" + earlierUsage + "}\n") +
              "data: {\"choices\":[],\"usage\":" + usage + "}\ndata: [DONE]\n"
            : "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":" + usage + "}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, stream ? "text/event-stream" : "application/json")
        };
    }

    private static async Task<VeniceCost?> Ask(VeniceClient client, bool stream, string model = Model)
    {
        List<ChatMessage> messages = [new() { Role = "user", Content = ChatContent.Text("q") }];
        if (stream)
        {
            var result = await client.StreamChatCompletionAsync(model, model, messages, null, null, new VeniceParameters(), null, fallback: false);
            Assert.Equal("ok", result.Text);
            return result.Cost;
        }

        var answer = await client.CreateChatCompletionAsync(model, messages, null, null, new VeniceParameters());
        Assert.Equal("ok", ChatContent.ReadText(answer.Choices.Single().Message.Content));
        return answer.ReportedCost();
    }

    [Theory]
    [InlineData(false, "\"cost_rub\":180,\"cost\":900")]
    [InlineData(true, "\"cost_rub\":180,\"cost\":900")]
    [InlineData(false, "\"cost\":180")]
    [InlineData(true, "\"cost\":180")]
    public async Task Ruble_cost_is_converted_and_the_explicit_field_has_priority(bool stream, string fields)
    {
        var spent = new List<VeniceCost>();
        var client = Client("{\"prompt_tokens\":1000,\"completion_tokens\":500,\"total_tokens\":1500,\"prompt_tokens_details\":{\"cached_tokens\":800}," + fields + "}", stream, spent, () => 90m);

        var cost = await Ask(client, stream);

        Assert.NotNull(cost);
        Assert.Equal(2m, cost.Usd);
        Assert.True(cost.HasData);
        Assert.True(cost.Estimated);
        var recorded = Assert.Single(spent);
        Assert.Equal(2m, recorded.Usd);
        Assert.Equal(1000, recorded.InputTokens);
        Assert.Equal(500, recorded.OutputTokens);
        Assert.True(recorded.Estimated);
        Assert.Equal(2m, client.RequestCost.Usd);
        Assert.True(client.RequestCost.Estimated);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "0")]
    [InlineData(true, "0")]
    [InlineData(false, "-90")]
    [InlineData(true, "-90")]
    public async Task Without_a_valid_rate_only_tokens_are_booked(bool stream, string? rateText)
    {
        var spent = new List<VeniceCost>();
        decimal? rate = rateText is null ? null : decimal.Parse(rateText, CultureInfo.InvariantCulture);
        var client = Client("{\"prompt_tokens\":1000,\"completion_tokens\":500,\"cost_rub\":180,\"cost\":900}", stream, spent, () => rate);

        var cost = await Ask(client, stream);

        Assert.True(cost is null || !cost.HasData);
        var recorded = Assert.Single(spent);
        Assert.Equal(0m, recorded.Usd);
        Assert.False(recorded.HasData);
        Assert.False(recorded.Estimated);
        Assert.Equal(1000, recorded.InputTokens);
        Assert.Equal(500, recorded.OutputTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zero_is_a_known_converted_cost_and_missing_cost_stays_unknown(bool stream)
    {
        var zeroSpent = new List<VeniceCost>();
        var zeroClient = Client("{\"prompt_tokens\":10,\"cost_rub\":0,\"cost\":900}", stream, zeroSpent, () => 90m);
        var zero = await Ask(zeroClient, stream);
        Assert.NotNull(zero);
        Assert.Equal(0m, zero.Usd);
        Assert.True(zero.HasData);
        Assert.True(zero.Estimated);
        Assert.True(Assert.Single(zeroSpent).HasData);

        var missingSpent = new List<VeniceCost>();
        var missingClient = Client("{\"prompt_tokens\":10}", stream, missingSpent, () => 90m);
        var missing = await Ask(missingClient, stream);
        Assert.True(missing is null || !missing.HasData);
        Assert.False(Assert.Single(missingSpent).HasData);
    }

    [Fact]
    public async Task Repeated_stream_usage_is_a_total_and_is_booked_once()
    {
        var spent = new List<VeniceCost>();
        var client = Client(
            "{\"prompt_tokens\":1000,\"completion_tokens\":500,\"cost_rub\":180}", true, spent, () => 90m,
            earlierUsage: "{\"prompt_tokens\":1000,\"completion_tokens\":100,\"cost_rub\":90}");

        var cost = await Ask(client, true);

        Assert.Equal(2m, cost!.Usd);
        Assert.Equal(2m, Assert.Single(spent).Usd);
        Assert.Equal(2m, client.RequestCost.Usd);
        Assert.Equal(500, client.RequestCost.OutputTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_rate_applies_only_to_new_requests(bool stream)
    {
        var spent = new List<VeniceCost>();
        decimal rate = 90m;
        var client = Client("{\"prompt_tokens\":10,\"cost_rub\":180}", stream, spent, () => rate);

        var first = await Ask(client, stream);
        rate = 180m;
        var second = await Ask(client, stream);

        Assert.Equal(2m, first!.Usd);
        Assert.Equal(1m, second!.Usd);
        Assert.Equal(2m, spent[0].Usd);
        Assert.Equal(1m, spent[1].Usd);
        Assert.Equal(3m, client.RequestCost.Usd);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dollar_provider_cost_is_not_converted(bool stream)
    {
        var spent = new List<VeniceCost>();
        var client = Client("{\"prompt_tokens\":10,\"cost\":180,\"cost_rub\":900}", stream, spent, () => 90m, LlmProvider.OpenRouter);

        var cost = await Ask(client, stream, "openrouter:openai/gpt-test");

        Assert.Equal(180m, cost!.Usd);
        Assert.False(cost.Estimated);
        Assert.Equal(180m, Assert.Single(spent).Usd);
    }

    [Fact]
    public async Task Cache_tokens_and_the_estimated_price_remain_available_to_callers()
    {
        var client = Client(
            "{\"prompt_tokens\":1000,\"completion_tokens\":500,\"prompt_tokens_details\":{\"cached_tokens\":800,\"cache_write_tokens\":100},\"cost_rub\":180}",
            false, [], () => 90m);

        var answer = await client.CreateChatCompletionAsync(
            Model, [new ChatMessage { Role = "user", Content = ChatContent.Text("q") }], null, null, new VeniceParameters());

        Assert.Equal(800, answer.Usage!.CacheReadTokens);
        Assert.Equal(100, answer.Usage.CacheWriteTokens);
        Assert.Equal(2m, answer.ReportedCost()!.Usd);
        Assert.True(answer.ReportedCost()!.Estimated);
    }

    [Fact]
    public async Task An_auxiliary_request_preserves_the_currency_conversion_and_its_source()
    {
        var spent = new List<VeniceCost>();
        var options = new AgentOptions
        {
            Keys = new ApiKeyProvider("test-key", LlmProvider.Parse("polza")),
            Model = Model,
            RublesPerUsd = () => 90m,
            SpendSink = (_, cost, _) => spent.Add(cost)
        };
        var titles = new ChatTitleGenerator(
            new HttpClient(new Handler(() => Response("{\"prompt_tokens\":10,\"cost_rub\":180}", false, null))),
            options,
            () => new AppSettings { TitleModelId = Model });

        var title = await titles.GenerateAsync("q");

        Assert.Equal(2m, title.Cost!.Usd);
        Assert.True(title.Cost.Estimated);
        Assert.Equal(2m, Assert.Single(spent).Usd);
    }

    [Fact]
    public async Task A_catalog_estimate_preserves_its_source_for_auxiliary_callers()
    {
        var spent = new List<VeniceCost>();
        var client = Client("{\"prompt_tokens\":1000,\"completion_tokens\":500}", false, spent, null, LlmProvider.OpenAI);

        var cost = await Ask(client, false, "openai:gpt-test");

        Assert.Equal(0.2m, cost!.Usd);
        Assert.True(cost.Estimated);
        Assert.True(Assert.Single(spent).Estimated);
    }

    [Fact]
    public void Separate_stream_cost_takes_priority_over_cumulative_usage_cost()
    {
        var accumulator = new ChatStreamAccumulator();
        accumulator.Apply(new ChatCompletionChunk { Usage = new VeniceUsage { Cost = 1m } });
        accumulator.Apply(new ChatCompletionChunk { Cost = new VeniceCostResponse { Usd = 2m } });
        accumulator.Apply(new ChatCompletionChunk { Usage = new VeniceUsage { Cost = 3m } });
        Assert.Equal(2m, accumulator.Cost.Usd);
        accumulator.Apply(new ChatCompletionChunk { Cost = new VeniceCostResponse { Usd = 4m } });
        Assert.Equal(6m, accumulator.Cost.Usd);
    }

    [Fact]
    public void Invalid_or_overflowing_conversion_has_no_dollar_value()
    {
        Assert.Null(ProviderMoney.ToUsd(-1m, 90m));
        Assert.Null(ProviderMoney.ToUsd(decimal.MaxValue, 0.0000000000000000000000000001m));
        Assert.Equal(0m, ProviderMoney.ToUsd(0m, 90m));
    }

    [Theory]
    [InlineData(false, "$2", "$2 USD")]
    [InlineData(true, "≈$2", "≈$2 USD")]
    public void Estimated_prices_remain_identifiable_in_chat_and_tool_text(bool estimated, string chat, string detail)
    {
        var cost = new VeniceCost { Usd = 2m, HasData = true, Estimated = estimated };
        Assert.Equal(chat, ChatFormat.Cost(cost));
        Assert.Equal(detail, cost.Format());
        Assert.Equal("", ChatFormat.Cost(new VeniceCost { Estimated = true }));
    }
}
