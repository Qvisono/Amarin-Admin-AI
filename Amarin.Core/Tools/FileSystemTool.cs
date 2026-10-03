using System.Text;
using System.Text.Json;

namespace Amarin.Tools;

public sealed class FileSystemTool : ITool
{
    public string Name => "filesystem";
    public string Description =>
        "Read, write, list, copy, move, create files and directories, or search a folder tree for files by name " +
        "mask, size and modification date (search). Deleting existing files is forbidden.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["read", "write", "list", "exists", "copy", "move", "mkdir", "search"],
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
            },
            "pattern": {
              "type": "string",
              "description": "search: file name mask such as *.log or report*.docx (default *)"
            },
            "min_size": { "type": "integer", "description": "search: minimum size in bytes" },
            "max_size": { "type": "integer", "description": "search: maximum size in bytes" },
            "modified_after": { "type": "string", "description": "search: date, e.g. 2026-09-01" },
            "modified_before": { "type": "string", "description": "search: date, e.g. 2026-09-30" },
            "max_results": { "type": "integer", "description": "search: 1-2000 (default 200)" },
            "max_depth": { "type": "integer", "description": "search: folder depth 0-32 (default 8)" }
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

            var action = actionProp.GetString()?.Trim().ToLowerInvariant();

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

            // Чтение, список и копирование секрета запрещены, куда бы ни вёл путь. Проверка
            // сидит здесь, а не только в шлюзе: read_file и анализ папок зовут этот инструмент
            // напрямую.
            if (action is "read" or "list" or "copy" or "move" or "search" &&
                SensitivePaths.IsSensitive(path, out var secret))
            {
                return Task.FromResult(ToolResult.Fail(secret));
            }

            // Второй рубеж после шлюза: в данные самой программы инструменты не пишут.
            if (action is "write" or "copy" or "move" or "mkdir" &&
                (action is "write" or "mkdir" ? path : destination) is { } target &&
                SensitivePaths.IsProgramData(target))
            {
                return Task.FromResult(ToolResult.Fail(SensitivePaths.ProgramDataRefusal(target)));
            }

            if (action == "move" && SensitivePaths.IsProgramData(path))
            {
                return Task.FromResult(ToolResult.Fail(SensitivePaths.ProgramDataRefusal(path)));
            }

            // Шлюз ставит это поле, когда пропустил запись без вопроса: новый файл в «Загрузках»
            // или на «Рабочем столе». Проверяет «нового» сама запись, а не шлюз заранее: два
            // параллельных вызова иначе оба увидели бы «файла нет».
            var createNew = arguments.TryGetProperty(SafeZone.CreateNewFlag, out var createNewProp) &&
                            createNewProp.ValueKind == JsonValueKind.True;

            if (action == "search")
            {
                return Task.Run(() => Search(path, arguments, cancellationToken), cancellationToken);
            }

            return action switch
            {
                "read" => Task.FromResult(ReadFile(path)),
                "write" => Task.FromResult(WriteFile(path, arguments, createNew)),
                "list" => Task.FromResult(ListDirectory(path)),
                "exists" => Task.FromResult(CheckExists(path)),
                "delete" => Task.FromResult(ToolResult.Fail(DeletionGuard.FileDeletionBlockedMessage)),
                "copy" => Task.FromResult(CopyPath(path, destination, overwrite: !createNew)),
                "move" => Task.FromResult(MovePath(path, destination, overwrite: !createNew)),
                "mkdir" => Task.FromResult(CreateDirectory(path)),
                _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Filesystem error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Молча шлюз пропускает только новый файл. Цель уже есть — значит, это перезапись, а о ней
    /// спрашивают: модель повторит вызов, и шлюз задаст вопрос.
    /// </summary>
    private static ToolResult AlreadyExists(string path) =>
        ToolResult.Fail(Amarin.Core.Loc.Format("S.Gate.AlreadyExists", path));

    private static ToolResult Search(string root, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return ToolResult.Fail($"Directory not found: {root}");
        }

        var pattern = arguments.TryGetProperty("pattern", out var patternProp) && patternProp.ValueKind == JsonValueKind.String &&
                      !string.IsNullOrWhiteSpace(patternProp.GetString())
            ? patternProp.GetString()!.Trim()
            : "*";
        if (pattern.IndexOfAny(Path.GetInvalidFileNameChars().Where(ch => ch is not ('*' or '?')).ToArray()) >= 0)
        {
            return ToolResult.Fail("pattern is a file name mask (* and ? allowed), not a path. Do not retry with the same value.");
        }

        DateTime? after = null;
        DateTime? before = null;
        if (StringOf(arguments, "modified_after") is { } afterText)
        {
            if (!FileSearch.TryDate(afterText, out var date))
            {
                return ToolResult.Fail("modified_after is not a date (use 2026-09-01).");
            }

            after = date;
        }

        if (StringOf(arguments, "modified_before") is { } beforeText)
        {
            if (!FileSearch.TryDate(beforeText, out var date))
            {
                return ToolResult.Fail("modified_before is not a date (use 2026-09-30).");
            }

            before = date;
        }

        var limit = (int)Math.Clamp(IntOf(arguments, "max_results") ?? FileSearch.DefaultResults, 1, FileSearch.MaxResults);
        var query = new FileSearchQuery(
            root,
            pattern,
            IntOf(arguments, "min_size"),
            IntOf(arguments, "max_size"),
            after,
            before,
            limit,
            (int)Math.Clamp(IntOf(arguments, "max_depth") ?? FileSearch.DefaultDepth, 0, FileSearch.MaxDepthLimit));
        var (found, truncated) = FileSearch.Run(query, cancellationToken);
        return ToolResult.Ok(FileSearch.Format(found, truncated, limit));

        static string? StringOf(JsonElement args, string name) =>
            args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;

        static long? IntOf(JsonElement args, string name) =>
            args.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;
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

    private static ToolResult WriteFile(string path, JsonElement arguments, bool createNew)
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

        var text = contentProp.GetString() ?? string.Empty;
        if (createNew)
        {
            // Тот же UTF-8 без BOM, что у File.WriteAllText, но только если файла ещё нет.
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(text);
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path))
            {
                return AlreadyExists(path);
            }
        }
        else
        {
            File.WriteAllText(path, text);
        }

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
            sb.AppendLine($"[DIR]  {Path.GetFileName(dir)} - {dir}");
        }

        foreach (var file in Directory.EnumerateFiles(path).Take(200))
        {
            var info = new FileInfo(file);
            sb.AppendLine($"[FILE] {Path.GetFileName(file)} - {file} ({info.Length} bytes)");
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

        if (!overwrite && (File.Exists(destination) || Directory.Exists(destination)))
        {
            return AlreadyExists(destination);
        }

        if (File.Exists(path))
        {
            try
            {
                File.Copy(path, destination, overwrite);
            }
            catch (IOException) when (!overwrite && File.Exists(destination))
            {
                return AlreadyExists(destination);
            }

            return ToolResult.WithFile($"Copied file to {destination}", Describe(destination));
        }

        if (Directory.Exists(path))
        {
            CopyDirectory(path, destination, overwrite);
            return ToolResult.Ok($"Copied directory to {destination}");
        }

        return ToolResult.Fail($"Source not found: {path}");
    }

    private static ToolResult MovePath(string path, string? destination, bool overwrite)
    {
        if (destination is null)
        {
            return ToolResult.Fail("move requires destination");
        }

        if (!overwrite && (File.Exists(destination) || Directory.Exists(destination)))
        {
            return AlreadyExists(destination);
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            if (Directory.Exists(path))
            {
                Directory.Move(path, destination);
                return ToolResult.Ok($"Moved to {destination}");
            }

            try
            {
                File.Move(path, destination, overwrite);
            }
            catch (IOException) when (!overwrite && File.Exists(destination))
            {
                return AlreadyExists(destination);
            }

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
            // Копия папки профиля браузера унесла бы пароли туда, где их уже можно прочитать.
            if (SensitivePaths.IsSensitive(file, out _))
            {
                continue;
            }

            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }
}