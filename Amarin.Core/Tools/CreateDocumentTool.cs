using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Markdig;

namespace Amarin.Tools;

/// <summary>
/// Новый документ: Word, Excel, PDF, HTML или текстовый файл — из Markdown, который модель пишет
/// лучше всего, или из таблиц.
/// </summary>
/// <remarks>
/// Формат задаёт расширение пути. Word и PDF собираются из Markdown со стилями, списками,
/// таблицами и картинками; Excel — из листов (<c>sheets</c>), таблиц Markdown или строк CSV, с
/// числами числами и датами датами. Существующий файл не перезаписывается без <c>overwrite</c>:
/// перезапись спрашивается у человека, а новый файл в «Загрузках» и на «Рабочем столе» —
/// создаётся без вопроса.
/// </remarks>
public sealed class CreateDocumentTool : ITool
{
    private static readonly MarkdownPipeline HtmlPipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseGridTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseListExtras()
        .UseAutoLinks()
        .Build();

    private readonly FileToolState? _state;

    public CreateDocumentTool() : this(null)
    {
    }

    internal CreateDocumentTool(FileToolState? state) => _state = state;

    public string Name => "create_document";

    public string Description =>
        "Create a new Word (.docx), Excel (.xlsx), PDF, HTML or text file and get its path back. The extension of " +
        "path picks the format. content is Markdown: headings, paragraphs, bold, italic, lists, tables, code, links, " +
        "\\pagebreak, and pictures as ![caption](amarin-image:handle) or ![caption](C:\\full\\path.png). For .xlsx give " +
        "sheets: [{name, rows: [[...], ...], fill: [{range, value, step}]}] (first row is the header; numbers, dates " +
        "YYYY-MM-DD, 12% and =formulas keep their type), or Markdown tables, or CSV lines in content. fill writes a whole " +
        "range from one formula with relative references, as dragging does in Excel ($ keeps a reference), or a number " +
        "series from value and step - a large or regular table needs no list of every value. Formulas are calculated as " +
        "the file is written. source makes the document from an existing one in a single call - Word, Excel (with formula " +
        "results), PDF, PowerPoint, CSV or text - instead of retyping it. An existing file is kept unless overwrite=true. " +
        "Too big for one call: create it with the first part and add the rest with edit_document append (Word, PDF) " +
        "or append_rows (Excel). A table wider than the page is printed in blocks of columns. " +
        "Relative paths go to the user's Downloads folder.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "New file path; the extension picks the format" },
            "content": { "type": "string", "description": "Markdown (or CSV lines for .xlsx)" },
            "source": { "type": "string", "description": "Make this document from an existing one instead of content: a path or amarin-attachment: handle" },
            "sheet": { "type": "string", "description": "With an Excel source: only this sheet" },
            "sheets": {
              "type": "array",
              "description": "Excel only: sheets with rows of values",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "rows": { "type": "array", "items": { "type": "array", "items": { "type": "string" } } },
                  "header": { "type": "boolean", "description": "First row is a header (default true)" },
                  "fill": {
                    "type": "array",
                    "description": "Ranges filled after rows: a formula with relative references, or a number series",
                    "items": {
                      "type": "object",
                      "properties": {
                        "range": { "type": "string", "description": "Such as B2:K11" },
                        "value": { "type": "string", "description": "=formula, a number to start a series, or text" },
                        "step": { "type": "number", "description": "Step of a number series" }
                      },
                      "required": ["range", "value"]
                    }
                  }
                }
              }
            },
            "overwrite": { "type": "boolean", "description": "Replace the file if it exists (default false)" }
          },
          "required": ["path"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        CreateAsync(arguments, cancellationToken);

    private async Task<ToolResult> CreateAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
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
            if (File.Exists(path) && FileToolPaths.Flag(arguments, "overwrite") && _state?.CheckFresh(session, path) is { } stale)
            {
                return ToolResult.Fail(stale);
            }

            var aiDocument = _state?.IsAiDocument(path) == true;
            var result = await Task.Run(() => Create(path, arguments), cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                _state?.NoteWritten(session, path, aiDocument);
            }

            return result;
        }
    }

    private static ToolResult Create(string path, JsonElement arguments)
    {
        // Шлюз, пропустивший запись молча, ставит «только новый файл» — перезаписи тогда нет,
        // даже если модель попросила.
        var createNew = FileToolPaths.Flag(arguments, SafeZone.CreateNewFlag);
        var overwrite = !createNew && FileToolPaths.Flag(arguments, "overwrite");
        if (File.Exists(path) && !overwrite)
        {
            return ToolResult.Fail($"{path} already exists. Pick another name, or set overwrite=true to replace it.");
        }

        if (Directory.Exists(path))
        {
            return ToolResult.Fail($"{path} is a folder. Give a file name with an extension.");
        }

        var content = FileToolPaths.String(arguments, "content") ?? "";
        try
        {
            if (FileToolPaths.String(arguments, "source") is { Length: > 0 } raw)
            {
                content = FromSource(raw, path, FileToolPaths.String(arguments, "sheet"));
            }

            var notes = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".docx" => WordWriter.Create(path, content, DocumentImages.Resolve, overwrite).Skipped,
                ".pdf" => PdfWriter.Create(path, content, DocumentImages.Resolve, overwrite).Skipped,
                ".xlsx" => Excel(path, arguments, content, overwrite),
                ".pptx" => throw new DocumentException("PowerPoint presentations are not created here. Offer a Word or PDF document, or a Markdown outline of the slides."),
                ".html" or ".htm" => Text(path, Html(content, Path.GetFileNameWithoutExtension(path)), overwrite),
                _ when DocumentKinds.Of(path) == DocumentKind.Text => Text(path, content, overwrite),
                _ => throw new DocumentException(
                    $"{Path.GetExtension(path)} is not a format this tool makes. It makes .docx, .xlsx, .pdf, .html and text files; pictures are saved with save_image.")
            };

            var report = new StringBuilder($"Created {path} ({FileToolPaths.Size(new FileInfo(path).Length)}).");
            foreach (var note in notes)
            {
                report.Append("\nSkipped: ").Append(note);
            }

            report.Append("\nThe user sees a card for this file under your reply; name the full path in your answer.");
            return ToolResult.WithFile(report.ToString(), FileToolPaths.Card(path));
        }
        catch (DocumentException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
        catch (IOException ex)
        {
            return ToolResult.Fail($"The file could not be written: {ex.Message}. If it is open in another program, ask the user to close it.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResult.Fail($"Windows refused to write there: {ex.Message}. Choose a folder in the user's profile, such as Documents or Downloads.");
        }
    }

    /// <summary>
    /// Содержимое нового документа из существующего — целиком, со значениями формул Excel. Секреты
    /// не читаются и так, куда бы ни вёл путь: перевод в PDF не должен становиться обходом запрета
    /// на чтение.
    /// </summary>
    private static string FromSource(string raw, string target, string? sheet)
    {
        if (!FileToolPaths.TryResolveSource(raw, out var source, out var error))
        {
            throw new DocumentException(error ?? "Invalid source path.");
        }

        if (!source.InMemory)
        {
            if (SensitivePaths.IsSensitive(source.Path, out var secret))
            {
                throw new DocumentException(secret);
            }

            if (!File.Exists(source.Path))
            {
                throw new DocumentException($"Source not found: {source.Path}. Pass the full path of an existing document.");
            }
        }

        if (DocumentKinds.Of(source) == DocumentKind.Excel && DocumentKinds.Of(target) == DocumentKind.Excel)
        {
            throw new DocumentException("The source is already an Excel workbook. Change it with edit_document, or save a copy with edit_document and save_as.");
        }

        return DocumentConvert.ToMarkdown(source, sheet);
    }

    private static List<string> Excel(string path, JsonElement arguments, string content, bool overwrite)
    {
        var sheets = arguments.TryGetProperty("sheets", out var given) && given.ValueKind == JsonValueKind.Array && given.GetArrayLength() > 0
            ? Sheets(given)
            : ExcelWriter.FromMarkdown(content);
        ExcelWriter.Create(path, sheets, overwrite);
        return [];
    }

    internal static List<SheetInput> Sheets(JsonElement sheets)
    {
        var result = new List<SheetInput>();
        var index = 1;
        foreach (var sheet in sheets.EnumerateArray())
        {
            var name = FileToolPaths.String(sheet, "name") ?? "Sheet" + index;
            var header = !sheet.TryGetProperty("header", out var flag) || flag.ValueKind != JsonValueKind.False;
            var fills = new List<CellFill>();
            if (sheet.TryGetProperty("fill", out var fill) && fill.ValueKind == JsonValueKind.Array)
            {
                var step = 0;
                foreach (var item in fill.EnumerateArray())
                {
                    fills.Add(EditDocumentTool.Fill(item, ++step));
                }
            }

            result.Add(new SheetInput(name, Rows(sheet.TryGetProperty("rows", out var rows) ? rows : default), header, fills));
            index++;
        }

        return result;
    }

    /// <summary>Строки значений из JSON: массив массивов; одиночное значение — строка из одной ячейки.</summary>
    internal static List<IReadOnlyList<CellInput>> Rows(JsonElement rows)
    {
        var result = new List<IReadOnlyList<CellInput>>();
        if (rows.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var row in rows.EnumerateArray())
        {
            result.Add(row.ValueKind == JsonValueKind.Array
                ? [.. row.EnumerateArray().Select(ExcelCells.Parse)]
                : [ExcelCells.Parse(row)]);
        }

        return result;
    }

    private static List<string> Text(string path, string content, bool overwrite)
    {
        // CSV — с меткой UTF-8: без неё Excel открывает его в кодировке Windows, и кириллица
        // превращается в кракозябры.
        var bom = Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase);
        var bytes = new UTF8Encoding(bom).GetPreamble().Concat(Encoding.UTF8.GetBytes(content)).ToArray();
        DocumentFiles.WriteAtomically(path, stream => stream.Write(bytes), overwrite);
        return [];
    }

    /// <summary>
    /// Страница из Markdown — или сам HTML, если модель прислала готовую страницу. Сырой HTML внутри
    /// Markdown не пропускается: страницу откроют в браузере, а текст мог прийти из чужого сайта.
    /// </summary>
    private static string Html(string content, string title)
    {
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }

        return $"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{WebUtility.HtmlEncode(title)}</title>
            <style>
            body {"{"} font-family: "Segoe UI", Arial, sans-serif; max-width: 820px; margin: 40px auto; padding: 0 20px; line-height: 1.55; color: #222; {"}"}
            table {"{"} border-collapse: collapse; margin: 12px 0; {"}"}
            th, td {"{"} border: 1px solid #ccc; padding: 6px 10px; text-align: left; {"}"}
            th {"{"} background: #f2f2f2; {"}"}
            code, pre {"{"} font-family: Consolas, monospace; background: #f4f4f4; {"}"}
            pre {"{"} padding: 10px; overflow-x: auto; {"}"}
            blockquote {"{"} border-left: 3px solid #ccc; margin-left: 0; padding-left: 12px; color: #555; {"}"}
            img {"{"} max-width: 100%; {"}"}
            </style>
            </head>
            <body>
            {Markdown.ToHtml(content, HtmlPipeline)}
            </body>
            </html>
            """;
    }
}
