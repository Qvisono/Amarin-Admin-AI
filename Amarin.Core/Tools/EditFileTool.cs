using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Точная замена фрагмента в текстовом файле или в документе Word — правка, а не переписывание.
/// </summary>
/// <remarks>
/// Тот же договор, что у правки в Claude Code: <c>old_string</c> встречается в файле ровно один
/// раз (или <c>replace_all</c>), иначе отказ с объяснением; править можно только прочитанное в этом
/// чате и не изменившееся с тех пор (<see cref="FileToolState"/>). Модель, которая переписывает файл
/// целиком, по дороге теряет куски и тащит старые версии из своей памяти; замена трогает только
/// названное. В ответе — строки вокруг правки с номерами: модель видит, что вышло, не перечитывая.
/// </remarks>
public sealed class EditFileTool : ITool
{
    private readonly FileToolState? _state;

    public EditFileTool() : this(null)
    {
    }

    internal EditFileTool(FileToolState? state) => _state = state;

    public string Name => "edit_file";

    public string Description =>
        "Change an existing text file or Word document by exact replacement: old_string must occur in it exactly " +
        "once (copy it from read_file, without line numbers), and new_string takes its place; replace_all=true " +
        "changes every occurrence. The file must have been read with read_file in this chat and not changed since. " +
        "Prefer this to rewriting the whole file.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File to change" },
            "old_string": { "type": "string", "description": "Exact text to replace" },
            "new_string": { "type": "string", "description": "Text to put in its place" },
            "replace_all": { "type": "boolean", "description": "Replace every occurrence (default false)" }
          },
          "required": ["path", "old_string", "new_string"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (FileToolPaths.IsAttachmentHandle(FileToolPaths.String(arguments, "path")))
        {
            return ToolResult.Fail(FileToolPaths.HandleIsReadOnly);
        }

        if (!FileToolPaths.TryResolve(FileToolPaths.String(arguments, "path"), out var path, out var error))
        {
            return ToolResult.Fail(error ?? "Invalid path.");
        }

        if (SensitivePaths.IsProgramData(path))
        {
            return ToolResult.Fail(SensitivePaths.ProgramDataRefusal(path));
        }

        var session = AgentRunScope.Current?.SessionId;
        using (await FileToolPaths.LockAsync(_state, path, cancellationToken).ConfigureAwait(false))
        {
            if (File.Exists(path) && _state?.CheckFresh(session, path) is { } stale)
            {
                return ToolResult.Fail(stale);
            }

            var result = await Task.Run(() => Edit(path, arguments), cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                _state?.NoteWritten(session, path);
            }

            return result;
        }
    }

    private static ToolResult Edit(string path, JsonElement arguments)
    {
        var oldText = FileToolPaths.String(arguments, "old_string");
        var newText = FileToolPaths.String(arguments, "new_string");
        if (oldText is null || newText is null)
        {
            return ToolResult.Fail("Missing required parameters: old_string, new_string");
        }

        if (!File.Exists(path))
        {
            return ToolResult.Fail($"File not found: {path}. To create a file use write_file or create_document.");
        }

        if (SensitivePaths.IsSensitive(path, out var secret))
        {
            return ToolResult.Fail(secret);
        }

        var all = FileToolPaths.Flag(arguments, "replace_all");
        try
        {
            switch (DocumentKinds.Of(path))
            {
                case DocumentKind.Text:
                    var result = TextEdits.Replace(path, oldText, newText, all);
                    return ToolResult.WithFile(
                        $"Edited {path}: {result.Count} replacement(s). The changed place now reads:\n{result.Snippet}",
                        FileToolPaths.Card(path));
                case DocumentKind.Word:
                    return EditWord(path, oldText, newText, all);
                case DocumentKind.Excel or DocumentKind.Pdf:
                    return ToolResult.Fail("edit_file changes text files and Word documents. For Excel cells and PDF pages use edit_document.");
                default:
                    return ToolResult.Fail(DocumentReader.BinaryRefusal(path));
            }
        }
        catch (DocumentException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
        catch (IOException ex)
        {
            return ToolResult.Fail($"The file could not be written: {ex.Message}. If it is open in another program, ask the user to close it.");
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Fail($"Windows refused to change {path}: it is read-only or protected. Save the result elsewhere with edit_document save_as, or ask the user.");
        }
    }

    private static ToolResult EditWord(string path, string oldText, string newText, bool all)
    {
        var replaced = 0;
        IReadOnlyList<int> units = [];
        DocumentFiles.EditCopy(path, path, overwrite: true, copy =>
        {
            using var editor = WordEditor.Open(copy, _ => null);
            (replaced, units) = editor.Replace(oldText, newText, all);
            editor.Save();
        });

        return ToolResult.WithFile(
            $"Edited {path}: {replaced} replacement(s) in paragraph(s) {string.Join(", ", units)}. Formatting of the changed text is kept.",
            FileToolPaths.Card(path));
    }
}
