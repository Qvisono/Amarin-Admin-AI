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

    public async Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("path", out var pathProp) ||
            string.IsNullOrWhiteSpace(pathProp.GetString()))
        {
            return ToolResult.Fail("Missing required parameter: path");
        }

        var rawPath = pathProp.GetString()!;
        if (!PathResolver.TryResolve(rawPath, out var resolved, out var error))
        {
            return ToolResult.Fail(error!);
        }

        var action = Directory.Exists(resolved) ? "list" : "read";
        var wrapped = JsonSerializer.SerializeToElement(new { action, path = resolved });
        var result = await _filesystem.ExecuteAsync(wrapped, cancellationToken).ConfigureAwait(false);
        return result.Success ? result : Explain(result, rawPath, resolved);
    }

    /// <summary>
    /// Дописывает к отказу причину, по которой путь оказался не тем, что имела в виду модель.
    /// </summary>
    /// <remarks>
    /// Относительный путь раскрывается от рабочей папки процесса, то есть от папки с программой.
    /// Модель, попросив «Вопросы к зачёту.pdf», получала «File not found: …\bin\…\Вопросы к
    /// зачёту.pdf», делала вывод, что доступа к файлам нет, и сообщала об этом человеку. Отказ
    /// инструмента модель читает, поэтому он должен говорить, что делать дальше.
    /// </remarks>
    private static ToolResult Explain(ToolResult result, string rawPath, string resolved)
    {
        if (Path.IsPathRooted(rawPath.Trim()))
        {
            return result;
        }

        return ToolResult.Fail(
            $"{result.Output}\n" +
            $"'{rawPath}' is a relative path, so it was resolved against the program's own " +
            $"folder ({Environment.CurrentDirectory}), which is almost certainly not where the " +
            "user meant. Pass an absolute path instead. If this was a file attached to the " +
            "message, do not read it at all - its contents are already in the conversation.");
    }
}
