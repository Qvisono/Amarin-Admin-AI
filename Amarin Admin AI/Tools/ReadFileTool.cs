using System.Text.Json;

namespace Amarin.Tools;

public sealed class ReadFileTool : ITool
{
    private readonly FileSystemTool _filesystem = new();

    public string Name => "read_file";

    public string Description =>
        "Read a UTF-8 text file, or list a directory if path points to a folder. " +
        "Use for inspecting local files the user mentioned.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "File or directory path"
            }
          },
          "required": ["path"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("path", out var pathProp) ||
            string.IsNullOrWhiteSpace(pathProp.GetString()))
        {
            return Task.FromResult(ToolResult.Fail("Missing required parameter: path"));
        }

        var rawPath = pathProp.GetString();
        if (!PathResolver.TryResolve(rawPath, out var resolved, out var error))
        {
            return Task.FromResult(ToolResult.Fail(error!));
        }

        var action = Directory.Exists(resolved) ? "list" : "read";
        var wrapped = JsonSerializer.SerializeToElement(new { action, path = resolved });
        return _filesystem.ExecuteAsync(wrapped, cancellationToken);
    }
}
