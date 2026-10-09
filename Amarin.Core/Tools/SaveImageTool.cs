using System.Drawing.Imaging;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>Картинка из чата — нарисованная или принесённая — файлом на диске.</summary>
/// <remarks>
/// Картинки чата живут в переписке, а не на диске: <c>generate_image</c> и <c>fetch_image</c>
/// отдают ссылку <c>amarin-image:</c>, и сохранить нарисованное было нечем — модель отвечала, что
/// доступа к файлам у неё нет. Формат файла задаёт расширение пути; если он другой, картинка
/// перекодируется (WebP перекодировать нечем — его сохраняют как есть).
/// </remarks>
public sealed class SaveImageTool : ITool
{
    public string Name => "save_image";

    public string Description =>
        "Save a picture from this chat to disk and get its path back. image is an amarin-image: handle from " +
        "generate_image or fetch_image. The extension of path picks the format: .png, .jpg, .bmp, .gif, or .webp for a " +
        "WebP picture. An existing file is kept unless overwrite=true. Relative paths go to the user's Downloads folder.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "image": { "type": "string", "description": "amarin-image: handle" },
            "path": { "type": "string", "description": "Where to save, with the extension of the wanted format" },
            "overwrite": { "type": "boolean", "description": "Replace the file if it exists (default false)" }
          },
          "required": ["image", "path"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        Task.Run(() => Save(arguments), cancellationToken);

    private static ToolResult Save(JsonElement arguments)
    {
        var handle = FileToolPaths.String(arguments, "image")?.Trim() ?? "";
        if (ChatImageRegistry.Find(handle) is not { } image)
        {
            return ToolResult.Fail(
                $"\"{handle}\" is not a picture of this chat. Use the amarin-image: handle that generate_image or fetch_image gave you.");
        }

        if (!FileToolPaths.TryResolve(FileToolPaths.String(arguments, "path"), out var path, out var error))
        {
            return ToolResult.Fail(error ?? "Invalid path.");
        }

        if (SensitivePaths.IsProgramData(path))
        {
            return ToolResult.Fail(SensitivePaths.ProgramDataRefusal(path));
        }

        var overwrite = !FileToolPaths.Flag(arguments, SafeZone.CreateNewFlag) && FileToolPaths.Flag(arguments, "overwrite");
        if (File.Exists(path) && !overwrite)
        {
            return ToolResult.Fail($"{path} already exists. Pick another name, or set overwrite=true (the user will be asked).");
        }

        byte[] source;
        try
        {
            source = ChatImageRegistry.OriginalOf(image) ?? Convert.FromBase64String(image.Base64);
        }
        catch (FormatException)
        {
            return ToolResult.Fail("The picture data is damaged and cannot be saved.");
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        var bytes = Encode(source, extension, out var problem);
        if (bytes is null)
        {
            return ToolResult.Fail(problem!);
        }

        try
        {
            DocumentFiles.WriteAtomically(path, stream => stream.Write(bytes), overwrite);
        }
        catch (DocumentException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Fail($"The picture could not be written: {ex.Message}");
        }

        return ToolResult.WithFile($"Saved the picture to {path} ({FileToolPaths.Size(bytes.Length)}).", FileToolPaths.Card(path));
    }

    /// <summary>Байты в формате расширения: как есть, если он уже тот, иначе перекодированные.</summary>
    private static byte[]? Encode(byte[] source, string extension, out string? problem)
    {
        problem = null;
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp"))
        {
            problem = $"\"{extension}\" is not a picture format this tool writes. Use .png, .jpg, .bmp, .gif or .webp.";
            return null;
        }

        var target = extension switch
        {
            ".png" => ImageFormat.Png,
            ".jpg" or ".jpeg" => ImageFormat.Jpeg,
            ".bmp" => ImageFormat.Bmp,
            ".gif" => ImageFormat.Gif,
            _ => null
        };

        var actual = ImageHelpers.SniffMimeType(source);
        if (target is null)
        {
            if (actual == "image/webp")
            {
                return source;
            }

            problem = "The picture is not WebP; save it as .png or .jpg.";
            return null;
        }

        if (actual == MimeOf(target))
        {
            return source;
        }

        try
        {
            using var input = new MemoryStream(source, writable: false);
            using var bitmap = System.Drawing.Image.FromStream(input);
            using var output = new MemoryStream();
            bitmap.Save(output, target);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            problem = actual == "image/webp"
                ? "The picture is WebP and cannot be converted here; save it with the .webp extension."
                : "The picture could not be converted to that format; try .png.";
            return null;
        }
    }

    private static string MimeOf(ImageFormat format) =>
        format.Equals(ImageFormat.Png) ? "image/png"
        : format.Equals(ImageFormat.Jpeg) ? "image/jpeg"
        : format.Equals(ImageFormat.Bmp) ? "image/bmp"
        : "image/gif";
}
