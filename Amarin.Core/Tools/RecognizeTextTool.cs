using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Текст с картинки или со страниц PDF-скана — встроенным OCR Windows (<see cref="LocalOcr"/>), на
/// этом ПК и без сети.
/// </summary>
/// <remarks>
/// Только чтение. Картинка из чата (<c>amarin-image:</c>) и вложение (<c>amarin-attachment:</c>)
/// живут в памяти, а OCR Windows читает файл, поэтому они кладутся во временный файл на время
/// распознавания и сразу удаляются.
/// </remarks>
public sealed class RecognizeTextTool : ITool
{
    public string Name => "recognize_text";

    public string Description =>
        "Recognize the text in a picture or in the pages of a scanned PDF with the text recognition built into Windows, " +
        "on this computer. Use it for a scan without a text layer, for a picture you cannot see, or when the exact wording " +
        "of a picture matters. pages picks PDF pages as numbers and N-M ranges, comma-separated (default: the first " +
        "pages, at most a few dozen per call). language is a language tag; without it the languages of the user's Windows " +
        "profile are used. Relative paths go to the user's Downloads folder.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Picture or PDF file, an amarin-image: handle or an amarin-attachment: handle" },
            "pages": { "type": "string", "description": "PDF pages: numbers and N-M ranges, comma-separated" },
            "language": { "type": "string", "description": "Language tag of the text; omit to use the Windows profile languages" }
          },
          "required": ["path"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (FileToolPaths.String(arguments, "path") is not { Length: > 0 } raw)
        {
            return ToolResult.Fail("Missing required parameter: path");
        }

        string? temporary = null;
        try
        {
            string path;
            byte[]? bytes = null;
            if (ChatImageRegistry.IsHandle(raw.Trim()))
            {
                bytes = DocumentImages.Resolve(raw.Trim()) ?? throw new DocumentException($"{raw} is not a picture of an open chat any more.");
                path = temporary = Temporary(bytes, ImageFormats.Sniff(bytes)?.MimeType == "image/jpeg" ? ".jpg" : ".png");
            }
            else
            {
                if (!FileToolPaths.TryResolveSource(raw, out var source, out var error))
                {
                    return ToolResult.Fail(error ?? "Invalid path.");
                }

                if (source.InMemory)
                {
                    bytes = source.ReadAllBytes();
                    path = temporary = Temporary(bytes, Path.GetExtension(source.Path));
                }
                else
                {
                    path = source.Path;
                    if (SensitivePaths.IsSensitive(path, out var secret))
                    {
                        return ToolResult.Fail(secret);
                    }

                    if (!File.Exists(path))
                    {
                        return ToolResult.Fail($"File not found: {path}. Pass the full path of an existing picture or PDF.");
                    }
                }
            }

            var kind = DocumentKinds.Of(path);
            if (kind is not (DocumentKind.Image or DocumentKind.Pdf))
            {
                return ToolResult.Fail("recognize_text reads pictures and PDF files. Read other documents with read_file.");
            }

            List<int> pages = [];
            if (kind == DocumentKind.Pdf)
            {
                var count = PdfEditor.PageCount(bytes ?? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
                pages = FileToolPaths.String(arguments, "pages") is { Length: > 0 } wanted
                    ? PdfEditor.ParsePages(wanted, count)
                    : [.. Enumerable.Range(1, Math.Min(count, LocalOcr.DefaultPages))];
                if (pages.Count > LocalOcr.MaxPages)
                {
                    return ToolResult.Fail($"That is {pages.Count} pages; recognize at most {LocalOcr.MaxPages} per call and continue with the next pages.");
                }
            }

            var (language, recognized) = await LocalOcr.RecognizeAsync(path, pages, FileToolPaths.String(arguments, "language"), cancellationToken).ConfigureAwait(false);
            return ToolResult.Ok(Report(raw, language, recognized, kind == DocumentKind.Pdf));
        }
        catch (DocumentException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
        catch (IOException ex)
        {
            return ToolResult.Fail($"The file could not be read: {ex.Message}.");
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static string Report(string source, string language, IReadOnlyList<OcrPage> pages, bool pdf)
    {
        var text = new StringBuilder("Text recognized in ").Append(source);
        if (language.Length > 0)
        {
            text.Append(" (").Append(language).Append(')');
        }

        text.Append(':');
        foreach (var page in pages)
        {
            if (pdf)
            {
                text.Append("\n\n--- Page ").Append(page.Number).Append(" ---");
            }

            text.Append('\n').Append(page.Text.Length > 0 ? page.Text : "[no text found]");
        }

        return text.ToString();
    }

    /// <summary>Временный файл для OCR Windows, который читает только файлы; удаляется сразу после.</summary>
    private static string Temporary(byte[] bytes, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), "amarin-ocr-" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
