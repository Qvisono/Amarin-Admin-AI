using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>Профиль чата (D11): свой промпт и набор инструкций.</summary>
public sealed class ChatProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-chat-profile-" + Guid.NewGuid().ToString("N"));

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
}
