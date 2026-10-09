using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>
/// Документ, приложенный к сообщению, — текстом для модели, прочитанным на этом ПК, и путь (или
/// ручка), по которому его потом дочитывают и правят инструменты.
/// </summary>
/// <remarks>
/// <para>
/// До 1.33.0 документ уходил провайдеру файлом, и текст из него доставал провайдер — как умел:
/// OpenRouter не разбирал Word вовсе, а разобранное сливалось в одну простыню без заголовков,
/// списков и таблиц. Теперь документ читается здесь, Markdown-ом с номерами абзацев, страниц или
/// строк листа — теми же, на которые потом ссылается правка.
/// </para>
/// <para>
/// В сообщение кладётся начало документа и оглавление, а дальше модель читает сама —
/// <c>read_file</c> по пути из заголовка блока. Блок узнаётся по разметке
/// <c>&lt;document … path="…"&gt;</c>: в старых сообщениях истории он сворачивается до ссылки на
/// путь (<see cref="ContextHygiene"/>), и прочитанный однажды документ не едет с каждым следующим
/// вопросом.
/// </para>
/// <para>
/// PDF без текстового слоя (скан) и то, что здесь не читается, по-прежнему уходят провайдеру
/// файлом: тот умеет распознавать картинки.
/// </para>
/// </remarks>
internal static class DocumentDigest
{
    /// <summary>Сколько знаков одного документа идёт вместе с сообщением, если он один или их два.</summary>
    public const int FileBudget = 8_000;

    /// <summary>Сколько знаков всех документов сообщения вместе: пять файлов по восемь тысяч — это много.</summary>
    public const int MessageBudget = 20_000;

    /// <summary>Начало блока документа в тексте сообщения — по нему его узнаёт сжатие истории.</summary>
    public const string OpenTag = "<document ";

    public const string CloseTag = "</document>";

    /// <summary>Сколько строк оглавления показать: дальше их видно чтением.</summary>
    private const int OutlineEntries = 40;

    private static readonly ConditionalWeakTable<FileAttachment, Dictionary<int, Digest>> Cache = [];

    /// <summary>Путь отдельно от текста: спросить путь не значит разбирать документ.</summary>
    private static readonly ConditionalWeakTable<FileAttachment, string> Paths = [];

    /// <summary>Прочитанное вложение: путь или ручка для инструментов и текст для модели (null — уходит файлом).</summary>
    internal sealed record Digest(string Path, string? Text);

    /// <summary>Бюджет одного документа, когда их в сообщении <paramref name="count"/>.</summary>
    public static int BudgetFor(int count) => count <= 2 ? FileBudget : Math.Max(2_000, MessageBudget / count);

    /// <summary>Блок документа для сообщения или null, если документ уходит провайдеру файлом.</summary>
    public static string? TextOf(FileAttachment file, int count = 1) => Of(file, BudgetFor(count)).Text;

    /// <summary>Путь на диске или ручка вложения — по ним его читают инструменты.</summary>
    public static string PathOf(FileAttachment file) => Paths.GetValue(file, attachment => PathFor(attachment, Decode(attachment.Base64)));

    /// <summary>
    /// Читает вложения заранее — на рабочем потоке: разбор стостраничного PDF на потоке окна
    /// держал бы его секунды. Повтор дёшев — прочитанное лежит в кэше.
    /// </summary>
    public static Task WarmAsync(IReadOnlyList<FileAttachment>? files, CancellationToken cancellationToken)
    {
        if (files is not { Count: > 0 })
        {
            return Task.CompletedTask;
        }

        var budget = BudgetFor(files.Count);
        return Task.Run(() =>
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = Of(file, budget, warming: true);
            }
        }, cancellationToken);
    }

    private static Digest Of(FileAttachment file, int budget, bool warming = false)
    {
        var known = Cache.GetOrCreateValue(file);
        lock (known)
        {
            if (known.TryGetValue(budget, out var cached))
            {
                return cached;
            }
        }

        // Метка в журнале скорости: место, которое забыло прогреть вложение, видно сразу.
        if (!warming && SynchronizationContext.Current is not null)
        {
            PerfLog.Write($"digest_cold_on_ui {file.FileName}");
        }

        var digest = Read(file, budget);
        lock (known)
        {
            known[budget] = digest;
        }

        return digest;
    }

    private static Digest Read(FileAttachment file, int budget)
    {
        var content = Decode(file.Base64);
        var path = PathOf(file);
        if (content is null)
        {
            return new Digest(path, null);
        }

        // Источник для чтения — всегда содержимое вложения: файл на диске мог измениться после отправки.
        var source = new DocumentSource(Path.GetFileName(file.FileName) is { Length: > 0 } name ? name : "attachment", content);
        var kind = DocumentKinds.Of(source);
        if (kind is DocumentKind.Image or DocumentKind.Binary ||
            (kind == DocumentKind.Pdf && !PdfTextReader.HasText(source)))
        {
            return new Digest(path, null);
        }

        try
        {
            var window = new DocumentWindow(Budget: budget);
            var read = DocumentReader.Read(source, window);
            var text = new StringBuilder()
                .Append(OpenTag).Append("name=\"").Append(Attribute(file.FileName)).Append("\" path=\"").Append(Attribute(path)).Append("\">\n")
                .Append(DocumentReader.Format(path, read, window));
            if (kind == DocumentKind.Word && read.Sections.Any(section => section.More))
            {
                AppendOutline(text, source, read);
            }

            return new Digest(path, text.Append('\n').Append(CloseTag).ToString());
        }
        catch (DocumentException)
        {
            return new Digest(path, null);
        }
    }

    /// <summary>
    /// Заголовки документа после показанной части — чтобы модель знала, где что лежит, и читала
    /// нужный раздел, а не всё подряд.
    /// </summary>
    private static void AppendOutline(StringBuilder text, DocumentSource source, DocumentContent shown)
    {
        var last = shown.Sections.SelectMany(section => section.Units).Select(unit => unit.Number).DefaultIfEmpty(0).Max();
        var whole = DocumentReader.Read(source, new DocumentWindow(Offset: last + 1, Budget: int.MaxValue / 4));
        var headings = whole.Sections
            .SelectMany(section => section.Units)
            .Where(unit => unit.Text.StartsWith('#'))
            .Take(OutlineEntries)
            .ToList();
        if (headings.Count == 0)
        {
            return;
        }

        text.Append("\nOutline of the rest:\n");
        foreach (var heading in headings)
        {
            text.Append('[').Append(heading.Number.ToString(CultureInfo.InvariantCulture)).Append("] ")
                .Append(heading.Text.Length > 120 ? heading.Text[..120] + "…" : heading.Text).Append('\n');
        }
    }

    /// <summary>
    /// Путь для инструментов: исходный файл, если он на месте и совпадает с вложением байт в байт,
    /// иначе ручка вложения из памяти.
    /// </summary>
    /// <remarks>
    /// Сверка по содержимому, а не по размеру: файл того же размера мог оказаться уже другим, и
    /// модель правила бы не тот документ, что ей прислали.
    /// </remarks>
    private static string PathFor(FileAttachment file, byte[]? content)
    {
        if (content is not null && file.SourcePath is { Length: > 0 } source && SameFile(source, content))
        {
            return source;
        }

        return ChatAttachmentRegistry.HandleOf(file);
    }

    private static bool SameFile(string path, byte[] content)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != content.LongLength)
            {
                return false;
            }

            using var stream = info.OpenRead();
            return SHA256.HashData(stream).AsSpan().SequenceEqual(SHA256.HashData(content));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Убирает копии вложений, которые до 1.33.0 ложились во <c>%TEMP%</c> открытым текстом и не
    /// удалялись. Один раз в фоне после запуска; что не удалилось, удалится в следующий раз.
    /// </summary>
    public static void DeleteLegacyCopies()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Amarin Admin AI", "attachments");
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"legacy attachment copies: {ex.Message}");
        }
    }

    private static byte[]? Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Attribute(string value) => value.Replace("\"", "'", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
