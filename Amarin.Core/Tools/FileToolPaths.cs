using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>Пути файловых инструментов чата.</summary>
/// <remarks>
/// Относительный путь раскрывается от «Загрузок», а не от рабочей папки процесса: та — папка с
/// самой программой, и «отчёт.docx» без папки ложился бы рядом с exe, где человек его не найдёт,
/// а шлюз ещё и спросил бы о записи в чужую папку. «Загрузки» — место, куда человек и так
/// заглядывает за тем, что ему прислали, и где новый файл создаётся без вопроса. Путь, начатый
/// именем известной папки («Desktop\план.docx», «Документы/смета.xlsx»), ведёт в саму эту папку.
/// </remarks>
internal static class FileToolPaths
{
    /// <summary>Начало ручки вложения: такой «путь» раскрывает реестр вложений, а не диск.</summary>
    public const string AttachmentScheme = "amarin-attachment:";

    /// <summary>Инструменты, чьи пути раскрывает это правило, — и только они.</summary>
    private static readonly FrozenSet<string> PathTools = new[]
    {
        "read_file", "write_file", "edit_file", "create_folder", "create_document", "edit_document", "save_image"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool TryResolve(string? raw, out string resolved, out string? error)
    {
        var value = (raw ?? "").Trim().Trim('"');
        if (value.Length > 0 && !IsAnchored(value))
        {
            value = UnderKnownFolder(value) ?? Path.Combine(DownloadPaths.DownloadsDirectory, value);
        }

        return PathResolver.TryResolve(value, out resolved, out error);
    }

    /// <summary>
    /// Откуда читать: файл по пути или вложение чата по его ручке. Правке на месте вложение не
    /// годится — это решает вызывающий по <see cref="DocumentSource.InMemory"/>.
    /// </summary>
    public static bool TryResolveSource(string? raw, out DocumentSource source, out string? error)
    {
        if (IsAttachmentHandle(raw))
        {
            var handle = raw.Trim();
            if (ChatAttachmentRegistry.Find(handle) is { } attachment)
            {
                source = new DocumentSource(handle, attachment.Content);
                error = null;
                return true;
            }

            source = DocumentSource.File(handle);
            error = $"{handle} is not an attachment of an open chat any more. Ask the user to attach the file again.";
            return false;
        }

        var ok = TryResolve(raw, out var path, out error);
        source = DocumentSource.File(path);
        return ok;
    }

    /// <summary>Ручка вложения из блока <c>&lt;document&gt;</c>, а не путь на диске.</summary>
    public static bool IsAttachmentHandle([NotNullWhen(true)] string? value) =>
        value?.Trim().StartsWith(AttachmentScheme, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Отказ правке по ручке вложения: у вложения без файла на диске править нечего.</summary>
    public const string HandleIsReadOnly =
        "This attachment exists only inside the chat, not as a file on disk, so it cannot be changed in place. " +
        "Use edit_document with save_as (or create_document) to make an edited copy in a real folder.";

    /// <summary>Замок на путь, если служба есть; без неё — пустой.</summary>
    public static async Task<IDisposable> LockAsync(FileToolState? state, string path, CancellationToken cancellationToken) =>
        state is null ? NoLock.Instance : await state.LockAsync(path, cancellationToken).ConfigureAwait(false);

    private sealed class NoLock : IDisposable
    {
        public static readonly NoLock Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Аргументы файлового инструмента с путями, раскрытыми так же, как их раскроет сам инструмент.
    /// </summary>
    /// <remarks>
    /// Шлюз проверял сырую строку, а инструмент писал по раскрытой от «Загрузок»: относительный путь
    /// не считался «новым файлом в Загрузках», вопрос показывал голое имя, а
    /// <c>..\..\AppData\Roaming\Amarin Admin AI\settings.json</c> проходил мимо запрета на папку
    /// данных программы. Шлюз зовёт это до всех проверок и дальше исполняет уже раскрытое, поэтому
    /// вопрос, аудит, запреты и сам инструмент видят один и тот же путь.
    /// </remarks>
    public static JsonElement CanonicalArguments(string tool, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !PathTools.Contains(tool))
        {
            return arguments;
        }

        JsonObject? node = null;
        foreach (var field in (string[])["path", "save_as"])
        {
            if (String(arguments, field) is not { Length: > 0 } raw || IsAttachmentHandle(raw) ||
                !TryResolve(raw, out var resolved, out _) ||
                string.Equals(raw, resolved, StringComparison.Ordinal))
            {
                continue;
            }

            node ??= JsonNode.Parse(arguments.GetRawText()) as JsonObject;
            if (node is null)
            {
                return arguments;
            }

            node[field] = resolved;
        }

        return node is null ? arguments : JsonSerializer.SerializeToElement(node);
    }

    /// <summary>Путь уже говорит, от чего считать: диск, сеть, домашняя папка или переменная.</summary>
    private static bool IsAnchored(string value) =>
        Path.IsPathRooted(value) ||
        value.StartsWith('~') ||
        value.StartsWith('%');

    /// <summary>Путь, начатый именем известной папки, — внутри неё; иначе null.</summary>
    private static string? UnderKnownFolder(string value)
    {
        var separator = value.IndexOfAny(['\\', '/']);
        var head = separator < 0 ? value : value[..separator];
        var folder = head.Trim().ToLowerInvariant() switch
        {
            "desktop" or "рабочий стол" => DownloadPaths.DesktopDirectory,
            "downloads" or "загрузки" => DownloadPaths.DownloadsDirectory,
            "documents" or "документы" or "мои документы" => DownloadPaths.DocumentsDirectory,
            _ => null
        };

        if (folder is null)
        {
            return null;
        }

        return separator < 0 ? folder : Path.Combine(folder, value[(separator + 1)..]);
    }

    /// <summary>Строковый аргумент или null.</summary>
    public static string? String(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Целое число из аргумента: модели присылают и 12, и "12".</summary>
    public static int? Int(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Логический флаг: true, "true", 1.</summary>
    public static bool Flag(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True ||
         (value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase)) ||
         (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n == 1));

    /// <summary>
    /// Размер для ответа модели — по-английски, как и весь ответ инструмента: общий
    /// <c>AttachmentTypes.FormatSize</c> подписывает размер на языке интерфейса («2,4 МБ»).
    /// </summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / (1024.0 * 1024)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB"
    };

    /// <summary>Карточка файла для ленты: путь, имя и размер с диска.</summary>
    public static SavedFile Card(string path)
    {
        long size = 0;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Размер — украшение карточки: файл записан, и врать из-за перемера незачем.
        }

        return new SavedFile(path, Path.GetFileName(path), size);
    }
}
