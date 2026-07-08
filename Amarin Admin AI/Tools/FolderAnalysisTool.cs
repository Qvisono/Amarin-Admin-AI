using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class FolderAnalysisTool : ITool
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf",
        ".csv", ".tsv", ".ps1", ".bat", ".cmd", ".reg", ".cs", ".cpp", ".h", ".js", ".ts",
        ".html", ".htm", ".css", ".sql", ".env", ".properties"
    };

    public string Name => "analyze_folder";
    public string Description =>
        "Analyze a folder on the user's system: list contents, read text files, and attach images for visual review. " +
        "Use when the user asks what is inside a directory or wants files/photos reviewed.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Folder path to analyze"
            },
            "max_depth": {
              "type": "integer",
              "description": "How deep to recurse when listing (default 2, max 4)"
            },
            "max_images": {
              "type": "integer",
              "description": "Maximum images to attach for vision (default 5, max 8)"
            },
            "max_text_files": {
              "type": "integer",
              "description": "Maximum text files to read (default 8, max 15)"
            }
          },
          "required": ["path"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!arguments.TryGetProperty("path", out var pathProp) ||
                string.IsNullOrWhiteSpace(pathProp.GetString()))
            {
                return Task.FromResult(ToolResult.Fail("Missing required parameter: path"));
            }

            var path = Path.GetFullPath(pathProp.GetString()!);
            if (!Directory.Exists(path))
            {
                return Task.FromResult(ToolResult.Fail($"Папка не найдена: {path}"));
            }

            var maxDepth = GetInt(arguments, "max_depth", 2, 1, 4);
            var maxImages = GetInt(arguments, "max_images", 5, 1, 8);
            var maxTextFiles = GetInt(arguments, "max_text_files", 8, 1, 15);

            var sb = new StringBuilder();
            sb.AppendLine($"Папка: {path}");
            sb.AppendLine();

            var images = new List<ImageAttachment>();
            var textFilesRead = 0;
            var entriesListed = 0;

            sb.AppendLine("Содержимое:");
            WalkDirectory(
                path,
                path,
                depth: 0,
                maxDepth,
                sb,
                ref entriesListed,
                images,
                maxImages,
                ref textFilesRead,
                maxTextFiles);

            if (images.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Прикреплено изображений для просмотра: {images.Count}");
            }

            if (textFilesRead == 0 && images.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine("Текстовых файлов и изображений для просмотра не найдено.");
            }

            var output = Truncate(sb.ToString().TrimEnd(), 24_000);
            return images.Count > 0
                ? Task.FromResult(ToolResult.WithImages(output, images))
                : Task.FromResult(ToolResult.Ok(output));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Folder analysis error: {ex.Message}"));
        }
    }

    private static void WalkDirectory(
        string root,
        string current,
        int depth,
        int maxDepth,
        StringBuilder sb,
        ref int entriesListed,
        List<ImageAttachment> images,
        int maxImages,
        ref int textFilesRead,
        int maxTextFiles)
    {
        if (depth > maxDepth || entriesListed > 400)
        {
            return;
        }

        var indent = new string(' ', depth * 2);

        IEnumerable<string> dirs;
        IEnumerable<string> files;
        try
        {
            dirs = Directory.EnumerateDirectories(current).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
            files = Directory.EnumerateFiles(current).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException)
        {
            sb.AppendLine($"{indent}[нет доступа] {current}");
            return;
        }

        foreach (var dir in dirs)
        {
            if (entriesListed++ > 400)
            {
                sb.AppendLine($"{indent}…");
                return;
            }

            var name = Path.GetFileName(dir);
            sb.AppendLine($"{indent}[DIR]  {name}\\");

            if (depth < maxDepth)
            {
                WalkDirectory(root, dir, depth + 1, maxDepth, sb, ref entriesListed, images, maxImages,
                    ref textFilesRead, maxTextFiles);
            }
        }

        foreach (var file in files)
        {
            if (entriesListed++ > 400)
            {
                sb.AppendLine($"{indent}…");
                return;
            }

            var info = new FileInfo(file);
            var name = Path.GetFileName(file);
            sb.AppendLine($"{indent}[FILE] {name} ({FormatSize(info.Length)})");

            if (images.Count < maxImages && ImageHelpers.IsImageFile(file))
            {
                var image = ImageHelpers.FromFile(file);
                if (image is not null)
                {
                    images.Add(image);
                }
            }

            if (textFilesRead < maxTextFiles && IsTextFile(file) && info.Length <= 64_000)
            {
                try
                {
                    var content = File.ReadAllText(file);
                    sb.AppendLine();
                    sb.AppendLine($"--- {Path.GetRelativePath(root, file)} ---");
                    sb.AppendLine(Truncate(content, 4_000));
                    textFilesRead++;
                }
                catch
                {
                    sb.AppendLine($"{indent}  (не удалось прочитать)");
                }
            }
        }
    }

    private static bool IsTextFile(string path) => TextExtensions.Contains(Path.GetExtension(path));

    private static int GetInt(JsonElement args, string name, int defaultValue, int min, int max)
    {
        if (!args.TryGetProperty(name, out var prop) || !prop.TryGetInt32(out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "\n… [обрезано]";
}