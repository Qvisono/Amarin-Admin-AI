using System.Text.Json;

namespace Amarin.Tools;

/// <summary>Создаёт папку со всеми промежуточными — и отдаёт модели её полный путь.</summary>
public sealed class CreateFolderTool : ITool
{
    public string Name => "create_folder";

    public string Description =>
        "Create a folder, with any missing parent folders, and get its full path back. Relative paths go to the " +
        "user's Downloads folder.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Folder to create" }
          },
          "required": ["path"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!FileToolPaths.TryResolve(FileToolPaths.String(arguments, "path"), out var path, out var error))
        {
            return Task.FromResult(ToolResult.Fail(error ?? "Invalid path."));
        }

        if (File.Exists(path))
        {
            return Task.FromResult(ToolResult.Fail($"{path} is a file, not a folder. Pick another name."));
        }

        if (SensitivePaths.IsProgramData(path))
        {
            return Task.FromResult(ToolResult.Fail(SensitivePaths.ProgramDataRefusal(path)));
        }

        try
        {
            var existed = Directory.Exists(path);
            Directory.CreateDirectory(path);

            // Карточка папки под ответом: человек открывает её щелчком, а не ищет путь в тексте.
            return Task.FromResult(ToolResult.WithFile(
                existed ? $"The folder already exists: {path}" : $"Created folder {path}",
                new SavedFile(path, Path.GetFileName(path.TrimEnd('\\', '/')), 0)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Task.FromResult(ToolResult.Fail($"The folder could not be created: {ex.Message}"));
        }
    }
}
