using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Каталоги и остатки прямых провайдеров — в том виде, каким программа описывает Venice.
/// </summary>
public sealed class ProviderCatalogParsingTests
{
    /// <summary>
    /// OpenAI отдаёт голые идентификаторы и среди них — модели речи и картинок, которые
    /// разговор не ведут. Возможностей он не называет, и без допущения «инструменты есть»
    /// отбор для агента выбросил бы весь список.
    /// </summary>
    [Fact]
    public void An_openai_list_keeps_the_chat_models_and_qualifies_them()
    {
        const string json = """
            {"object":"list","data":[
              {"id":"gpt-6-sol","object":"model","created":1790000000,"owned_by":"openai"},
              {"id":"text-embedding-3-large","object":"model"},
              {"id":"gpt-image-2","object":"model"},
              {"id":"whisper-1","object":"model"},
              {"id":"gpt-6-luna","object":"model"}]}
            """;

        var models = ModelListParser.Parse(json, LlmProvider.OpenAI);

        Assert.Equal(["openai:gpt-6-sol", "openai:gpt-6-luna"], models.Select(model => model.Id));
        Assert.True(models[0].ModelSpec!.Capabilities!.SupportsFunctionCalling);
        Assert.Single(VeniceModelCatalog.FilterAgentic(models), model => model.Id == "openai:gpt-6-sol");
    }

    [Fact]
    public void Gemini_loses_its_models_prefix()
    {
        var models = ModelListParser.Parse(
            """{"data":[{"id":"models/gemini-3.8-flash","object":"model"}]}""", LlmProvider.Google);

        Assert.Equal("google:gemini-3.8-flash", Assert.Single(models).Id);
        Assert.Equal("gemini-3.8-flash", ModelRef.Bare(models[0].Id));
    }

    [Fact]
    public void An_anthropic_list_brings_context_ceiling_and_thinking_modes()
    {
        const string json = """
            {"data":[{"type":"model","id":"claude-sonnet-5-5","display_name":"Claude Sonnet 5.5",
              "created_at":"2026-09-01T00:00:00Z","max_input_tokens":1000000,"max_tokens":128000,
              "capabilities":{"image_input":{"supported":true},
                "thinking":{"supported":true,"types":{"adaptive":{"supported":true},"between_tools":{"supported":true},"enabled":{"supported":false}}},
                "effort":{"supported":true,"low":{"supported":true},"medium":{"supported":true},"high":{"supported":true},"max":{"supported":true}}}}],
             "has_more":true,"last_id":"claude-sonnet-5-5"}
            """;

        var model = Assert.Single(ModelListParser.Parse(json, LlmProvider.Anthropic));
        var caps = model.ModelSpec!.Capabilities!;

        Assert.Equal("anthropic:claude-sonnet-5-5", model.Id);
        Assert.Equal("Claude Sonnet 5.5", model.ModelSpec.Name);
        Assert.Equal(1_000_000, model.ContextLength);
        Assert.Equal(128_000, model.ModelSpec.MaxOutputTokens);
        Assert.True(caps.SupportsVision);
        Assert.Equal(["adaptive", "between_tools"], caps.ThinkingModes!);
        Assert.Equal(["low", "medium", "high", "max"], caps.ReasoningEffortOptions!);
        Assert.Equal("claude-sonnet-5-5", ModelListParser.NextPageAfter(json));
    }

    /// <summary>Перепродавец кладёт цены по образцу OpenRouter — за токен; программа держит за миллион.</summary>
    [Fact]
    public void A_reseller_price_is_read_per_million()
    {
        var model = Assert.Single(ModelListParser.Parse(
            """{"data":[{"id":"claude-haiku-5-5","pricing":{"prompt":"0.0000001","completion":"0.0000005"}}]}""",
            LlmProvider.Custom("abc123")));

        Assert.Equal("custom-abc123:claude-haiku-5-5", model.Id);
        Assert.Equal(0.1m, model.ModelSpec!.Pricing!.Input!.Usd);
        Assert.Equal(0.5m, model.ModelSpec.Pricing.Output!.Usd);
    }

    /// <summary>
    /// OpenRouter называет уровни размышления модели сам. Зашитые low/medium/high отдавали
    /// <c>deepseek-v4-flash-0731</c> уровень medium, которого у неё нет.
    /// </summary>
    [Fact]
    public void OpenRouter_levels_come_from_the_catalog_in_scale_order()
    {
        var response = System.Text.Json.JsonSerializer.Deserialize(
            """
            {"data":[
              {"id":"deepseek/deepseek-v4-flash-0731","supported_parameters":["tools","reasoning"],
               "reasoning":{"supported_efforts":["max","LOW","high"],"default_effort":"high"}},
              {"id":"x-ai/grok-4.7","supported_parameters":["tools","reasoning"]}
            ]}
            """,
            VeniceJsonContext.Default.OpenRouterModelsResponse)!;

        var models = OpenRouterMapper.ToModels(response);

        Assert.Equal(["low", "high", "max"], models[0].ModelSpec!.Capabilities!.ReasoningEffortOptions!);
        Assert.Equal("high", models[0].ModelSpec!.Capabilities!.DefaultReasoningEffort);
        Assert.Equal(["low", "medium", "high"], models[1].ModelSpec!.Capabilities!.ReasoningEffortOptions!);
    }

    [Theory]
    [InlineData("""{"data":[{"type":"model","id":"claude-haiku-5-5"}]}""", WireProtocol.AnthropicMessages)]
    [InlineData("""{"object":"list","data":[{"id":"gpt-6-sol","object":"model"}]}""", WireProtocol.OpenAiChat)]
    public void The_list_shape_tells_the_protocol(string json, WireProtocol expected) =>
        Assert.Equal(expected, ModelListParser.ProtocolOf(json));

    [Fact]
    public void DeepSeek_reports_only_its_dollars()
    {
        var data = ProviderBalances.FromDeepSeek("""
            {"is_available":true,"balance_infos":[
              {"currency":"CNY","total_balance":"110.00"},
              {"currency":"USD","total_balance":"12.50"}]}
            """);

        Assert.Equal(12.5m, data.Balances!.Usd);
        Assert.True(data.AccessPermitted);
    }

    [Fact]
    public void Moonshot_reports_the_available_balance()
    {
        var data = ProviderBalances.FromMoonshot("""{"code":0,"data":{"available_balance":49.58,"cash_balance":3.0},"status":true}""");

        Assert.Equal(49.58m, data.Balances!.Usd);
    }

    /// <summary>Шлюзы one-api: потолок минус потраченное, а потраченное — в центах.</summary>
    [Fact]
    public void A_reseller_gateway_reports_the_limit_minus_usage()
    {
        var data = ProviderBalances.FromResellerBilling(
            """{"object":"billing_subscription","hard_limit_usd":20.0}""",
            """{"object":"list","total_usage":750}""");

        Assert.Equal(12.5m, data!.Balances!.Usd);
        Assert.Equal(7.5m, data.SpentUsd);
    }

    [Fact]
    public void A_gateway_without_a_limit_reports_nothing()
    {
        Assert.Null(ProviderBalances.FromResellerBilling("""{"object":"billing_subscription"}""", null));
    }
}
