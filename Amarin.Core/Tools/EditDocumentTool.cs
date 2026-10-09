using System.Globalization;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Правка Word, Excel и PDF по частям — абзацами по номерам из чтения, ячейками по адресам,
/// страницами по номерам — с сохранением всего, чего правка не касалась.
/// </summary>
/// <remarks>
/// <para>
/// Все операции одного вызова ссылаются на номера, которые модель видела в <c>read_file</c>, и
/// идут в данном порядке. Документ правится в копии и встаёт на место только целиком — сбой на
/// третьей операции не оставляет полуисправленный файл.
/// </para>
/// <para>
/// <c>save_as</c> кладёт результат в новый файл и не трогает исходный: так правят то, что человек
/// прислал, — у него остаётся оригинал, а молча создать новый файл в «Загрузках» можно и без вопроса.
/// </para>
/// </remarks>
public sealed class EditDocumentTool : ITool
{
    private readonly FileToolState? _state;

    public EditDocumentTool() : this(null)
    {
    }

    internal EditDocumentTool(FileToolState? state) => _state = state;

    public string Name => "edit_document";

    public string Description =>
        "Change a Word, Excel or PDF file in parts, keeping everything else as it was. operations run in order and refer " +
        "to the numbers read_file showed. Word (.docx): {op:\"replace\", find, replace, all}, {op:\"insert\", after|before: " +
        "paragraph, content}, {op:\"set\", paragraph, content}, {op:\"delete\", paragraph, to}, {op:\"append\", content} - " +
        "content is Markdown. Excel (.xlsx): {op:\"set_cells\", sheet, cells:[{cell:\"B2\", value:\"12.5\"}, {cell:\"C9\", value:\"=SUM(C2:C8)\"}]}, " +
        "{op:\"append_rows\", sheet, rows}, {op:\"add_sheet\", name, rows}, {op:\"rename_sheet\", sheet, name}, " +
        "{op:\"clear\", sheet, range}. PDF: {op:\"keep_pages\", pages:\"1-3,7\"}, {op:\"append_pdf\", files:[...]}. " +
        "save_as writes the result to a new file and keeps the original.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Document to change" },
            "operations": {
              "type": "array",
              "description": "Changes in order; see the tool description for each op",
              "items": {
                "type": "object",
                "properties": {
                  "op": { "type": "string", "enum": ["replace", "insert", "set", "delete", "append", "set_cells", "append_rows", "add_sheet", "rename_sheet", "clear", "keep_pages", "append_pdf"] },
                  "find": { "type": "string" },
                  "replace": { "type": "string" },
                  "all": { "type": "boolean" },
                  "paragraph": { "type": "integer" },
                  "after": { "type": "integer" },
                  "before": { "type": "integer" },
                  "to": { "type": "integer" },
                  "content": { "type": "string" },
                  "sheet": { "type": "string" },
                  "cells": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "cell": { "type": "string", "description": "Address such as B7" },
                        "value": { "type": "string", "description": "Number, text, TRUE/FALSE, YYYY-MM-DD date or =formula" }
                      },
                      "required": ["cell", "value"]
                    }
                  },
                  "rows": { "type": "array", "items": { "type": "array", "items": { "type": "string" } } },
                  "name": { "type": "string" },
                  "range": { "type": "string" },
                  "pages": { "type": "string" },
                  "files": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["op"]
              }
            },
            "save_as": { "type": "string", "description": "Write the result here and keep the original untouched" },
            "overwrite": { "type": "boolean", "description": "save_as may replace an existing file (default false)" }
          },
          "required": ["path", "operations"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        EditAsync(arguments, cancellationToken);

    private async Task<ToolResult> EditAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (FileToolPaths.IsAttachmentHandle(FileToolPaths.String(arguments, "path")) &&
            FileToolPaths.String(arguments, "save_as") is not { Length: > 0 })
        {
            return ToolResult.Fail(FileToolPaths.HandleIsReadOnly);
        }

        if (!FileToolPaths.TryResolveSource(FileToolPaths.String(arguments, "path"), out var source, out var error))
        {
            return ToolResult.Fail(error ?? "Invalid path.");
        }

        var target = source.Path;
        if (FileToolPaths.String(arguments, "save_as") is { Length: > 0 } saveAs &&
            !FileToolPaths.TryResolve(saveAs, out target, out var saveError))
        {
            return ToolResult.Fail(saveError ?? "Invalid save_as path.");
        }

        // Данные самой программы не правятся и не уносятся копией: там переписки и настройки.
        foreach (var touched in source.InMemory ? [target] : (string[])[source.Path, target])
        {
            if (SensitivePaths.IsProgramData(touched))
            {
                return ToolResult.Fail(SensitivePaths.ProgramDataRefusal(touched));
            }
        }

        var session = AgentRunScope.Current?.SessionId;
        using (await FileToolPaths.LockAsync(_state, target, cancellationToken).ConfigureAwait(false))
        {
            // Номера абзацев, ячеек и страниц — из чтения: правка по устаревшему чтению попала бы не туда.
            if (!source.InMemory && File.Exists(source.Path) && _state?.CheckFresh(session, source.Path) is { } stale)
            {
                return ToolResult.Fail(stale);
            }

            var result = await Task.Run(() => Edit(source, target, arguments), cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                _state?.NoteWritten(session, target);
            }

            return result;
        }
    }

    private static ToolResult Edit(DocumentSource source, string target, JsonElement arguments)
    {
        var path = source.Path;
        if (!source.InMemory && !File.Exists(path))
        {
            return ToolResult.Fail($"File not found: {path}.");
        }

        if (!source.InMemory && SensitivePaths.IsSensitive(path, out var secret))
        {
            return ToolResult.Fail(secret);
        }

        if (!arguments.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() == 0)
        {
            return ToolResult.Fail("Missing operations: give a list such as [{\"op\":\"replace\",\"find\":\"...\",\"replace\":\"...\"}].");
        }

        var overwrite = true;
        if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
        {
            overwrite = !FileToolPaths.Flag(arguments, SafeZone.CreateNewFlag) && FileToolPaths.Flag(arguments, "overwrite");
            if (File.Exists(target) && !overwrite)
            {
                return ToolResult.Fail($"{target} already exists. Pick another save_as name, or set overwrite=true (the user will be asked).");
            }

            if (!string.Equals(Path.GetExtension(target), Path.GetExtension(path), StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("save_as must keep the same extension: this tool changes a document, it does not convert it. Read it and use create_document for another format.");
            }
        }

        var log = new StringBuilder();
        try
        {
            switch (DocumentKinds.Of(source))
            {
                case DocumentKind.Word:
                    DocumentFiles.EditCopy(source, target, overwrite, copy => EditWord(copy, operations, log));
                    break;
                case DocumentKind.Excel:
                    DocumentFiles.EditCopy(source, target, overwrite, copy => EditExcel(copy, operations, log));
                    break;
                case DocumentKind.Pdf:
                    var bytes = EditPdf(source.ReadAllBytes(), operations, log);
                    DocumentFiles.WriteAtomically(target, stream => stream.Write(bytes), overwrite);
                    break;
                case DocumentKind.Text:
                    return ToolResult.Fail("This is a text file: change it with edit_file (exact replacement) or rewrite it with write_file.");
                default:
                    return ToolResult.Fail(DocumentReader.BinaryRefusal(path));
            }
        }
        catch (DocumentException ex)
        {
            return ToolResult.Fail(ex.Message + (log.Length > 0 ? "\nNothing was saved. Done before the failure:\n" + log : "\nNothing was saved."));
        }
        catch (IOException ex)
        {
            return ToolResult.Fail($"The file could not be written: {ex.Message}. If it is open in another program, ask the user to close it.");
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Fail($"Windows refused to write {target}: it is read-only or protected. Use save_as with a folder in the user's profile.");
        }

        log.Append("Saved: ").Append(target);
        if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
        {
            log.Append(" (the original ").Append(path).Append(" is unchanged)");
        }

        return ToolResult.WithFile(log.ToString(), FileToolPaths.Card(target));
    }

    private static void EditWord(string copy, JsonElement operations, StringBuilder log)
    {
        using var editor = WordEditor.Open(copy, DocumentImages.Resolve);
        var step = 0;
        foreach (var operation in operations.EnumerateArray())
        {
            step++;
            var op = Op(operation);
            switch (op)
            {
                case "replace":
                    var (count, units) = editor.Replace(
                        Required(operation, "find", step),
                        FileToolPaths.String(operation, "replace") ?? "",
                        FileToolPaths.Flag(operation, "all"));
                    log.Append(step).Append(". replaced ").Append(count).Append(" in paragraph(s) ").Append(string.Join(", ", units)).Append('\n');
                    break;
                case "insert":
                    var before = FileToolPaths.Int(operation, "before");
                    var after = FileToolPaths.Int(operation, "after");
                    if (before is null && after is null)
                    {
                        throw new DocumentException($"Operation {step} (insert) needs after or before: the paragraph number to insert next to (before: 1 inserts at the start).");
                    }

                    editor.Insert(before ?? after!.Value, before is not null, Required(operation, "content", step));
                    log.Append(step).Append(". inserted ").Append(before is not null ? "before " : "after ").Append(before ?? after).Append('\n');
                    break;
                case "set":
                    var paragraph = FileToolPaths.Int(operation, "paragraph") ?? throw new DocumentException($"Operation {step} (set) needs paragraph.");
                    editor.Set(paragraph, Required(operation, "content", step));
                    log.Append(step).Append(". replaced paragraph ").Append(paragraph).Append('\n');
                    break;
                case "delete":
                    var from = FileToolPaths.Int(operation, "paragraph") ?? throw new DocumentException($"Operation {step} (delete) needs paragraph.");
                    var to = FileToolPaths.Int(operation, "to") ?? from;
                    editor.Delete(from, to);
                    log.Append(step).Append(". deleted paragraph(s) ").Append(from).Append(to != from ? "-" + to.ToString(CultureInfo.InvariantCulture) : "").Append('\n');
                    break;
                case "append":
                    editor.Append(Required(operation, "content", step));
                    log.Append(step).Append(". appended at the end\n");
                    break;
                default:
                    throw new DocumentException($"Operation {step}: \"{op}\" is not a Word operation. Word takes replace, insert, set, delete, append.");
            }
        }

        foreach (var skipped in editor.Skipped)
        {
            log.Append("Skipped: ").Append(skipped).Append('\n');
        }

        editor.Save();
    }

    private static void EditExcel(string copy, JsonElement operations, StringBuilder log)
    {
        using var editor = ExcelEditor.Open(copy);
        var step = 0;
        foreach (var operation in operations.EnumerateArray())
        {
            step++;
            var op = Op(operation);
            var sheet = FileToolPaths.String(operation, "sheet");
            switch (op)
            {
                case "set_cells":
                    var count = editor.SetCells(sheet, Cells(operation, step));
                    log.Append(step).Append(". set ").Append(count).Append(" cell(s)\n");
                    break;
                case "append_rows":
                    var rows = CreateDocumentTool.Rows(operation.TryGetProperty("rows", out var given) ? given : default);
                    var start = editor.AppendRows(sheet, rows);
                    log.Append(step).Append(". appended ").Append(rows.Count).Append(" row(s) from row ").Append(start).Append('\n');
                    break;
                case "add_sheet":
                    var name = Required(operation, "name", step);
                    editor.AddSheet(name, CreateDocumentTool.Rows(operation.TryGetProperty("rows", out var sheetRows) ? sheetRows : default), header: true);
                    log.Append(step).Append(". added sheet \"").Append(name).Append("\"\n");
                    break;
                case "rename_sheet":
                    editor.RenameSheet(sheet ?? throw new DocumentException($"Operation {step} (rename_sheet) needs sheet."), Required(operation, "name", step));
                    log.Append(step).Append(". renamed sheet\n");
                    break;
                case "clear":
                    var cleared = editor.Clear(sheet, Required(operation, "range", step));
                    log.Append(step).Append(". cleared ").Append(cleared).Append(" cell(s)\n");
                    break;
                default:
                    throw new DocumentException($"Operation {step}: \"{op}\" is not an Excel operation. Excel takes set_cells, append_rows, add_sheet, rename_sheet, clear.");
            }
        }

        if (editor.HasFormulas)
        {
            log.Append("Formula results are recalculated when the file is opened in Excel; until then read_file shows the totals saved before this change.\n");
        }

        editor.Save();
    }

    private static byte[] EditPdf(byte[] bytes, JsonElement operations, StringBuilder log)
    {
        var step = 0;
        foreach (var operation in operations.EnumerateArray())
        {
            step++;
            var op = Op(operation);
            switch (op)
            {
                case "keep_pages":
                    bytes = PdfEditor.KeepPages(bytes, Required(operation, "pages", step));
                    log.Append(step).Append(". kept pages, now ").Append(PdfEditor.PageCount(bytes)).Append('\n');
                    break;
                case "append_pdf":
                    var files = operation.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array
                        ? [.. list.EnumerateArray().Select(file => file.ValueKind == JsonValueKind.String ? file.GetString() ?? "" : file.ToString())]
                        : new List<string>();
                    var others = new List<byte[]>();
                    foreach (var file in files)
                    {
                        if (!FileToolPaths.TryResolve(file, out var other, out _) || !File.Exists(other) || DocumentKinds.Of(other) != DocumentKind.Pdf)
                        {
                            throw new DocumentException($"Operation {step} (append_pdf): {file} is not a PDF file that exists.");
                        }

                        others.Add(File.ReadAllBytes(other));
                    }

                    if (others.Count == 0)
                    {
                        throw new DocumentException($"Operation {step} (append_pdf) needs files: the PDFs to add at the end.");
                    }

                    bytes = PdfEditor.Append(bytes, others);
                    log.Append(step).Append(". appended ").Append(others.Count).Append(" file(s), now ").Append(PdfEditor.PageCount(bytes)).Append(" pages\n");
                    break;
                default:
                    throw new DocumentException($"Operation {step}: \"{op}\" is not a PDF operation. PDF takes keep_pages and append_pdf; to change its text, read it and make a new document with create_document.");
            }
        }

        return bytes;
    }

    /// <summary>
    /// Ячейки операции: список <c>[{cell, value}]</c>, как в схеме, или объект <c>{"B2": value}</c>,
    /// которым модели пишут его по привычке, — оба понимаются одинаково.
    /// </summary>
    /// <remarks>
    /// Схема — список, а не объект с произвольными ключами: строгие провайдеры отвергают объект без
    /// перечисленных свойств вместе со всем запросом.
    /// </remarks>
    private static List<(string Address, CellInput Value)> Cells(JsonElement operation, int step)
    {
        if (!operation.TryGetProperty("cells", out var cells))
        {
            throw new DocumentException($"Operation {step} (set_cells) needs cells: [{{\"cell\":\"B2\",\"value\":\"12.5\"}}].");
        }

        return cells.ValueKind switch
        {
            JsonValueKind.Object => [.. cells.EnumerateObject().Select(cell => (cell.Name, ExcelCells.Parse(cell.Value)))],
            JsonValueKind.Array => [.. cells.EnumerateArray().Select(item =>
                (FileToolPaths.String(item, "cell") ?? throw new DocumentException($"Operation {step} (set_cells): every item needs cell, such as \"B2\"."),
                 item.TryGetProperty("value", out var value) ? ExcelCells.Parse(value) : CellInput.Empty))],
            _ => throw new DocumentException($"Operation {step} (set_cells) needs cells: [{{\"cell\":\"B2\",\"value\":\"12.5\"}}].")
        };
    }

    private static string Op(JsonElement operation) =>
        (FileToolPaths.String(operation, "op") ?? "").Trim().ToLowerInvariant();

    private static string Required(JsonElement operation, string name, int step) =>
        FileToolPaths.String(operation, name) is { } value
            ? value
            : throw new DocumentException($"Operation {step} ({Op(operation)}) needs {name}.");
}
