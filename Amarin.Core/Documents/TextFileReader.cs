using System.Text;

namespace Amarin.Core;

/// <summary>Текстовый файл строками — с той кодировкой, в которой он записан.</summary>
/// <remarks>
/// Прежнее чтение брало UTF-8 вслепую, и файл в Windows-1251 (старые .txt, .bat, логи
/// русскоязычных программ) приходил модели вопросительными знаками. Порядок проверки: метка
/// порядка байтов, затем строгий UTF-8, и только если он не сходится — кодовая страница ANSI
/// этой машины.
/// </remarks>
internal static class TextFileReader
{
    /// <summary>Потолок чтения: файл больше — не текст, который читают глазами, а выгрузка.</summary>
    public const long MaxBytes = 32L * 1024 * 1024;

    static TextFileReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <remarks>
    /// Длинная строка (минифицированный код, выгрузка) не обрезается здесь: окно показывает её
    /// кусками (<see cref="DocumentWindow.Part"/>), и до хвоста можно дочитать.
    /// </remarks>
    public static DocumentContent Read(DocumentSource source, DocumentWindow window)
    {
        var (text, encoding) = ReadText(source);
        var lines = SplitLines(text);
        var units = new List<DocumentUnit>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            units.Add(new DocumentUnit(i + 1, lines[i]));
        }

        // Номер строки с табуляцией занимает восемь знаков — их тоже считает бюджет.
        return new DocumentContent
        {
            Kind = DocumentKind.Text,
            Unit = "line",
            Sections = [UnitBudget.Slice(units, window, unit => unit.Text.Length + 8)],
            Summary = $"{units.Count} lines, {encoding.WebName}"
        };
    }

    /// <summary>Весь текст файла и кодировка, в которой он прочитан.</summary>
    public static (string Text, Encoding Encoding) ReadText(DocumentSource source)
    {
        var length = source.Content?.LongLength ?? new FileInfo(source.Path).Length;
        if (length > MaxBytes)
        {
            throw new DocumentException(
                $"The file is {length / (1024 * 1024)} MB - too large to read as text (limit {MaxBytes / (1024 * 1024)} MB).");
        }

        return Decode(source.ReadAllBytes());
    }

    public static (string Text, Encoding Encoding) Decode(byte[] bytes)
    {
        var bom = Bom(bytes);
        if (bom is { } marked)
        {
            return (marked.Encoding.GetString(bytes, marked.Length, bytes.Length - marked.Length), marked.Encoding);
        }

        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return (strict.GetString(bytes), new UTF8Encoding(false));
        }
        catch (DecoderFallbackException)
        {
            var ansi = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            return (ansi.GetString(bytes), ansi);
        }
    }

    /// <summary>Строки без переводов строки; последняя пустая (файл кончается переводом) не считается.</summary>
    public static List<string> SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static (Encoding Encoding, int Length)? Bom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Encoding.Unicode, 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode, 2);
        }

        return null;
    }
}
