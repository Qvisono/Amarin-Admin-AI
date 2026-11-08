using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Логотип определяется автором модели, а транспорт и варианты не меняют его.</summary>
public sealed class ModelLogoCatalogTests
{
    [Theory]
    [InlineData("openrouter:google/gemini-2.5-flash", "GoogleGemini")]
    [InlineData("openrouter:google/gemma-3-27b-it", "Gemma")]
    [InlineData("openrouter:anthropic/claude-sonnet-4.5", "Claude")]
    [InlineData("openrouter:nvidia/llama-3.1-nemotron-70b-instruct", "NVIDIA")]
    [InlineData("openrouter:bytedance-seed/seed-oss-36b-instruct", "Seed")]
    [InlineData("openrouter:aion-labs/aion-rp-llama-3.1-8b", "AionLabs")]
    [InlineData("openrouter:cognitivecomputations/dolphin-mistral-24b", "Dolphin")]
    [InlineData("openrouter:meta/muse-wonder", "Muse")]
    [InlineData("openrouter:meta/a-new-gemma-derivative", "Meta")]
    [InlineData("openrouter:nousresearch/hermes-4-405b", "NousResearch")]
    [InlineData("openrouter:amazon/nova-pro-v1", "Nova")]
    [InlineData("openrouter:microsoft/phi-4", "Microsoft")]
    [InlineData("openrouter:tencent/hy4", "Hunyuan")]
    [InlineData("openrouter:openrouter/auto", "OpenRouter")]
    [InlineData("auto", "Auto")]
    public void Namespaced_models_resolve_the_author_or_its_own_family(string id, string expected) =>
        Assert.Equal(expected, VeniceModelCatalog.GetLogoResourceKey(id));

    [Theory]
    [InlineData("unknown-author/llama-3-70b")]
    [InlineData("unknown-author/gpt-6")]
    [InlineData("unknown-author/claude-compatible")]
    [InlineData("my-google-model/unknown")]
    [InlineData("openrouter:unknown-author/qwen3")]
    public void Unknown_authors_do_not_borrow_another_brands_logo(string id) =>
        Assert.Null(VeniceModelCatalog.GetLogoResourceKey(id));

    [Theory]
    [InlineData("openrouter:~google/gemini-latest:nitro", "GoogleGemini")]
    [InlineData("custom-a1b2c3:anthropic/claude-sonnet-4.5:batch", "Claude")]
    [InlineData(" OPENROUTER:~OPENAI/GPT-SOL-LATEST:FREE ", "OpenAI")]
    [InlineData("deepseek:deepseek-chat", "DeepSeek")]
    [InlineData("custom-a1b2c3:muse-spark", "Muse")]
    [InlineData("dolphin-mistral-24b-venice-edition", "Dolphin")]
    [InlineData("hermes-llama-3-70b", "NousResearch")]
    [InlineData("custom-a1b2c3:hermes-4-405b", "NousResearch")]
    [InlineData("custom-a1b2c3:lfm-3-1.7b", "Liquid")]
    public void Transport_latest_alias_and_variant_do_not_change_the_logo(string id, string expected) =>
        Assert.Equal(expected, VeniceModelCatalog.GetLogoResourceKey(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown-model-9000")]
    public void Missing_or_unknown_model_has_no_brand(string? id) =>
        Assert.Null(ModelLogoCatalog.GetResourceKey(id));

    [Fact]
    public void Snapshot_models_keep_their_expected_logo_for_providers_and_variants()
    {
        // Зафиксированные ID проверяются без сети: новый порядок правил не должен незаметно
        // перекрасить производную модель в марку её базового семейства.
        var path = SourceTree.RepositoryFile(Path.Combine("tools", "model-logos", "catalog.snapshot.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var models = document.RootElement.GetProperty("models").EnumerateArray().ToArray();
        Assert.NotEmpty(models);

        foreach (var model in models)
        {
            var id = Assert.IsType<string>(model.GetProperty("id").GetString());
            var expected = Assert.IsType<string>(model.GetProperty("key").GetString());
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.False(string.IsNullOrWhiteSpace(expected));
            Assert.Equal(expected, ModelLogoCatalog.GetResourceKey(id));
            Assert.Equal(expected, ModelLogoCatalog.GetResourceKey("openrouter:" + id));
            Assert.Equal(expected, ModelLogoCatalog.GetResourceKey("custom-a1b2c3:" + id));
            Assert.Equal(expected, ModelLogoCatalog.GetResourceKey(id.ToUpperInvariant()));
            Assert.Equal(expected, ModelLogoCatalog.GetResourceKey("openrouter:" + id + ":nitro"));
            Assert.Equal(expected, ModelLogoCatalog.GetResourceKey("openrouter:" + id + ":batch"));
        }
    }
}
