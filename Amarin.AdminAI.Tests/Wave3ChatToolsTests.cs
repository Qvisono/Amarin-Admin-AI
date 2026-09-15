using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

public sealed class Wave3ChatToolsTests
{
    [Fact]
    public async Task Read_file_returns_file_contents()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "note.txt");
            await File.WriteAllTextAsync(path, "hello wave3");
            var tool = new ReadFileTool();
            var result = await tool.ExecuteAsync(JsonSchema.Parse($$"""{"path":{{JsonSerializer.Serialize(path)}}}"""));
            Assert.True(result.Success);
            Assert.Equal("hello wave3", result.Output);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Read_file_lists_directory()
    {
        var dir = NewTempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "a");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            var tool = new ReadFileTool();
            var result = await tool.ExecuteAsync(JsonSchema.Parse($$"""{"path":{{JsonSerializer.Serialize(dir)}}}"""));
            Assert.True(result.Success);
            Assert.Contains("[FILE] a.txt", result.Output, StringComparison.Ordinal);
            Assert.Contains("[DIR]  sub", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Write_file_creates_parent_and_writes()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "nested", "out.txt");
            var tool = new WriteFileTool();
            var result = await tool.ExecuteAsync(JsonSchema.Parse(
                $$"""{"path":{{JsonSerializer.Serialize(path)}},"content":"saved"}"""));
            Assert.True(result.Success, result.Output);
            Assert.True(File.Exists(path));
            Assert.Equal("saved", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Tool_preview_summarizes_multiline_output()
    {
        var preview = ChatToolPreview.Summarize(ToolResult.Ok("first line\nsecond\nthird"));
        Assert.Equal("first line (+ещё 2)", preview);
        Assert.Equal("boom", ChatToolPreview.Summarize(ToolResult.Fail("boom")));
        Assert.StartsWith("ERROR:", ChatToolPreview.FormatForApi(ToolResult.Fail("nope")), StringComparison.Ordinal);
    }

    [Fact]
    public void Default_tech_prompt_names_chat_tools_and_not_ask_user()
    {
        Assert.Contains("read_file", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains("write_file", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains("search_web", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains("init_agent", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains("lite", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains("heavy", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("ask_user", ChatEngine.DefaultTechPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Laconic", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("no filler", ChatEngine.DefaultTechPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("list services", ChatEngine.DefaultTechPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user's actual request", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains("re-run init_agent", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains("ASCII", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains("Never refuse", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains("blank slate", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains("Call init_agent as a tool", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Contains("Nothing after }", ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
        Assert.Equal("", AppSettings.CreateDefault().MainPrompt);
        Assert.Contains("Unicode emoji", Agent.BaseSystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(":)", Agent.BaseSystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_tech_prompt_is_not_the_agent_prompt()
    {
        Assert.DoesNotContain("read_file", Agent.BaseSystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("init_agent", Agent.BaseSystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(Agent.BaseSystemPrompt.Trim(), ChatEngine.DefaultTechPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Router_treats_simple_checks_as_lite()
    {
        Assert.Contains("status check", ChatEngine.RouterSystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unsure", ChatEngine.RouterSystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lite", ChatEngine.RouterSystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("list services", ChatEngine.RouterSystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Init_agent_prompt_must_restate_the_user_request()
    {
        var tool = new InitAgentTool(new AgentSlotLimiter(), new FakeHost());
        Assert.Contains("user's actual request", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("still lite", tool.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("list services", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not copy examples", tool.ParametersSchema.GetRawText(), StringComparison.Ordinal);
    }

    private sealed class FakeHost : IAgentHost
    {
        public Task<ToolResult> RunAsync(string prompt, string complexity, CancellationToken cancellationToken) =>
            Task.FromResult(ToolResult.Ok("x"));
    }

    [Fact]
    public void Chat_registry_exposes_only_ordinary_ai_tools()
    {
        var registry = new ToolRegistry(
        [
            new ReadFileTool(),
            new WriteFileTool(),
            new WebSearchTool((_, _) => Task.FromResult("ok"))
        ]);
        var names = registry.GetDefinitions().Select(d => d.Function.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["read_file", "search_web", "write_file"], names);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "amarin-wave3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // temp leftovers are acceptable
        }
    }
}
