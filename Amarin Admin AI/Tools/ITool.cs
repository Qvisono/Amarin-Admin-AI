using System.Text.Json;

namespace Amarin.Tools;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement ParametersSchema { get; }
    Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default);
}

public sealed record ToolResult(
    bool Success,
    string Output,
    string? ImageBase64 = null,
    string? ImageMimeType = null,
    IReadOnlyList<ImageAttachment>? Images = null)
{
    public static ToolResult Ok(string output) => new(true, output);
    public static ToolResult Fail(string output) => new(false, output);

    public static ToolResult WithImage(string output, string base64, string mimeType = "image/png") =>
        new(true, output, base64, mimeType, [new ImageAttachment(base64, mimeType)]);

    public static ToolResult WithImages(string output, IReadOnlyList<ImageAttachment> images) =>
        new(true, output, Images: images);

    public IReadOnlyList<ImageAttachment> GetImages()
    {
        if (Images is { Count: > 0 })
        {
            return Images;
        }

        if (ImageBase64 is not null)
        {
            return [new ImageAttachment(ImageBase64, ImageMimeType ?? "image/png")];
        }

        return [];
    }

    public bool HasImages => GetImages().Count > 0;
}