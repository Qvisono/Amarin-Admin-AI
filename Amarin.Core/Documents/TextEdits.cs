using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Amarin.Core;

/// <summary>Итог точной замены: сколько заменено и строки вокруг первой замены — уже с номерами.</summary>
internal sealed record TextEditResult(int Count, string Snippet, int FirstLine);

/// <summary>
/// Точная замена в текстовом файле — та правка, которой Claude Code меняет код: фрагмент, который
/// встречается ровно один раз, на новый.
/// </summary>
/// <remarks>
/// <para>
/// Почему не «перепиши файл целиком»: модель, переписывающая длинный файл, теряет куски и
/// возвращает старые версии из своей памяти, а точная замена трогает ровно то, что названо, и
/// неоднозначный фрагмент отвергает — пусть модель возьмёт кусок пошире.
/// </para>
/// <para>
/// Кодировка, метка порядка байтов и переводы строк файла сохраняются: модель пишет <c>\n</c>, а
/// файл Windows — <c>\r\n</c>, и замена не должна перекраивать весь файл ради одной строки.
/// Номера строк, по ошибке скопированные из <c>read_file</c>, снимаются перед поиском.
/// </para>
/// </remarks>
internal static partial class TextEdits
{
    private const int SnippetContext = 3;

    /// <summary>Больше этого файл правкой не открывается: это уже не текст, который правят руками.</summary>
    internal const long MaxBytes = 20L * 1024 * 1024;

    public static TextEditResult Replace(string path, string oldText, string newText, bool all)
    {
        if (string.IsNullOrEmpty(oldText))
        {
            throw new DocumentException("old_string is empty. To create a file use write_file; to add text, replace a nearby fragment with itself plus the new lines.");
        }

        if (oldText == newText)
        {
            throw new DocumentException("old_string and new_string are the same - nothing would change.");
        }

        if (new FileInfo(path).Length > MaxBytes)
        {
            throw new DocumentException($"The file is larger than {MaxBytes / (1024 * 1024)} MB and is not edited here. Hand the job to the agent.");
        }

        var bytes = File.ReadAllBytes(path);
        var (text, encoding) = TextFileReader.Decode(bytes);
        var (find, replacement, count) = Locate(text, oldText, newText);

        if (count == 0)
        {
            throw new DocumentException(
                "old_string was not found in the file. Read the file again with read_file and copy the fragment exactly as it is, without the line numbers.");
        }

        if (count > 1 && !all)
        {
            throw new DocumentException(
                $"old_string occurs {count} times. Include more surrounding lines so it occurs once, or set replace_all=true to change every occurrence.");
        }

        var first = text.IndexOf(find, StringComparison.Ordinal);
        var output = all ? text.Replace(find, replacement, StringComparison.Ordinal) : text[..first] + replacement + text[(first + find.Length)..];
        var encoded = encoding.GetBytes(output);

        // Файл в кодировке Windows не хранит то, чего нет в его кодовой странице: вместо буквы на
        // диск легли бы вопросительные знаки, и человек узнал бы об этом, открыв файл.
        if (!string.Equals(encoding.GetString(encoded), output, StringComparison.Ordinal))
        {
            throw new DocumentException(
                $"The file is saved in the {encoding.WebName} encoding, which cannot store some characters of new_string. Use only characters that encoding has, or ask the user whether to convert the file to UTF-8.");
        }

        var preamble = bytes.AsSpan().StartsWith(encoding.GetPreamble()) ? encoding.GetPreamble() : [];
        DocumentFiles.WriteAtomically(path, stream =>
        {
            stream.Write(preamble);
            stream.Write(encoded);
        }, overwrite: true);

        var updated = output.Replace("\r\n", "\n", StringComparison.Ordinal);
        var firstLine = text[..first].Count(c => c == '\n') + 1;
        var lastLine = firstLine + replacement.Count(c => c == '\n');
        return new TextEditResult(all ? count : 1, Snippet(updated, firstLine - SnippetContext, lastLine + SnippetContext), firstLine);
    }

    /// <summary>
    /// Фрагмент так, как он лежит в файле, и замена с теми же переводами строк.
    /// </summary>
    /// <remarks>
    /// Модель пишет <c>\n</c>, файл Windows хранит <c>\r\n</c>, а бывают и файлы вперемешку. Весь
    /// файл к одному виду не приводится — тогда правка одной строки переписала бы концы всех строк,
    /// и в истории версий человек увидел бы изменённым весь файл. Ищется сам фрагмент в обоих видах,
    /// а замена берёт вид найденного; без переводов строк во фрагменте — вид, которого в файле больше.
    /// </remarks>
    private static (string Find, string Replacement, int Count) Locate(string text, string oldText, string newText)
    {
        foreach (var (find, replacement) in Candidates(oldText, newText))
        {
            foreach (var form in (string[])["\n", "\r\n"])
            {
                var shaped = Shape(find, form);
                var count = Count(text, shaped);
                if (count > 0)
                {
                    var style = shaped.Contains('\n', StringComparison.Ordinal) ? form : Dominant(text);
                    return (shaped, Shape(replacement, style), count);
                }
            }
        }

        return (oldText, newText, 0);
    }

    /// <summary>Как прислано — и, если не нашлось, без номеров строк, скопированных из чтения.</summary>
    private static IEnumerable<(string Find, string Replacement)> Candidates(string oldText, string newText)
    {
        yield return (oldText, newText);
        var find = oldText.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (WithoutLineNumbers(find) is { } stripped)
        {
            yield return (stripped, WithoutLineNumbers(newText.Replace("\r\n", "\n", StringComparison.Ordinal)) ?? newText);
        }
    }

    private static string Shape(string text, string newline) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal);

    /// <summary>Каких переводов строк в файле больше: <c>\r\n</c> или <c>\n</c>.</summary>
    private static string Dominant(string text)
    {
        var windows = Count(text, "\r\n");
        var unix = text.Count(c => c == '\n') - windows;
        return windows > unix ? "\r\n" : "\n";
    }

    private static int Count(string text, string find)
    {
        var count = 0;
        for (var at = text.IndexOf(find, StringComparison.Ordinal); at >= 0; at = text.IndexOf(find, at + find.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>Строки файла с номерами — тот же вид, что у <c>read_file</c>.</summary>
    public static string Snippet(string text, int from, int to)
    {
        var lines = text.Split('\n');
        var start = Math.Max(1, from);
        var end = Math.Min(lines.Length, to);
        var snippet = new StringBuilder();
        for (var number = start; number <= end; number++)
        {
            snippet.Append(number.ToString(CultureInfo.InvariantCulture).PadLeft(6)).Append('\t').Append(lines[number - 1]).Append('\n');
        }

        return snippet.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Тот же фрагмент без номеров строк, если каждая его строка начинается с номера и табуляции,
    /// как в выводе <c>read_file</c>; иначе null.
    /// </summary>
    internal static string? WithoutLineNumbers(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length == 0 || !lines.All(line => line.Length == 0 || NumberedLine().IsMatch(line)))
        {
            return null;
        }

        return string.Join("\n", lines.Select(line => NumberedLine().Replace(line, "", 1)));
    }

    [GeneratedRegex(@"^\s*\d+\t")]
    private static partial Regex NumberedLine();
}
