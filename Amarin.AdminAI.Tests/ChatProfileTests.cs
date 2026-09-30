using System.Reflection;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>Профиль чата и шаблоны (D11).</summary>
public sealed class ChatProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-templates-" + Guid.NewGuid().ToString("N"));

    public ChatProfileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Instruction Instruction(string id) => new() { Id = id, Name = "N" + id, Enabled = true };

    [Fact]
    public void A_profile_narrows_the_instructions_and_no_profile_keeps_all()
    {
        IReadOnlyList<Instruction> enabled = [Instruction("a"), Instruction("b"), Instruction("c")];

        Assert.Equal(3, ChatProfile.Filter(enabled, null).Count);
        Assert.Equal(3, ChatProfile.Filter(enabled, new ChatProfile { Prompt = "p" }).Count);
        Assert.Equal(["b"], ChatProfile.Filter(enabled, new ChatProfile { InstructionIds = ["b", "gone"] }).Select(item => item.Id));
        Assert.Empty(ChatProfile.Filter(enabled, new ChatProfile { InstructionIds = [] }));
    }

    [Fact]
    public void An_empty_profile_is_empty()
    {
        Assert.True(new ChatProfile { Prompt = "  " }.IsEmpty);
        Assert.False(new ChatProfile { InstructionIds = [] }.IsEmpty);
    }

    [Fact]
    public void A_template_carries_prompt_model_thinking_and_instructions_into_a_new_chat()
    {
        var source = new ChatSession
        {
            SelectedModelId = "grok-4-6",
            DisableThinking = false,
            ReasoningEffort = "high",
            Profile = new ChatProfile { Prompt = "be terse", InstructionIds = ["x"] }
        };
        var template = ChatTemplate.From("Terse", source);

        var fresh = new ChatSession { SelectedModelId = "other", DisableThinking = true };
        template.ApplyTo(fresh);

        Assert.Equal("grok-4-6", fresh.SelectedModelId);
        Assert.False(fresh.DisableThinking);
        Assert.Equal("high", fresh.ReasoningEffort);
        Assert.Equal("be terse", fresh.Profile!.Prompt);
        Assert.Equal(["x"], fresh.Profile.InstructionIds!);

        // Правка шаблона не трогает уже созданный по нему чат.
        template.InstructionIds!.Add("y");
        Assert.Single(fresh.Profile.InstructionIds!);
    }

    [Fact]
    public void Templates_survive_a_restart_and_can_be_deleted()
    {
        var book = new ChatTemplateBook(_root);
        var template = ChatTemplate.From("One", new ChatSession { SelectedModelId = "m" });
        book.Save(template);
        book.Save(ChatTemplate.From("Two", new ChatSession()));

        var reread = new ChatTemplateBook(_root);
        Assert.Equal(["One", "Two"], reread.All().Select(item => item.Name).Order());

        reread.Delete(template.Id);
        Assert.Equal(["Two"], new ChatTemplateBook(_root).All().Select(item => item.Name));
    }

    [Fact]
    public void Templates_travel_with_the_settings_and_are_wiped_with_the_profile()
    {
        Assert.Equal(DataCategory.Settings, DataBundle.CategoryOf(ChatTemplateBook.FileName));
        Assert.Contains(ChatTemplateBook.FileName, ProfileDataWiperFiles());
    }

    [Fact]
    public void The_chat_prompt_replaces_the_main_prompt_and_hides_other_instructions()
    {
        var library = new InstructionLibrary(_root);
        var kept = library.Save(new Instruction { Name = "Kept one", Text = "t" })!;
        var hidden = library.Save(new Instruction { Name = "Hidden one", Text = "t" })!;
        var settings = AppSettings.CreateDefault();
        settings.MainPrompt = "GENERAL-MAIN-PROMPT";
        var options = new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "grok-4-6", MaxToolRounds = 3 };
        var engine = new ChatEngine(
            new VeniceClient(new HttpClient { BaseAddress = new Uri("https://api.venice.ai/api/v1/") }, options),
            options,
            () => settings,
            new ToolRegistry([new ReadInstructionTool(library)]),
            agents: null,
            instructions: library);

        var session = new ChatSession { Profile = new ChatProfile { Prompt = "CHAT-OWN-PROMPT", InstructionIds = [kept.Id] } };
        var own = engine.CurrentSystemPrompt(session);
        var general = engine.CurrentSystemPrompt(new ChatSession());

        Assert.Contains("CHAT-OWN-PROMPT", own, StringComparison.Ordinal);
        Assert.DoesNotContain("GENERAL-MAIN-PROMPT", own, StringComparison.Ordinal);
        Assert.Contains("Kept one", own, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden one", own, StringComparison.Ordinal);
        Assert.Contains("GENERAL-MAIN-PROMPT", general, StringComparison.Ordinal);
        Assert.Contains(hidden.Name, general, StringComparison.Ordinal);
    }

    private static IEnumerable<string> ProfileDataWiperFiles() =>
        typeof(ProfileDataWiper).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(field => field.FieldType == typeof(string[]))
            .SelectMany(field => (string[])field.GetValue(null)!);
}
