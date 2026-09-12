using System.Text.Json;

namespace Amarin.Core;

/// <summary>Одна строка разбивки: что это за данные, сколько весит и из скольких файлов.</summary>
/// <param name="LabelKey">Ключ подписи в словаре строк.</param>
public readonly record struct UsageEntry(string LabelKey, long Bytes, int Files);

/// <summary>
/// Сколько места занимает программа и её данные.
/// </summary>
/// <param name="Entries">Категории, от крупной к мелкой; пустых в списке нет.</param>
/// <param name="TotalBytes">Сумма по всем категориям — данные пользователя, без самой программы.</param>
/// <param name="AttachmentBytes">Сколько из чатов приходится на вложения.</param>
/// <param name="AppBytes">Размер исполняемого файла. Считается отдельно: это не данные.</param>
public sealed record UsageReport(
    IReadOnlyList<UsageEntry> Entries,
    long TotalBytes,
    long AttachmentBytes,
    long AppBytes);

/// <summary>
/// Обход папок программы с разбивкой по тому, что человек узнаёт: чаты, настройки, языки,
/// снимки для отката, журналы.
/// </summary>
/// <remarks>
/// <para>
/// Профили отдельной категорией не выделены намеренно. У каждого профиля свои <c>chats</c>,
/// <c>settings.json</c> и аватар, и вопрос «сколько весят чаты» — это вопрос про все профили
/// сразу, а не про корневой. Поэтому файлы классифицируются по тому, что они такое, а не по
/// тому, в чьей папке лежат.
/// </para>
/// <para>
/// Вложений отдельной папкой не существует: картинки и документы лежат base64 прямо внутри
/// <c>chats/&lt;id&gt;.json</c>. Их вес поэтому не берётся у файловой системы, а считается
/// проходом по самому JSON — и это честная цена на диске, а не размер исходного файла, который
/// после base64 на треть меньше.
/// </para>
/// </remarks>
public static class DataUsage
{
    public const string ChatsKey = "S.Data.Usage.Chats";
    public const string AttachmentsKey = "S.Data.Usage.Attachments";
    public const string SettingsKey = "S.Data.Usage.Settings";
    public const string LanguagesKey = "S.Data.Usage.Languages";
    public const string SharedKey = "S.Data.Usage.Shared";
    public const string AppearanceKey = "S.Data.Usage.Appearance";
    public const string SnapshotsKey = "S.Data.Usage.Snapshots";
    public const string LogsKey = "S.Data.Usage.Logs";
    public const string OtherKey = "S.Data.Usage.Other";

    /// <summary>Сама программа. В сумму по данным не входит — её не почистишь.</summary>
    public const string AppKey = "S.Data.Usage.App";

    /// <summary>
    /// Считает всё разом. Ходит по диску, поэтому зовётся не с потока интерфейса.
    /// </summary>
    /// <param name="appRoot">Корень данных, обычно <see cref="AppPaths.Root"/>.</param>
    /// <param name="localRoot">Папка диагностики в <c>%LOCALAPPDATA%</c>: журналы и снимки.</param>
    /// <param name="exePath">Путь к программе; <c>null</c> — не показывать её размер.</param>
    public static UsageReport Measure(
        string appRoot,
        string localRoot,
        string? exePath,
        CancellationToken cancellationToken = default)
    {
        var bytes = new Dictionary<string, long>(StringComparer.Ordinal);
        var files = new Dictionary<string, int>(StringComparer.Ordinal);
        var attachments = 0L;

        void Add(string key, long size)
        {
            bytes[key] = bytes.GetValueOrDefault(key) + size;
            files[key] = files.GetValueOrDefault(key) + 1;
        }

        // Один буфер на весь обход, а не массив на каждый файл: вложения лежат base64 внутри
        // chats/<id>.json, поэтому чтение целиком отправляло в кучу больших объектов по
        // многомегабайтному массиву на каждый чат. Их сборка останавливает и поток интерфейса —
        // работа идёт в фоне, а подлагивает на экране.
        var buffer = Array.Empty<byte>();

        foreach (var file in Walk(appRoot, cancellationToken))
        {
            var key = ClassifyAppFile(Relative(appRoot, file.FullName));
            Add(key, file.Length);

            if (key == ChatsKey)
            {
                attachments += CountAttachmentBytes(file, ref buffer);
            }
        }

        foreach (var file in Walk(localRoot, cancellationToken))
        {
            Add(ClassifyLocalFile(Relative(localRoot, file.FullName)), file.Length);
        }

        var entries = bytes
            .Where(pair => pair.Value > 0)
            .Select(pair => new UsageEntry(pair.Key, pair.Value, files[pair.Key]))
            .OrderByDescending(entry => entry.Bytes)
            .ToList();

        return new UsageReport(entries, entries.Sum(e => e.Bytes), attachments, AppSize(exePath));
    }

    /// <summary>
    /// Куда отнести файл под корнем данных. Путь — относительный, через прямой слэш,
    /// в нижнем регистре.
    /// </summary>
    internal static string ClassifyAppFile(string relative)
    {
        var name = LastSegment(relative);

        // Проверки по имени идут первыми: settings.json лежит и в корне, и в каждом профиле,
        // а по папке их не различить.
        if (name is "settings.json" or "profiles.json" or "balance.json")
        {
            return SettingsKey;
        }

        if (name == "avatar.png" || name.StartsWith("background.", StringComparison.Ordinal))
        {
            return AppearanceKey;
        }

        if (InFolder(relative, "chats"))
        {
            return ChatsKey;
        }

        if (InFolder(relative, "languages"))
        {
            return LanguagesKey;
        }

        if (InFolder(relative, "shared"))
        {
            return SharedKey;
        }

        return OtherKey;
    }

    /// <summary>Куда отнести файл под папкой диагностики.</summary>
    internal static string ClassifyLocalFile(string relative)
    {
        if (InFolder(relative, "snapshots"))
        {
            return SnapshotsKey;
        }

        return LastSegment(relative).EndsWith(".log", StringComparison.Ordinal) ? LogsKey : OtherKey;
    }

    /// <summary>
    /// Сколько байтов файла чата занимают вложения.
    /// </summary>
    /// <remarks>
    /// Вложение доезжает до диска в двух видах: в стенограмме для модели — как <c>data:</c>-URI,
    /// в сообщении для экрана — голой строкой в поле <c>base64</c>. Считаются оба, иначе половина
    /// веса пропала бы из отчёта. Обычный текст под правило не подходит: у него нет ни такого
    /// поля, ни такого начала.
    /// </remarks>
    internal static long CountAttachmentBytes(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        var total = 0L;
        var inBase64Property = false;

        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        inBase64Property = reader.ValueTextEquals("base64"u8);
                        break;

                    case JsonTokenType.String:
                        var value = reader.ValueSpan;
                        if (inBase64Property || value.StartsWith("data:"u8))
                        {
                            total += value.Length;
                        }

                        inBase64Property = false;
                        break;

                    default:
                        inBase64Property = false;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Повреждённый чат — обычное дело, а не авария: посчитаем его целиком как чат
            // и просто не будем знать, сколько в нём вложений.
            return total;
        }

        return total;
    }

    /// <param name="buffer">
    /// Общий буфер обхода: дорастает до самого большого файла чата и переиспользуется дальше.
    /// </param>
    private static long CountAttachmentBytes(FileInfo file, ref byte[] buffer)
    {
        try
        {
            using var stream = file.OpenRead();
            var length = stream.Length;
            if (length <= 0 || length > int.MaxValue)
            {
                return 0;
            }

            var size = (int)length;
            if (buffer.Length < size)
            {
                buffer = new byte[size];
            }

            // Файл мог укоротиться между Walk и чтением — считаем то, что дочитали.
            var read = stream.ReadAtLeast(buffer.AsSpan(0, size), size, throwOnEndOfStream: false);
            return CountAttachmentBytes(buffer.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            return 0;
        }
    }

    private static long AppSize(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return 0;
        }

        try
        {
            var info = new FileInfo(exePath);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Все файлы под корнем. Недоступная папка пропускается: отчёт о занятом месте не та вещь,
    /// ради которой стоит показывать человеку исключение.
    /// </summary>
    private static IEnumerable<FileInfo> Walk(string root, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            yield break;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        IEnumerator<FileInfo> walker;
        try
        {
            walker = new DirectoryInfo(root).EnumerateFiles("*", options).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileInfo current;
            try
            {
                if (!walker.MoveNext())
                {
                    break;
                }

                current = walker.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }

            yield return current;
        }

        walker.Dispose();
    }

    private static string Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Replace('\\', '/').ToLowerInvariant();
    }

    private static string LastSegment(string relative)
    {
        var cut = relative.LastIndexOf('/');
        return cut < 0 ? relative : relative[(cut + 1)..];
    }

    /// <summary>Лежит ли файл в папке с таким именем на любом уровне вложенности.</summary>
    private static bool InFolder(string relative, string folder)
    {
        var cut = relative.LastIndexOf('/');
        if (cut < 0)
        {
            return false;
        }

        foreach (var segment in relative[..cut].Split('/'))
        {
            if (segment == folder)
            {
                return true;
            }
        }

        return false;
    }
}
