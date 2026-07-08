using System.Text.Json;

namespace Amarin.Tools;

public sealed class WebSearchTool : ITool
{
    private readonly Func<string, CancellationToken, Task<string>> _search;

    public WebSearchTool(Func<string, CancellationToken, Task<string>> search) => _search = search;

    public string Name => "search_web";
    public string Description =>
        "Search the web for Windows error codes, KB articles, known fixes, and documentation. " +
        "Use for unfamiliar or complex problems before making system changes.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Search query"
            }
          },
          "required": ["query"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("query", out var queryProp) ||
            string.IsNullOrWhiteSpace(queryProp.GetString()))
        {
            return ToolResult.Fail("Missing required parameter: query");
        }

        try
        {
            var result = await _search(queryProp.GetString()!, cancellationToken);
            return ToolResult.Ok(result);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Web search error: {ex.Message}");
        }
    }
}