using System.Drawing;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class ClipboardTool : ITool
{
    public string Name => "read_clipboard";
    public string Description =>
        "Read and analyze content from the Windows clipboard: text, images, or copied files. " +
        "Use when the user asks to analyze clipboard content or pasted data.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "format": {
              "type": "string",
              "enum": ["auto", "text", "image", "files"],
              "description": "auto = detect best format, or force a specific type"
            },
            "read_file_preview": {
              "type": "boolean",
              "description": "When clipboard has files, preview first text file content (default true)"
            }
          }
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var format = "auto";
            if (arguments.TryGetProperty("format", out var formatProp) &&
                formatProp.ValueKind == JsonValueKind.String)
            {
                format = formatProp.GetString() ?? "auto";
            }

            var readFilePreview = !arguments.TryGetProperty("read_file_preview", out var previewProp) ||
                                  previewProp.ValueKind != JsonValueKind.False;

            return Task.FromResult(StaThread.Run(() => ReadClipboard(format, readFilePreview)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Clipboard error: {ex.Message}"));
        }
    }

    private static ToolResult ReadClipboard(string format, bool readFilePreview)
    {
        if (!ClipboardNative.HasText() &&
            !ClipboardNative.HasImage() &&
            !ClipboardNative.HasFiles())
        {
            return ToolResult.Fail("Буфер обмена пуст или содержит неподдерживаемый формат.");
        }

        if (format is "image" or "auto" && ClipboardNative.HasImage())
        {
            using var bitmap = ClipboardNative.TryGetBitmap();
            if (bitmap is not null)
            {
                var attachment = ImageHelpers.FromBitmap(bitmap, "clipboard");
                return ToolResult.WithImages(
                    "В буфере обмена изображение. Оно прикреплено для анализа.",
                    [attachment]);
            }
        }

        if (format is "files" or "auto" && ClipboardNative.HasFiles())
        {
            var files = ClipboardNative.TryGetFiles();
            if (files.Count > 0)
            {
                return ReadClipboardFiles(files, readFilePreview);
            }
        }

        if (format is "text" or "auto" && ClipboardNative.HasText())
        {
            var text = ClipboardNative.TryGetText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return ToolResult.Ok(FormatTextResult("текст", text));
            }
        }

        return ToolResult.Fail("Не удалось прочитать буфер обмена в запрошенном формате.");
    }

    private static ToolResult ReadClipboardFiles(IReadOnlyList<string> files, bool readFilePreview)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"В буфере обмена {files.Count} элемент(ов):");
        var images = new List<ImageAttachment>();

        foreach (var item in files)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            var path = Path.GetFullPath(item);
            if (Directory.Exists(path))
            {
                sb.AppendLine($"  [DIR]  {path}");
                continue;
            }

            if (!File.Exists(path))
            {
                sb.AppendLine($"  [????] {path}");
                continue;
            }

            var info = new FileInfo(path);
            sb.AppendLine($"  [FILE] {path} ({info.Length} bytes)");

            if (ImageHelpers.IsImageFile(path))
            {
                var image = ImageHelpers.FromFile(path);
                if (image is not null)
                {
                    images.Add(image);
                }
            }
        }

        if (readFilePreview)
        {
            var textPreview = TryReadFirstTextFile(files);
            if (textPreview is not null)
            {
                sb.AppendLine();
                sb.Append(textPreview);
            }
        }

        if (images.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Прикреплено изображений для просмотра: {images.Count}");
            return ToolResult.WithImages(sb.ToString().TrimEnd(), images);
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static string? TryReadFirstTextFile(IReadOnlyList<string> files)
    {
        foreach (var item in files)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            var path = Path.GetFullPath(item);
            if (!File.Exists(path) || ImageHelpers.IsImageFile(path))
            {
                continue;
            }

            var info = new FileInfo(path);
            if (info.Length > 64_000)
            {
                return $"=== {path} ===\nФайл слишком большой для предпросмотра ({info.Length} bytes).";
            }

            try
            {
                var content = File.ReadAllText(path);
                return $"=== {path} ===\n{Truncate(content, 8_000)}";
            }
            catch
            {
                continue;
            }
        }

        return null;
    }

    private static string FormatTextResult(string kind, string text) =>
        $"Буфер обмена ({kind}):\n{Truncate(text, 16_000)}";

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "\n… [обрезано]";
}