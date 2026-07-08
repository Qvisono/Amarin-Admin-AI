using System.Text.Json;

namespace Amarin.Tools;

public sealed class ScrapeUrlTool : ITool
{
    private readonly Func<string, CancellationToken, Task<string>> _scrape;

    public ScrapeUrlTool(Func<string, CancellationToken, Task<string>> scrape) => _scrape = scrape;

    public string Name => "scrape_url";
    public string Description =>
        "Scrape a public web page and return its content as markdown (~$0.01/request). " +
        "Use for any URL the user asks about. For YouTube or sites that need a browser window, " +
        "also use run_powershell: Start-Process 'url'. Does not work on X/Twitter or Reddit.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "url": {
              "type": "string",
              "description": "Public URL to scrape"
            }
          },
          "required": ["url"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("url", out var urlProp) ||
            string.IsNullOrWhiteSpace(urlProp.GetString()))
        {
            return ToolResult.Fail("Missing required parameter: url");
        }

        var url = urlProp.GetString()!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return ToolResult.Fail("Invalid URL. Use http or https.");
        }

        try
        {
            var content = await _scrape(url, cancellationToken);
            return ToolResult.Ok(content);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Scrape error: {ex.Message}");
        }
    }
}