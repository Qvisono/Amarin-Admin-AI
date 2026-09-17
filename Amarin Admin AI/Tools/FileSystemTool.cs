using System.Text;
using System.Text.Json;

namespace Amarin.Tools;

public sealed class FileSystemTool : ITool
{
    public string Name => "filesystem";
    public string Description =>
        "Read, write, list, copy, move, or create files and directories. Deleting existing files is forbidden.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["read", "write", "list", "exists", "copy", "move", "mkdir"],
              "description": "File system operation"
            },
            "path": {
              "type": "string",
              "description": "Source file or directory path"
            },
            "destination": {
              "type": "string",
              "description": "Destination path for copy/move/write"
            },
            "content": {
              "type": "string",
              "description": "Text content for write action"
            },
            "recursive": {
              "type": "boolean",
              "description": "Recursive delete for directories"
            }
          },
          "required": ["action", "path"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!arguments.TryGetProperty("action", out var actionProp) ||
                !arguments.TryGetProperty("path", out var pathProp))
            {
                return Task.FromResult(ToolResult.Fail("Missing required parameters: action, path"));
            }

            var action = actionProp.GetString()?.ToLowerInvariant();

            if (!PathResolver.TryResolve(pathProp.GetString(), out var path, out var pathError))
            {
                return Task.FromResult(ToolResult.Fail(pathError!));
            }

            arguments.TryGetProperty("destination", out var destProp);
            string? destination = null;
            if (destProp.ValueKind == JsonValueKind.String)
            {
                if (!PathResolver.TryResolve(destProp.GetString(), out var resolvedDestination, out var destError))
                {
                    return Task.FromResult(ToolResult.Fail(destError!));
                }

                destination = resolvedDestination;
            }

            return action switch
            {
                "read" => Task.FromResult(ReadFile(path)),
                "write" => Task.FromResult(WriteFile(path, arguments)),
                "list" => Task.FromResult(ListDirectory(path)),
                "exists" => Task.FromResult(CheckExists(path)),
                "delete" => Task.FromResult(ToolResult.Fail(DeletionGuard.FileDeletionBlockedMessage)),
                "copy" => Task.FromResult(CopyPath(path, destination, overwrite: true)),
                "move" => Task.FromResult(MovePath(path, destination)),
                "mkdir" => Task.FromResult(CreateDirectory(path)),
                _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Filesystem error: {ex.Message}"));
        }
    }

    private static ToolResult ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return ToolResult.Fail($"File not found: {path}");
        }

        var info = new FileInfo(path);
        if (info.Length > 512_000)
        {
            return ToolResult.Fail($"File too large ({info.Length} bytes). Max 512 KB for read.");
        }

        var content = File.ReadAllText(path);
        return ToolResult.Ok(content);
    }

    private static ToolResult WriteFile(string path, JsonElement arguments)
    {
        if (!arguments.TryGetProperty("content", out var contentProp))
        {
            return ToolResult.Fail("Write requires content parameter");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, contentProp.GetString() ?? string.Empty);
        return ToolResult.WithFile($"Written {path}", Describe(path));
    }

    private static ToolResult ListDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return ToolResult.Fail($"Directory not found: {path}");
        }

        var sb = new StringBuilder();
        foreach (var dir in Directory.EnumerateDirectories(path).Take(100))
        {
            sb.AppendLine($"[DIR]  {Path.GetFileName(dir)} — {dir}");
        }

        foreach (var file in Directory.EnumerateFiles(path).Take(200))
        {
            var info = new FileInfo(file);
            sb.AppendLine($"[FILE] {Path.GetFileName(file)} — {file} ({info.Length} bytes)");
        }

        return ToolResult.Ok(sb.Length == 0 ? "Empty directory." : sb.ToString().TrimEnd());
    }

    private static ToolResult CheckExists(string path)
    {
        if (File.Exists(path)) return ToolResult.Ok($"Exists: file ({path})");
        if (Directory.Exists(path)) return ToolResult.Ok($"Exists: directory ({path})");
        return ToolResult.Ok($"Does not exist: {path}");
    }

    private static ToolResult CopyPath(string path, string? destination, bool overwrite)
    {
        if (destination is null)
        {
            return ToolResult.Fail("copy requires destination");
        }

        if (File.Exists(path))
        {
            File.Copy(path, destination, overwrite);
            return ToolResult.WithFile($"Copied file to {destination}", Describe(destination));
        }

        if (Directory.Exists(path))
        {
            CopyDirectory(path, destination, overwrite);
            return ToolResult.Ok($"Copied directory to {destination}");
        }

        return ToolResult.Fail($"Source not found: {path}");
    }

    private static ToolResult MovePath(string path, string? destination)
    {
        if (destination is null)
        {
            return ToolResult.Fail("move requires destination");
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            if (Directory.Exists(path))
            {
                Directory.Move(path, destination);
                return ToolResult.Ok($"Moved to {destination}");
            }

            File.Move(path, destination, overwrite: true);
            return ToolResult.WithFile($"Moved to {destination}", Describe(destination));
        }

        return ToolResult.Fail($"Source not found: {path}");
    }

    private static ToolResult CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return ToolResult.Ok($"Created directory: {path}");
    }

    /// <summary>
    /// Карточка файла для ленты чата. Размер читается с диска, а не считается по содержимому:
    /// после записи на диске может оказаться не то же число байт, что в строке (перевод строки,
    /// кодировка), а в карточке человеку показывают именно файл.
    /// </summary>
    private static SavedFile Describe(string path)
    {
        long size = 0;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Размер — украшение карточки, а не результат работы: файл записан, и сообщать об
            // ошибке из-за того, что его не удалось перемерить, было бы враньём.
        }

        return new SavedFile(path, Path.GetFileName(path), size);
    }

    private static void CopyDirectory(string source, string destination, bool overwrite)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }
}