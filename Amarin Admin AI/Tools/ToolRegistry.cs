using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ITool> All => _tools.Values.ToList();

    public List<ToolDefinition> GetDefinitions()
    {
        return All.Select(tool => new ToolDefinition
        {
            Function = new FunctionDefinition
            {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = tool.ParametersSchema
            }
        }).ToList();
    }

    public async Task<ToolResult> ExecuteAsync(
        string name,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            return ToolResult.Fail($"Unknown tool: {name}");
        }

        return await tool.ExecuteAsync(arguments, cancellationToken);
    }
}