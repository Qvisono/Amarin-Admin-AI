using System.Windows;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The logo lookup fails silently — a key with no matching resource just falls back to a letter
/// — so the mapping and the two theme dictionaries have to be checked against each other.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ModelLogoTests
{
    private readonly WpfFixture _wpf;

    public ModelLogoTests(WpfFixture wpf) => _wpf = wpf;

    private static readonly string[] Dictionaries =
    [
        "UI/Theme/AiLogos.Dark.xaml",
        "UI/Theme/AiLogos.Light.xaml"
    ];

    private static ResourceDictionary Load(string relative) =>
        (ResourceDictionary)Application.LoadComponent(
            new Uri("/Amarin Admin AI;component/" + relative, UriKind.Relative));

    [Theory]
    [InlineData("auto", "Auto")]
    [InlineData("claude-sonnet-5", "Claude")]
    [InlineData("grok-4-6", "Grok")]
    [InlineData("grok-imagine-image", "Grok")]
    [InlineData("deepseek-v4-flash-0731-fast", "DeepSeek")]
    [InlineData("kimi-k2-7-code", "Kimi")]
    [InlineData("minimax-m3-preview", "MiniMax")]
    [InlineData("qwen-3-7-plus", "Qwen")]
    [InlineData("qwen-image", "Qwen")]
    [InlineData("openai-gpt-53-codex", "OpenAI")]
    [InlineData("openai-gpt-56-luna", "OpenAI")]
    [InlineData("gpt-image-2", "OpenAI")]
    [InlineData("gemini-3-6-flash", "GoogleGemini")]
    [InlineData("gemma-3-27b", "Gemma")]
    [InlineData("llama-3.2-3b", "Llama")]
    [InlineData("mistral-31-24b", "Mistral")]
    [InlineData("ministral-8b", "Mistral")]
    [InlineData("nano-banana-pro", "Google")]
    [InlineData("nano-banana-2", "Google")]
    [InlineData("venice-sd35", "Stability")]
    [InlineData("flux-2-dev", "Flux")]
    [InlineData("seedream-4", "ByteDance")]
    [InlineData("seedance-1-pro", "ByteDance")]
    [InlineData("moonshot-v1-8k", "Moonshot")]
    [InlineData("anthropic-claude-legacy", "Claude")]
    [InlineData("zai-org-glm-5-1", "GLM")]
    [InlineData("xiaomi-mimo-7b", "MiMo")]
    [InlineData("seed-oss-36b", "Seed")]
    public void Known_models_resolve_to_the_expected_logo(string modelId, string expected) =>
        Assert.Equal(expected, VeniceModelCatalog.GetLogoResourceKey(modelId));

    [Fact]
    public void An_unknown_model_falls_back_to_a_letter()
    {
        Assert.Null(VeniceModelCatalog.GetLogoResourceKey("mystery-model-9000"));
        Assert.Equal("M", VeniceModelCatalog.GetLogoLetter("mystery-model-9000"));
    }

    [Fact]
    public void Every_key_the_mapping_can_return_exists_in_both_themes()
    {
        var dictionaries = _wpf.Ui.Invoke(() => Dictionaries.Select(Load).ToList());

        // Everything reachable from the table, plus "Auto" which is returned separately.
        var ids = new[]
        {
            "auto", "claude", "grok", "deepseek", "kimi", "moonshot", "minimax", "mimo", "glm",
            "qwen", "gemma", "gemini", "llama", "nemotron", "nvidia", "hunyuan", "baichuan",
            "arcee", "aion", "mercury", "inception", "spark", "perplexity", "sonar", "cohere",
            "command-r", "mistral", "ministral", "magistral", "codestral", "devstral",
            "pixtral", "openai", "gpt", "codex", "nano-banana", "flux", "seedream", "seedance",
            "bytedance", "doubao", "seed", "venice-sd", "stable-diffusion", "sdxl", "kling",
            "pixverse", "hailuo", "runway", "luma", "pika", "vidu", "anthropic", "google",
            "alibaba"
        };

        var missing = new List<string>();
        foreach (var id in ids)
        {
            if (VeniceModelCatalog.GetLogoResourceKey(id) is not { } key)
            {
                missing.Add($"{id}: no key");
                continue;
            }

            for (var i = 0; i < dictionaries.Count; i++)
            {
                if (!dictionaries[i].Contains(key))
                {
                    missing.Add($"{id} -> {key} missing from {Dictionaries[i]}");
                }
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Both_themes_define_exactly_the_same_logos()
    {
        var keys = _wpf.Ui.Invoke(() => Dictionaries
            .Select(path => Load(path).Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(k => k).ToList())
            .ToList());

        Assert.Equal(keys[0], keys[1]);
    }

    [Fact]
    public void The_two_themes_actually_use_different_ink()
    {
        // A copy-paste that left the dark artwork in the light file would render invisible.
        var dark = File.ReadAllText(ProjectFile("UI/Theme/AiLogos.Dark.xaml"));
        var light = File.ReadAllText(ProjectFile("UI/Theme/AiLogos.Light.xaml"));

        Assert.Contains("Brush=\"White\"", dark, StringComparison.Ordinal);
        Assert.DoesNotContain("Brush=\"White\"", light, StringComparison.Ordinal);
        Assert.Contains("Brush=\"#FF16161A\"", light, StringComparison.Ordinal);
        Assert.DoesNotContain("Brush=\"#FF16161A\"", dark, StringComparison.Ordinal);

        // Brand colours are identity, not ink: they must survive in both.
        foreach (var brand in new[] { "#FF76B900", "#FF4285F4", "#FFFA520F", "#FF6950EF" })
        {
            Assert.Contains(brand, dark, StringComparison.Ordinal);
            Assert.Contains(brand, light, StringComparison.Ordinal);
        }
    }

    private static string ProjectFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Amarin Admin AI", relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
