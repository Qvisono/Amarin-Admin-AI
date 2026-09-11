namespace Amarin.Core;

/// <summary>
/// Какие документы имеет смысл отправлять модели и под каким MIME.
/// </summary>
/// <remarks>
/// Список повторяет то, что Venice умеет разбирать сам: PDF, EPUB, офисные документы, таблицы,
/// текст и большинство исходников. Картинки сюда не входят — у них свой путь через
/// <see cref="Tools.ImageHelpers"/> и содержимое <c>image_url</c>, потому что модель смотрит
/// на них, а не читает. Флага возможности у моделей (аналога <c>supportsVision</c>) в каталоге
/// Venice нет, поэтому предупреждать «эта модель не умеет файлы» не по чему.
/// </remarks>
internal static class AttachmentTypes
{
    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".epub"] = "application/epub+zip",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".xls"] = "application/vnd.ms-excel",
        [".csv"] = "text/csv",
        [".tsv"] = "text/tab-separated-values",
        [".txt"] = "text/plain",
        [".log"] = "text/plain",
        [".md"] = "text/markdown",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".yaml"] = "application/yaml",
        [".yml"] = "application/yaml",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".css"] = "text/css",
        [".py"] = "text/x-python",
        [".js"] = "text/javascript",
        [".ts"] = "text/x-typescript",
        [".tsx"] = "text/x-typescript",
        [".jsx"] = "text/javascript",
        [".cs"] = "text/x-csharp",
        [".c"] = "text/x-c",
        [".h"] = "text/x-c",
        [".cpp"] = "text/x-c++",
        [".hpp"] = "text/x-c++",
        [".java"] = "text/x-java",
        [".go"] = "text/x-go",
        [".rs"] = "text/x-rust",
        [".rb"] = "text/x-ruby",
        [".php"] = "text/x-php",
        [".swift"] = "text/x-swift",
        [".kt"] = "text/x-kotlin",
        [".sql"] = "application/sql",
        [".sh"] = "application/x-sh",
        [".ps1"] = "application/x-powershell",
        [".bat"] = "text/plain",
        [".cmd"] = "text/plain",
        [".ini"] = "text/plain",
        [".toml"] = "application/toml",
        [".conf"] = "text/plain",
        [".reg"] = "text/plain"
    };

    /// <summary>Документ, который Venice разберёт. Картинки сюда не попадают.</summary>
    public static bool IsSupportedDocument(string path) =>
        MimeByExtension.ContainsKey(Path.GetExtension(path));

    public static string GuessMimeType(string path) =>
        MimeByExtension.TryGetValue(Path.GetExtension(path), out var mime)
            ? mime
            : "application/octet-stream";

    /// <summary>Расширение в том виде, в каком его показывают человеку в отказе.</summary>
    public static string DescribeExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension)
            ? Loc.Get("S.Size.NoExtension")
            : extension.ToLowerInvariant();
    }

    /// <summary>«2,4 МБ» — так размер читается в карточке и в предупреждении.</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => Loc.Format("S.Size.Bytes", bytes),
        < 1024 * 1024 => Loc.Format("S.Size.Kilobytes", $"{bytes / 1024.0:0.#}"),
        _ => Loc.Format("S.Size.Megabytes", $"{bytes / (1024.0 * 1024.0):0.#}")
    };
}
