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

    /// <summary>
    /// Текстовая часть сообщения с вложениями: сам текст плюс перечень того, что приложено.
    /// </summary>
    /// <remarks>
    /// Без этого перечня модель не понимала, что содержимое файла уже лежит у неё в контексте:
    /// на «прочти файл» она брала <c>read_file</c> с голым именем, тот раскрывал относительный
    /// путь от рабочей папки программы и отвечал «File not found: …\bin\…», после чего модель
    /// сообщала человеку, что доступа к файлу нет. Перечень называет имя, размер и настоящий
    /// путь — путь нужен на вопросы про сам файл (где лежит, когда изменён), а не про его текст.
    /// Ещё он переживает выброс вложений из истории: <see cref="ApiContextLimiter"/> сохраняет
    /// текстовую часть, поэтому имена и пути остаются в контексте и после того, как base64 ушёл.
    /// </remarks>
    public static string BuildPrompt(
        string text,
        IReadOnlyList<Tools.ImageAttachment>? images,
        IReadOnlyList<Tools.FileAttachment>? files)
    {
        var prompt = string.IsNullOrWhiteSpace(text) ? StandIn(images, files) : text.Trim();
        if (files is not { Count: > 0 })
        {
            return prompt;
        }

        var sb = new StringBuilder(prompt);
        sb.AppendLine().AppendLine().AppendLine("[Вложения к этому сообщению]");
        foreach (var file in files)
        {
            sb.Append("- ")
                .Append(file.FileName)
                .Append(" — ")
                .Append(AttachmentTypes.Badge(file.FileName))
                .Append(", ")
                .Append(AttachmentTypes.FormatSize(file.SizeBytes));

            // Путь пишем, только если файл и правда там лежит: у чата, открытого на другой
            // машине или после переезда файла, путь врал бы, и модель послала бы туда агента.
            if (!string.IsNullOrWhiteSpace(file.SourcePath) && File.Exists(file.SourcePath))
            {
                sb.Append(", ").Append(file.SourcePath);
            }

            sb.AppendLine();
        }

        sb.AppendLine(
            "Содержимое этих файлов передано вместе с сообщением — открывать их инструментом не нужно.");
        sb.Append(
            "Путь указан на случай вопросов про сам файл на диске, а не про его содержимое.");
        return sb.ToString();
    }

    /// <summary>
    /// Массив содержимого всегда начинается с текста, поэтому ход из одних вложений нуждается
    /// в подставной просьбе, а не в пустой строке, которую модели приходится угадывать.
    /// </summary>
    public static string StandIn(
        IReadOnlyList<Tools.ImageAttachment>? images,
        IReadOnlyList<Tools.FileAttachment>? files)
    {
        if (files is { Count: > 0 })
        {
            return images is { Count: > 0 } ? "Посмотри вложения." : "Прочитай вложенные файлы.";
        }

        return "Посмотри на изображение.";
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