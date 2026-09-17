using System.Text.Json;

namespace Amarin.Tools;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement ParametersSchema { get; }
    Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default);
}

/// <summary>
/// Файл, который инструмент положил на диск.
/// </summary>
/// <remarks>
/// Содержимого здесь нет — в отличие от <see cref="FileAttachment"/>, который возит документ
/// base64 прямо в переписке. Скачанный дистрибутив на восемьдесят мегабайт уехал бы в
/// <c>chats/*.json</c> и в каждую ссылку на чат; от файла на своей машине нужен путь, а не копия.
/// Поэтому карточка такого файла живёт ровно столько, сколько сам файл: переехал или удалён —
/// открывать нечего, и карточка об этом честно говорит.
/// </remarks>
/// <param name="Path">Полный путь. По нему открывается проводник.</param>
/// <param name="FileName">Имя с расширением; из него берётся метка типа в карточке.</param>
/// <param name="SizeBytes">Размер на момент записи.</param>
public sealed record SavedFile(string Path, string FileName, long SizeBytes);

public sealed record ToolResult(
    bool Success,
    string Output,
    string? ImageBase64 = null,
    string? ImageMimeType = null,
    IReadOnlyList<ImageAttachment>? Images = null,
    IReadOnlyList<SavedFile>? Files = null)
{
    public static ToolResult Ok(string output) => new(true, output);
    public static ToolResult Fail(string output) => new(false, output);

    public static ToolResult WithImage(string output, string base64, string mimeType = "image/png") =>
        new(true, output, base64, mimeType, [new ImageAttachment(base64, mimeType)]);

    public static ToolResult WithImages(string output, IReadOnlyList<ImageAttachment> images) =>
        new(true, output, Images: images);

    /// <summary>
    /// Успех, при котором на диске появился файл. Текст для модели остаётся прежним: она читает
    /// его как раньше, а путь дублируется отдельным полем только ради карточки в чате.
    /// </summary>
    public static ToolResult WithFile(string output, SavedFile file) =>
        new(true, output, Files: [file]);

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

    public IReadOnlyList<SavedFile> GetFiles() => Files is { Count: > 0 } ? Files : [];

    public bool HasFiles => GetFiles().Count > 0;
}
