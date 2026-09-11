using System.Reflection;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The third agent tier. Its whole point is not spending flagship money on trivial work, so the
/// failure that matters is the quiet one: a "fast" request resolving to somebody else's model.
/// </summary>
public sealed class FastAgentTierTests
{
    private static string Resolve(string complexity, AppSettings settings) =>
        (string)typeof(AgentHost)
            .GetMethod("ResolveModel", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [complexity, settings])!;

    private static ReasoningSettings Reasoning(string complexity, AppSettings settings) =>
        (ReasoningSettings)typeof(AgentHost)
            .GetMethod("ReasoningFor", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [complexity, settings])!;

    [Fact]
    public void Fast_resolves_to_its_own_model_and_not_to_lite()
    {
        var settings = AppSettings.CreateDefault();
        settings.AgentFastModelId = "fast-model";
        settings.AgentLiteModelId = "lite-model";
        settings.AgentHeavyModelId = "heavy-model";

        // The two methods used to be `heavy ? … : …`, where "not heavy" silently meant lite.
        Assert.Equal("fast-model", Resolve("fast", settings));
        Assert.Equal("lite-model", Resolve("lite", settings));
        Assert.Equal("heavy-model", Resolve("heavy", settings));
    }

    [Fact]
    public void Fast_gets_its_own_reasoning_slot()
    {
        var settings = AppSettings.CreateDefault();
        settings.AgentFastReasoning = new ReasoningSettings { DisableThinking = true };
        settings.AgentLiteReasoning = new ReasoningSettings { DisableThinking = false };

        Assert.True(Reasoning("fast", settings).DisableThinking);
        Assert.False(Reasoning("lite", settings).DisableThinking);
    }

    [Fact]
    public void An_empty_model_id_still_falls_back_rather_than_going_out_blank()
    {
        var settings = AppSettings.CreateDefault();
        settings.AgentFastModelId = "   ";

        Assert.Equal(AgentHost.ForcedAgentModelId, Resolve("fast", settings));
    }

    [Fact]
    public void The_shipped_default_is_the_flash_model()
    {
        Assert.Equal("deepseek-v4-flash-0731-fast", AppSettings.CreateDefault().AgentFastModelId);
        Assert.True(AppSettings.CreateDefault().AgentFastReasoning.DisableThinking);
    }

    [Fact]
    public async Task Init_agent_accepts_fast_and_passes_it_through()
    {
        var host = new RecordingHost();
        var tool = new InitAgentTool(new AgentSlotLimiter(), host);

        var result = await tool.ExecuteAsync(
            JsonSchema.Parse("""{"prompt":"глянь свободное место","complexity":"fast"}"""));

        Assert.True(result.Success);
        Assert.Equal("fast", host.LastComplexity);
    }

    [Fact]
    public async Task Init_agent_still_refuses_a_model_id_in_place_of_a_tier()
    {
        var tool = new InitAgentTool(new AgentSlotLimiter(), new RecordingHost());

        var result = await tool.ExecuteAsync(
            JsonSchema.Parse("""{"prompt":"do","complexity":"deepseek-v4-flash-0731-fast"}"""));

        Assert.False(result.Success);
        Assert.Contains("lite", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fast", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_slash_command_understands_the_new_tier_both_ways()
    {
        Assert.Equal("fast", ChatCommands.TryParse("/agent-fast посмотри диск")!.Value.Complexity);
        Assert.Equal("fast", ChatCommands.TryParse("/agent fast посмотри диск")!.Value.Complexity);

        // Without a prompt after it, "fast" is the prompt, not a modifier.
        Assert.Equal("fast", ChatCommands.TryParse("/agent fast")!.Value.Argument);
    }

    [Fact]
    public void The_model_has_a_name_rather_than_a_humanised_id()
    {
        Assert.Equal("DeepSeek V4 Flash", VeniceModelCatalog.GetDisplayName("deepseek-v4-flash-0731-fast"));
    }

    [Fact]
    public void The_chat_prompt_teaches_the_tier_and_reaches_people_who_saved_the_old_one()
    {
        Assert.Contains("\"fast\"", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"fast\"", ChatEngine.LegacyDefaultTechPromptV13, StringComparison.Ordinal);

        var settings = AppSettings.CreateDefault();
        settings.TechAiPrompt = ChatEngine.LegacyDefaultTechPromptV13;
        Assert.True(AppSettingsStore.MigrateLegacyChatPrompts(settings));
        Assert.Equal("", settings.TechAiPrompt);
    }

    [Fact]
    public void Hurrying_is_a_tier_rather_than_a_word_shouted_at_the_agent()
    {
        // Asked to hurry, the model kept the slow tier and wrote "СРОЧНО" into the prompt —
        // which the agent reads and can do nothing with, while the model stays just as slow.
        Assert.Contains("СРОЧНО", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains("Hurry is a tier", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("СРОЧНО", ChatEngine.LegacyDefaultTechPromptV14, StringComparison.Ordinal);
    }

    [Fact]
    public void The_follow_up_rules_reach_people_who_saved_the_previous_default()
    {
        Assert.Contains("A LINE TYPED WHILE YOU WORK", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "A LINE TYPED WHILE YOU WORK", ChatEngine.LegacyDefaultTechPromptV14, StringComparison.Ordinal);

        var settings = AppSettings.CreateDefault();
        settings.TechAiPrompt = ChatEngine.LegacyDefaultTechPromptV14;
        Assert.True(AppSettingsStore.MigrateLegacyChatPrompts(settings));
        Assert.Equal("", settings.TechAiPrompt);
    }

    [Fact]
    public void The_fast_reasoning_slot_is_filled_in_for_a_settings_file_written_before_it_existed()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-fast-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Ровно то, что лежит у всех, кто обновился: поля быстрого агента в файле нет.
            File.WriteAllText(
                Path.Combine(root, "settings.json"),
                """{ "agentLiteModelId": "grok-4-3", "agentFastReasoning": null }""");

            Assert.NotNull(new AppSettingsStore(root).Load().AgentFastReasoning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingHost : IAgentHost
    {
        public string? LastComplexity { get; private set; }

        public Task<ToolResult> RunAsync(string prompt, string complexity, CancellationToken cancellationToken)
        {
            LastComplexity = complexity;
            return Task.FromResult(ToolResult.Ok("готово"));
        }
    }
}
