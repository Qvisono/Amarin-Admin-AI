using System.Text.Json;

namespace Amarin.Tools;

public sealed class WriteFileTool : ITool
{
    private readonly FileSystemTool _filesystem = new();

    public string Name => "write_file";

    public string Description =>
        "Write UTF-8 text to a file. Parent directories are created. " +
        "Does not delete files. Not for system administration — the agent handles that.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Destination file path"
            },
            "content": {
              "type": "string",
              "description": "Full text to write"
            }
          },
          "required": ["path", "content"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("path", out var pathProp) ||
            string.IsNullOrWhiteSpace(pathProp.GetString()))
        {
            return Task.FromResult(ToolResult.Fail("Missing required parameter: path"));
        }

        if (!arguments.TryGetProperty("content", out var contentProp) ||
            contentProp.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Task.FromResult(ToolResult.Fail("Missing required parameter: content"));
        }

        var wrapped = JsonSerializer.SerializeToElement(new
        {
            action = "write",
            path = pathProp.GetString(),
            content = contentProp.ValueKind == JsonValueKind.String
                ? contentProp.GetString()
                : contentProp.ToString()
        });
        return _filesystem.ExecuteAsync(wrapped, cancellationToken);
    }
}
