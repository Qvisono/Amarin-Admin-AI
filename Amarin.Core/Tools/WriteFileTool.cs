using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>Текстовый файл целиком: код, заметки, Markdown, CSV, JSON, HTML, SVG, скрипты.</summary>
/// <remarks>
/// Только текст — и это проверяется по расширению. До 1.33.0 модель, которую просили «сделать
/// документ Word», писала этим инструментом Markdown в файл с расширением .docx, и человек получал
/// файл, который Word не открывает. Двоичные форматы теперь собирают <c>create_document</c> и
/// <c>save_image</c>, а этот инструмент на них отвечает, куда идти.
/// </remarks>
public sealed class WriteFileTool : ITool
{
    private readonly FileSystemTool _filesystem = new();
    private readonly FileToolState? _state;

    public WriteFileTool() : this(null)
    {
    }

    internal WriteFileTool(FileToolState? state) => _state = state;

    public string Name => "write_file";

    public string Description =>
        "Create or overwrite a plain-text file: code, notes, Markdown, CSV, JSON, HTML, SVG, scripts. Folders on the way " +
        "are created. Word, Excel and PDF are made with create_document, pictures saved with save_image; to change part " +
        "of an existing file use edit_file instead of rewriting it. Relative paths go to the user's Downloads folder.";

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

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (FileToolPaths.String(arguments, "path") is not { } raw || string.IsNullOrWhiteSpace(raw))
        {
            return ToolResult.Fail("Missing required parameter: path");
        }

        if (!arguments.TryGetProperty("content", out var contentProp) ||
            contentProp.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return ToolResult.Fail("Missing required parameter: content");
        }

        if (!FileToolPaths.TryResolve(raw, out var path, out var error))
        {
            return ToolResult.Fail(error ?? "Invalid path.");
        }

        if (BinaryRefusal(path) is { } refusal)
        {
            return ToolResult.Fail(refusal);
        }

        var session = AgentRunScope.Current?.SessionId;
        using (await FileToolPaths.LockAsync(_state, path, cancellationToken).ConfigureAwait(false))
        {
            // Перезапись — как правка: только того, что модель в этом чате видела таким, каков он сейчас.
            if (File.Exists(path) && _state?.CheckFresh(session, path) is { } stale)
            {
                return ToolResult.Fail(stale + " To change part of it, use edit_file.");
            }

            var result = await WriteAsync(arguments, path, contentProp, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                _state?.NoteWritten(session, path);
            }

            return result;
        }
    }

    private Task<ToolResult> WriteAsync(JsonElement arguments, string path, JsonElement contentProp, CancellationToken cancellationToken)
    {

        // Условие шлюза «только если файла нет» идёт дальше вместе с записью, иначе молчаливая
        // запись нового файла в «Загрузки» стала бы молчаливой перезаписью.
        var createNew = arguments.TryGetProperty(SafeZone.CreateNewFlag, out var createNewProp) &&
                        createNewProp.ValueKind == JsonValueKind.True;
        var content = contentProp.ValueKind == JsonValueKind.String
            ? contentProp.GetString()
            : contentProp.ToString();
        var wrapped = createNew
            ? JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["action"] = "write",
                ["path"] = path,
                ["content"] = content,
                [SafeZone.CreateNewFlag] = true
            })
            : JsonSerializer.SerializeToElement(new
            {
                action = "write",
                path,
                content
            });
        return _filesystem.ExecuteAsync(wrapped, cancellationToken);
    }

    /// <summary>Отказ для двоичного формата: чем его делать вместо этого. Null — текст, пишем.</summary>
    internal static string? BinaryRefusal(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return DocumentKinds.Of(path) switch
        {
            DocumentKind.Word or DocumentKind.Excel or DocumentKind.Pdf =>
                $"write_file writes plain text only, and a {extension} file is not text. Make it with create_document (Markdown content becomes a real Word or PDF document, tables become Excel sheets); to change an existing one use edit_document.",
            DocumentKind.Slides =>
                $"write_file writes plain text only, and {extension} is a PowerPoint file. Presentations are not created here; offer a Word or PDF document instead.",
            DocumentKind.Image =>
                $"write_file writes plain text only, and {extension} is a picture. Draw it with generate_image and save it with save_image; an SVG picture is text and can be written here.",
            DocumentKind.Binary =>
                $"write_file writes plain text only, and {extension} is a binary format. Do not retry; tell the user what you can make instead.",
            _ => null
        };
    }
}
