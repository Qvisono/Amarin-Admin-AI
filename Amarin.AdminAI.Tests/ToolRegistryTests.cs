using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

public sealed class ToolRegistryTests
{
    [Fact]
    public void GetDefinitions_is_cached_and_includes_explanation_for_confirmable_tools()
    {
        var registry = new ToolRegistry(
        [
            new SystemInfoTool(),
            new RegistryTool(),
            new PowerShellTool()
        ]);

        var first = registry.GetDefinitions();
        var second = registry.GetDefinitions();

        Assert.Equal(3, first.Count);
        Assert.Equal(first.Select(d => d.Function.Name), second.Select(d => d.Function.Name));
        Assert.NotSame(first, second);

        var registryDef = first.Single(d => d.Function.Name == "registry");
        Assert.Contains("explanation", registryDef.Function.Parameters.GetRawText(), StringComparison.Ordinal);

        var powershell = first.Single(d => d.Function.Name == "run_powershell");
        Assert.Contains("explanation", powershell.Function.Parameters.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_unknown_tool_fails()
    {
        var registry = new ToolRegistry([new SystemInfoTool()]);
        var result = await registry.ExecuteAsync("nope", JsonSchema.Parse("{}"));
        Assert.False(result.Success);
        Assert.Contains("Unknown tool", result.Output, StringComparison.OrdinalIgnoreCase);
    }
}
