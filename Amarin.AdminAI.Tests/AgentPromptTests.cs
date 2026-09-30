using System.Reflection;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Системный промпт агента: без личной политики про одну компанию, с правдой о командах и на
/// языке интерфейса, а не всегда по-русски.
/// </summary>
public sealed class AgentPromptTests
{
    [Fact]
    public void The_yandex_policy_and_its_exception_to_the_delete_rule_are_gone()
    {
        // Исключение из «никогда не удаляй файлы», прошитое в заводской промпт, — это
        // разрешение удалять, которое никто на этой машине не давал.
        Assert.DoesNotContain("Yandex", Agent.BaseSystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXCEPT", Agent.BaseSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("NEVER delete existing files or directories.", Agent.BaseSystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_command_list_names_every_tier_the_chat_understands()
    {
        // До 1.28.0 промпт знал только «/agent lite» и велел не выдумывать других команд — то
        // есть отрицать fast и heavy, которые разбирает ChatCommands.
        foreach (var command in new[] { "/agent <task>", "/agent lite|fast|heavy", "/agent-lite", "/agent-fast", "/agent-heavy" })
        {
            Assert.Contains(command, Agent.BaseSystemPrompt, StringComparison.Ordinal);
        }

        foreach (var tier in new[] { ChatCommands.Lite, ChatCommands.Fast, ChatCommands.Heavy })
        {
            Assert.NotNull(ChatCommands.TryParse($"/agent-{tier} проверь диск"));
            Assert.Equal(tier, ChatCommands.TryParse($"/agent {tier} проверь диск")!.Value.Complexity);
        }
    }

    [Fact]
    public void The_rules_are_written_without_quoted_user_phrases()
    {
        // Разобранная в промпте фраза пользователя тянет ответы к себе (см. CLAUDE.md, «правило,
        // а не разбор случая»); кириллица в прежнем промпте была ровно такими цитатами.
        Assert.DoesNotContain(Agent.BaseSystemPrompt, IsCyrillic);
        Assert.DoesNotContain("Example", Agent.BaseSystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(examples)", Agent.BaseSystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCyrillic(char c) => c is >= 'Ѐ' and <= 'ӿ';

    [Fact]
    public void Replies_and_explanations_follow_the_interface_language()
    {
        Assert.DoesNotContain("Russian", Agent.BaseSystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("interface language", Agent.BaseSystemPrompt, StringComparison.Ordinal);

        // Язык называет приписка к промпту — она доходит и до сохранённого человеком текста.
        var paths = (string)typeof(Agent)
            .GetMethod("BuildMachinePathsPrompt", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null)!;
        Assert.Contains("Interface language: " + ChatTitle.LanguageName(), paths, StringComparison.Ordinal);
    }

    [Fact]
    public void The_explanation_parameter_no_longer_demands_russian()
    {
        var registry = new ToolRegistry([new PowerShellTool()]);
        var schema = JsonSerializer.Serialize(registry.GetDefinitions());

        Assert.DoesNotContain("Russian", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("interface language", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void An_untouched_saved_copy_of_an_old_agent_prompt_gives_way_to_the_new_one()
    {
        foreach (var legacy in LegacyAgentPrompts.All)
        {
            Assert.Contains("Yandex", legacy, StringComparison.Ordinal);

            var settings = new AppSettings { TechAgentPrompt = legacy.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n" };
            Assert.True(AppSettingsStore.MigrateLegacyChatPrompts(settings));
            Assert.Equal("", settings.TechAgentPrompt);
        }
    }

    [Fact]
    public void A_prompt_the_person_edited_is_left_alone()
    {
        var edited = LegacyAgentPrompts.All[^1] + "\nMy own rule.";
        var settings = new AppSettings { TechAgentPrompt = edited };

        Assert.False(AppSettingsStore.MigrateLegacyChatPrompts(settings));
        Assert.Equal(edited, settings.TechAgentPrompt);
    }
}
