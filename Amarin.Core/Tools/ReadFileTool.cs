using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Чтение файла моделью чата: текст и код — строками с номерами, Word, Excel, PowerPoint и PDF —
/// нумерованными абзацами, листами, слайдами и страницами, картинка — показом, папка — списком.
/// </summary>
/// <remarks>
/// <para>
/// Прежде инструмент читал только UTF-8 целиком и до 512 КБ: .docx приходил модели мусором из
/// zip-архива, а длинный лог — отказом. Теперь длинное читается частями (<c>offset</c>/<c>limit</c>),
/// и каждая часть говорит, откуда читать дальше.
/// </para>
/// <para>
/// Номера — те же, что принимают <c>edit_file</c> и <c>edit_document</c>: модель ссылается на
/// абзац 12 или строку листа 40, не переписывая документ своими словами.
/// </para>
/// </remarks>
public sealed class ReadFileTool : ITool
{
    /// <summary>Сколько строк списка папки показывать: дальше — поиск агентом.</summary>
    private const int ListLimit = 300;

    private readonly FileToolState? _state;

    public ReadFileTool() : this(null)
    {
    }

    internal ReadFileTool(FileToolState? state) => _state = state;

    public string Name => "read_file";

    public string Description =>
        "Read a file or list a folder. Text and code come with line numbers. Word, Excel, PowerPoint and PDF are " +
        "read natively: numbered paragraphs, sheet rows with cell addresses, slides and pages - the same numbers " +
        "edit_file and edit_document use. Pictures are shown to you. Long files come in parts: continue with offset. " +
        "Relative paths are looked up in the user's Downloads folder.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File or folder path; absolute is best" },
            "offset": { "type": "integer", "description": "First line / paragraph / page / slide / sheet row to show (1-based)" },
            "limit": { "type": "integer", "description": "How many of them to show at most" },
            "sheet": { "type": "string", "description": "Excel: sheet name" },
            "range": { "type": "string", "description": "Excel: cell range such as A1:F50" },
            "part": { "type": "integer", "description": "Next piece of one very long line, paragraph, table or page (2, 3, ...), as the previous read suggested" }
          },
          "required": ["path"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(arguments), cancellationToken);

    private ToolResult Read(JsonElement arguments)
    {
        if (FileToolPaths.String(arguments, "path") is not { Length: > 0 } raw)
        {
            return ToolResult.Fail("Missing required parameter: path");
        }

        if (!FileToolPaths.TryResolveSource(raw, out var source, out var error))
        {
            return ToolResult.Fail(error ?? "Invalid path.");
        }

        var path = source.Path;
        if (!source.InMemory)
        {
            // Секреты не читаются, куда бы ни вёл путь, — тот же запрет, что у файловых инструментов агента.
            if (SensitivePaths.IsSensitive(path, out var secret))
            {
                return ToolResult.Fail(secret);
            }

            if (Directory.Exists(path))
            {
                return List(path);
            }

            if (!File.Exists(path))
            {
                return ToolResult.Fail(
                    $"File not found: {path}. Pass the full path; a bare name is looked up in Downloads ({DownloadPaths.DownloadsDirectory}). " +
                    "A file attached to the message is read by the path named in its <document> block.");
            }

            if (DocumentKinds.Of(path) == DocumentKind.Image)
            {
                return Picture(path);
            }
        }

        var window = new DocumentWindow(
            Math.Max(1, FileToolPaths.Int(arguments, "offset") ?? 1),
            FileToolPaths.Int(arguments, "limit") is { } limit and > 0 ? limit : null,
            FileToolPaths.String(arguments, "sheet"),
            FileToolPaths.String(arguments, "range"),
            Part: Math.Max(1, FileToolPaths.Int(arguments, "part") ?? 1));
        try
        {
            var text = DocumentReader.Format(path, DocumentReader.Read(source, window), window);

            // Отметка — после удачного чтения: правка опирается на то, что модель действительно видела.
            if (!source.InMemory)
            {
                _state?.NoteRead(AgentRunScope.Current?.SessionId, path);
            }

            return ToolResult.Ok(text);
        }
        catch (DocumentException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }

    private static ToolResult Picture(string path)
    {
        var image = ImageHelpers.FromFile(path);
        if (image is null)
        {
            return ToolResult.Fail($"{path} could not be opened as a picture.");
        }

        var size = new FileInfo(path).Length;
        return ToolResult.WithImages($"Picture {path} ({FileToolPaths.Size(size)}) is shown to you below.", [image]);
    }

    private static ToolResult List(string path)
    {
        var text = new StringBuilder();
        var shown = 0;
        var total = 0;
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(path).Order(StringComparer.OrdinalIgnoreCase))
            {
                total++;
                if (shown++ < ListLimit)
                {
                    text.Append("[DIR]  ").Append(Path.GetFileName(folder)).Append('\n');
                }
            }

            foreach (var file in Directory.EnumerateFiles(path).Order(StringComparer.OrdinalIgnoreCase))
            {
                total++;
                if (shown++ < ListLimit)
                {
                    var info = new FileInfo(file);
                    text.Append("[FILE] ").Append(info.Name).Append("  ").Append(FileToolPaths.Size(info.Length))
                        .Append("  ").Append(info.LastWriteTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Fail($"The folder could not be listed: {ex.Message}");
        }

        if (total == 0)
        {
            return ToolResult.Ok($"{path} - empty folder.");
        }

        var header = $"{path} - folder, {total} entries" + (total > ListLimit ? $" (first {ListLimit} shown)" : "") + "\n";
        return ToolResult.Ok(header + text.ToString().TrimEnd());
    }
}
