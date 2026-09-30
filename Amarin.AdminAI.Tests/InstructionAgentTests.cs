using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Инструкции, открытые агенту (C6). По умолчанию закрыты: агент исполняет команды на машине, и
/// инструкция, заведённая для чата, не должна молча доходить до него.
/// </summary>
public sealed class InstructionAgentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-instr-agent-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void Without_the_field_an_instruction_is_closed_to_the_agent()
    {
        var parsed = InstructionLibrary.Parse("---\nname: Сеть\nenabled: true\n---\nТекст", "x", DateTime.Now)!;

        Assert.False(parsed.VisibleToAgent);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("maybe", false)]
    public void The_agent_field_is_read_from_the_header(string value, bool expected)
    {
        var parsed = InstructionLibrary.Parse($"---\nname: Сеть\nagent: {value}\n---\nТекст", "x", DateTime.Now)!;

        Assert.Equal(expected, parsed.VisibleToAgent);
    }

    [Fact]
    public void The_flag_survives_a_save_and_a_reread()
    {
        var library = new InstructionLibrary(_root);
        var saved = library.Save(new Instruction { Name = "Принтеры", Text = "Спулер", VisibleToAgent = true })!;

        var reread = new InstructionLibrary(_root).Find(saved.Id)!;

        Assert.True(reread.VisibleToAgent);
        Assert.Contains("agent: true", InstructionLibrary.Serialize(reread), StringComparison.Ordinal);
    }

    [Fact]
    public void The_agent_snapshot_has_only_enabled_and_opened_entries()
    {
        var library = new InstructionLibrary(_root);
        library.Save(new Instruction { Name = "Открыта", Text = "a", VisibleToAgent = true });
        library.Save(new Instruction { Name = "Только чат", Text = "b" });
        library.Save(new Instruction { Name = "Выключена", Text = "c", VisibleToAgent = true, Enabled = false });

        Assert.Equal(["Открыта"], library.AgentSnapshot().Select(item => item.Name));
    }

    [Fact]
    public void The_agent_index_lists_only_what_was_opened_to_it()
    {
        var open = new Instruction { Id = "a1", Name = "Сеть", Text = "x", VisibleToAgent = true, Triggers = ["DNS"] };
        var closed = new Instruction { Id = "b2", Name = "Секреты", Text = "y" };

        var block = InstructionBriefing.ForAgent([open, closed]);

        Assert.Contains("[a1] Сеть — triggers: DNS", block, StringComparison.Ordinal);
        Assert.DoesNotContain("b2", block, StringComparison.Ordinal);
        Assert.DoesNotContain("init_agent", block, StringComparison.Ordinal);
        Assert.Equal("", InstructionBriefing.ForAgent([closed]));
    }

    [Fact]
    public async Task The_agents_reader_cannot_open_a_closed_instruction()
    {
        var library = new InstructionLibrary(_root);
        var open = library.Save(new Instruction { Name = "Открыта", Text = "можно", VisibleToAgent = true })!;
        var closed = library.Save(new Instruction { Name = "Закрыта", Text = "нельзя" })!;
        var tool = new ReadInstructionTool(library, library.AgentSnapshot());

        var allowed = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { id = open.Id }));
        var byId = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { id = closed.Id }));
        var byName = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { id = "Закрыта" }));

        Assert.True(allowed.Success);
        Assert.False(byId.Success);
        Assert.False(byName.Success);
        Assert.DoesNotContain("нельзя", byId.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_agent_gets_the_reader_only_when_something_is_open_to_it()
    {
        var library = new InstructionLibrary(_root);
        using var http = new HttpClient();
        var venice = new VeniceClient(http, new AgentOptions { ApiKey = "test" });

        var without = AgentTools.Create(venice, http, new DownloadOptions(), instructions: library, agentInstructions: []);
        var with = AgentTools.Create(venice, http, new DownloadOptions(), instructions: library,
            agentInstructions: [new Instruction { Id = "a1", Name = "x", Text = "y", VisibleToAgent = true }]);

        Assert.DoesNotContain(without.GetDefinitions(), definition => definition.Function.Name == ReadInstructionTool.ToolName);
        Assert.Contains(with.GetDefinitions(), definition => definition.Function.Name == ReadInstructionTool.ToolName);
    }
}
