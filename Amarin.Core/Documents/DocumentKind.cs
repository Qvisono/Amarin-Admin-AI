namespace Amarin.Core;

/// <summary>Что за файл — от этого зависит, как его читать и чем править.</summary>
internal enum DocumentKind
{
    /// <summary>Текст и код: читается строками с номерами, правится точной заменой.</summary>
    Text,

    /// <summary>Word (.docx): абзацы и таблицы.</summary>
    Word,

    /// <summary>Excel (.xlsx): листы, строки и адреса ячеек.</summary>
    Excel,

    /// <summary>PowerPoint (.pptx): слайды и заметки к ним.</summary>
    Slides,

    /// <summary>PDF: страницы текста.</summary>
    Pdf,

    /// <summary>Картинка: её показывают модели, а не читают.</summary>
    Image,

    /// <summary>Двоичный файл, который прочитать нечем: программа, архив, старый .doc.</summary>
    Binary
}

/// <summary>Вид файла по расширению и, для неизвестных расширений, по первым байтам.</summary>
internal static class DocumentKinds
{
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".msi", ".msix", ".appx", ".cab", ".zip", ".7z", ".rar", ".gz", ".tar", ".iso",
        ".img", ".vhd", ".vhdx", ".bin", ".dat", ".db", ".sqlite", ".mdb", ".accdb", ".pst", ".ost",
        ".doc", ".xls", ".ppt", ".odt", ".ods", ".odp", ".mp3", ".mp4", ".mkv", ".avi", ".mov", ".wav",
        ".flac", ".ogg", ".m4a", ".wma", ".wmv", ".ttf", ".otf", ".woff", ".woff2", ".pdb", ".obj", ".lib"
    };

    public static DocumentKind Of(string path) => Of(DocumentSource.File(path));

    /// <summary>Вид документа по расширению, а без известного расширения — по первым байтам.</summary>
    public static DocumentKind Of(DocumentSource source)
    {
        var path = source.Path;
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".docx" or ".docm" or ".dotx" or ".dotm":
                return DocumentKind.Word;
            case ".xlsx" or ".xlsm" or ".xltx" or ".xltm":
                return DocumentKind.Excel;
            case ".pptx" or ".pptm" or ".potx":
                return DocumentKind.Slides;
            case ".pdf":
                return DocumentKind.Pdf;
            case ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" or ".ico":
                return DocumentKind.Image;
        }

        if (BinaryExtensions.Contains(Path.GetExtension(path)))
        {
            return DocumentKind.Binary;
        }

        return LooksBinary(source) ? DocumentKind.Binary : DocumentKind.Text;
    }

    /// <summary>
    /// Нулевой байт в начале файла — верный признак двоичного содержимого: в тексте UTF-8 и
    /// в однобайтовых кодировках его не бывает, а UTF-16 узнаётся раньше, по метке порядка байтов.
    /// </summary>
    private static bool LooksBinary(DocumentSource source)
    {
        try
        {
            using var stream = source.OpenRead();
            Span<byte> head = stackalloc byte[4096];
            var read = stream.Read(head);
            head = head[..read];
            if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            {
                return false;
            }

            return head.IndexOf((byte)0) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Пишется ли этот вид текстом: такие файлы создаёт и правит <c>write_file</c>.</summary>
    public static bool IsPlainText(string path) => Of(path) == DocumentKind.Text;

    /// <summary>Как назвать вид человеку и модели в заголовке чтения.</summary>
    public static string Label(DocumentKind kind) => kind switch
    {
        DocumentKind.Word => "Word",
        DocumentKind.Excel => "Excel",
        DocumentKind.Slides => "PowerPoint",
        DocumentKind.Pdf => "PDF",
        DocumentKind.Image => "image",
        DocumentKind.Binary => "binary",
        _ => "text"
    };
}
