using System.Text.Json;
using System.Text.Json.Nodes;
using Amarin.Core;

namespace Amarin.Tools;

public sealed class ToolRegistry
{
    /// <summary>
    /// Tools that can trigger the confirmation dialog — inject optional <c>explanation</c>
    /// into their JSON schema so the model can describe the action in plain Russian.
    /// </summary>
    private static readonly HashSet<string> ConfirmableTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "registry",
        "windows_service",
        "filesystem",
        "run_powershell",
        "windows_process",
        "scheduled_task",
        "network",
        "virtualization",
        "download_file",
        "change_rollback",
        "system_repair",
        "disk_management",
        "disk_space",
        "software_inventory",
        "firewall_rules",
        "windows_features",
        "local_users"
    };

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
                Parameters = EnrichSchemaWithExplanation(tool.Name, tool.ParametersSchema)
            }
        }).ToList();
    }

    /// <summary>
    /// Adds optional <c>explanation</c> property for tools that may require user confirmation.
    /// Skips tools that already declare the property (e.g. run_powershell).
    /// </summary>
    internal static JsonElement EnrichSchemaWithExplanation(string toolName, JsonElement schema)
    {
        if (!ConfirmableTools.Contains(toolName))
        {
            return schema;
        }

        try
        {
            var node = JsonNode.Parse(schema.GetRawText());
            if (node is not JsonObject root)
            {
                return schema;
            }

            var properties = root["properties"] as JsonObject ?? new JsonObject();
            if (properties.ContainsKey("explanation"))
            {
                return schema;
            }

            properties["explanation"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] =
                    "When this call may change the system (user confirmation dialog), " +
                    "pass 1–2 short sentences in Russian: what the action does and what changes on the PC. " +
                    "Do not paste the raw command."
            };
            root["properties"] = properties;
            return JsonSchema.Parse(root.ToJsonString());
        }
        catch
        {
            return schema;
        }
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