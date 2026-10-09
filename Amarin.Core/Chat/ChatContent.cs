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
    /// Начало текста у сообщения, которым движок отдаёт модели картинки инструмента.
    /// </summary>
    /// <remarks>
    /// Роль у такого сообщения <c>user</c> — иначе картинку модели не показать, — но ходом
    /// человека оно не является. По этой строке его и отличают при разрезе истории: и в новых
    /// переписках, и в старых, где она лежит с тех пор, как картинки инструментов появились.
    /// Менять её нельзя — старые файлы перестали бы узнаваться.
    /// </remarks>
    public const string ToolImagePrefix = "Результат инструмента ";

    /// <summary>Картинки, которые вернул инструмент, в виде сообщения для модели.</summary>
    public static JsonElement ToolImages(
        string toolName,
        string placement,
        IReadOnlyList<Tools.ImageAttachment> images) =>
        VisionMultiple($"{ToolImagePrefix}{toolName}. {placement}", images);

    /// <summary>
    /// Сообщение с картинками инструмента, а не ход человека.
    /// </summary>
    /// <remarks>
    /// Два признака сразу: содержимое из нескольких частей и текст с <see cref="ToolImagePrefix"/>.
    /// Одной строки мало — человек мог сам начать сообщение с этих слов, но тогда без вложений
    /// оно ушло бы простой строкой, а не массивом.
    /// </remarks>
    public static bool IsToolImage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) ||
            message.Content is not { ValueKind: JsonValueKind.Array } content)
        {
            return false;
        }

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                type.GetString() == "text" &&
                part.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                return text.GetString()?.StartsWith(ToolImagePrefix, StringComparison.Ordinal) == true;
            }
        }

        return false;
    }

    /// <summary>
    /// Запись истории модели, с которой начинается ход человека: <c>user</c>, но не картинки
    /// инструмента.
    /// </summary>
    public static bool IsTurnStart(ChatMessage message) =>
        message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) && !IsToolImage(message);

    /// <summary>
    /// Сообщение из нескольких частей: текст, картинки и документы.
    /// </summary>
    /// <remarks>
    /// Формат OpenAI-совместимый. Текстовая часть обязана идти первой — без неё часть моделей
    /// спотыкается на массиве содержимого. Частью <c>file</c> с 1.33.0 уходят только документы,
    /// которые не прочитались здесь (скан PDF без текстового слоя): их распознаёт провайдер, а
    /// прочитанные едут текстом (<see cref="DocumentDigest"/>).
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
        var digested = new List<string>();
        foreach (var file in files)
        {
            sb.Append("- ")
                .Append(file.FileName)
                .Append(" - ")
                .Append(AttachmentTypes.Badge(file.FileName))
                .Append(", ")
                .Append(AttachmentTypes.FormatSize(file.SizeBytes));

            // Путь — тот, по которому вложение читают инструменты: исходный файл, если он на месте
            // и тот же, иначе ручка вложения. Чужой путь врал бы, и модель правила бы не тот файл.
            sb.Append(", ").Append(DocumentDigest.PathOf(file)).AppendLine();
            if (DocumentDigest.TextOf(file, files.Count) is { } block)
            {
                digested.Add(block);
            }
        }

        if (digested.Count > 0)
        {
            sb.AppendLine(
                "Текст документов прочитан на этом ПК и стоит ниже блоками <document>: номера абзацев, строк и страниц - " +
                "те же, что у read_file и edit_document. Если показана только часть, дочитывай read_file по пути из блока.");
        }

        if (digested.Count < files.Count)
        {
            sb.AppendLine("Остальные файлы переданы вместе с сообщением как есть.");
        }

        foreach (var block in digested)
        {
            sb.AppendLine().Append(block).AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Вложения, которые уходят провайдеру файлом: прочитанные здесь едут текстом.</summary>
    private static List<Tools.FileAttachment>? AsFiles(IReadOnlyList<Tools.FileAttachment>? files) =>
        files?.Where(file => DocumentDigest.TextOf(file, files.Count) is null).ToList() is { Count: > 0 } rest ? rest : null;

    /// <summary>
    /// Содержимое сообщения человека для модели, собранное из того, что лежит в переписке.
    /// </summary>
    /// <remarks>
    /// Одно место на все случаи — первая отправка, правка текста, пересборка после удаления
    /// хода. Пока их было несколько, каждая новая часть сообщения (сперва документы, теперь
    /// цитаты) молча терялась в той копии, куда её забыли добавить.
    /// </remarks>
    /// <param name="index">Где это сообщение стоит в <paramref name="messages"/>.</param>
    public static JsonElement ForUser(
        ChatDisplayMessage user,
        IReadOnlyList<ChatDisplayMessage> messages,
        int index)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(messages);

        var images = user.Images.Count == 0 ? null : user.Images;
        var files = user.Files.Count == 0 ? null : user.Files;
        var body = images is null && files is null ? user.Text : BuildPrompt(user.Text, images, files);
        var text = ChatQuotes.Wrap(body, user.Quotes, messages, index);
        var raw = AsFiles(files);

        return images is null && raw is null
            ? Text(text)
            : Multipart(text, images, raw);
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