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
    public void The_tier_left_the_chat_prompt_and_people_who_saved_an_old_one_are_migrated()
    {
        // Уровень называла модель чата и завышала его; теперь его называет маршрутизатор, и
        // слова уровня ушли из промпта вместе с аргументом.
        Assert.Contains("\"fast\"", ChatEngine.LegacyDefaultTechPromptV17, StringComparison.Ordinal);
        Assert.DoesNotContain("\"fast\"", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains("notes", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);

        foreach (var saved in new[]
                 {
                     ChatEngine.LegacyDefaultTechPromptV13,
                     ChatEngine.LegacyDefaultTechPromptV17
                 })
        {
            var settings = AppSettings.CreateDefault();
            settings.TechAiPrompt = saved;
            Assert.True(AppSettingsStore.MigrateLegacyChatPrompts(settings));
            Assert.Equal("", settings.TechAiPrompt);
        }
    }

    [Fact]
    public void Hurrying_is_a_note_rather_than_a_word_shouted_at_the_agent()
    {
        // Asked to hurry, the model wrote "СРОЧНО" into the prompt — which the agent reads and
        // can do nothing with. Раньше вместо этого выбирали уровень; теперь это пишут в пометке,
        // и распорядиться ею может только маршрутизатор.
        Assert.Contains("СРОЧНО", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains("goes into notes", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Hurry is a tier", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
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
    public void Choosing_the_model_is_no_longer_the_chat_models_job()
    {
        // Дисциплину уровней правили дважды и оба раза ненадолго: модель всё равно завышала
        // уровень на рутине. Теперь правила нет вовсе — есть запрет решать это самой.
        Assert.Contains("You do not pick the model", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "weigh the work, not the wording", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MODELS block of this prompt", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);

        var settings = AppSettings.CreateDefault();
        settings.TechAiPrompt = ChatEngine.LegacyDefaultTechPromptV16;
        Assert.True(AppSettingsStore.MigrateLegacyChatPrompts(settings));
        Assert.Equal("", settings.TechAiPrompt);
    }

    [Fact]
    public void The_archived_prompt_is_the_old_text_and_not_a_copy_of_the_new_one()
    {
        // Архив копируется вручную, и копия, случайно совпавшая с новым текстом, сделала бы
        // миграцию тихой пустышкой: сохранённый старый промпт не совпал бы ни с чем и остался
        // бы у человека навсегда.
        Assert.NotEqual(ChatEngine.DefaultTechPrompt, ChatEngine.LegacyDefaultTechPromptV16);
        Assert.NotEqual(ChatEngine.DefaultTechPrompt, ChatEngine.LegacyDefaultTechPromptV17);
        Assert.Contains(
            "lite = one check/listing.", ChatEngine.LegacyDefaultTechPromptV16, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "lite = one check/listing.", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains(
            "complexity is exactly", ChatEngine.LegacyDefaultTechPromptV17, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "complexity is exactly", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
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

}
