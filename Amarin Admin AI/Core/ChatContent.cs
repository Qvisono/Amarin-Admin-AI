using System.Text;
using System.Text.Json;

namespace Amarin.Core;

internal static class ChatContent
{
    public static JsonElement Text(string value) =>
        JsonSerializer.SerializeToElement(value, VeniceJsonContext.Default.String);

    public static JsonElement Vision(string prompt, string base64, string mimeType = "image/png") =>
        VisionMultiple(prompt, [new Tools.ImageAttachment(base64, mimeType)]);

    public static JsonElement VisionMultiple(string prompt, IReadOnlyList<Tools.ImageAttachment> images) =>
        Multipart(prompt, images, files: null);

    /// <summary>
    /// Сообщение из нескольких частей: текст, картинки и документы.
    /// </summary>
    /// <remarks>
    /// Формат OpenAI-совместимый. Текстовая часть обязана идти первой — без неё часть моделей
    /// спотыкается на массиве содержимого. Документы уходят частью <c>file</c>: Venice сам
    /// извлекает из них текст (PDF, DOCX, XLSX, исходники), поэтому разбирать их в программе
    /// не нужно, а <c>filename</c> модель видит и на него ссылается в ответе.
    /// </remarks>
    public static JsonElement Multipart(
        string prompt,
        IReadOnlyList<Tools.ImageAttachment>? images,
        IReadOnlyList<Tools.FileAttachment>? files)
    {
        var parts = new List<object> { new { type = "text", text = prompt } };

        if (images is not null)
        {
            foreach (var image in images)
            {
                parts.Add(new
                {
                    type = "image_url",
                    image_url = new { url = $"data:{image.MimeType};base64,{image.Base64}" }
                });
            }
        }

        if (files is not null)
        {
            foreach (var file in files)
            {
                parts.Add(new
                {
                    type = "file",
                    file = new
                    {
                        file_data = $"data:{file.MimeType};base64,{file.Base64}",
                        filename = file.FileName
                    }
                });
            }
        }

        return JsonSerializer.SerializeToElement(parts);
    }

    public static JsonElement? Clone(JsonElement? content) =>
        content is null ? null : content.Value.Clone();

    public static string? ReadText(JsonElement? content)
    {
        if (content is null || content.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (content.Value.ValueKind == JsonValueKind.String)
        {
            return content.Value.GetString();
        }

        if (content.Value.ValueKind != JsonValueKind.Array)
        {
            return content.Value.ToString();
        }

        var sb = new StringBuilder();
        foreach (var part in content.Value.EnumerateArray())
        {
            if (part.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString() == "text" &&
                part.TryGetProperty("text", out var textProp))
            {
                sb.AppendLine(textProp.GetString());
            }
        }

        var text = sb.ToString().Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}