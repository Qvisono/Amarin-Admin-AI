using System.Text.Json;

namespace Amarin.Tools;

public sealed class WebSearchTool : ITool
{
    private readonly Func<string, CancellationToken, Task<string>> _search;

    public WebSearchTool(Func<string, CancellationToken, Task<string>> search) => _search = search;

    public string Name => "search_web";
    public string Description =>
        "Search the web for anything: Windows error codes, KB articles and documentation, but " +
        "also pictures, art, wallpapers, news, facts. The answer always ends with a list of the " +
        "source URLs, so this is how you find an address to hand to fetch_image when the user " +
        "asks you to find a picture rather than draw one.";

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