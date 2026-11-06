using System.Text;
using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>
/// Блок <c>```file</c>: модель называет файл, а его текст подставляет программа.
/// </summary>
/// <remarks>
/// <para>
/// Модель перепечатывала файлы в ответах: блоки по двадцать тысяч знаков стоили дороже всего
/// остального вывода и ехали потом в истории каждого запроса. Читать файл ей не нужно второй раз,
/// а печатать его незачем: человеку покажет программа.
/// </para>
/// <para>
/// Подставленный текст ложится в <see cref="ChatDisplayMessage.Text"/> обычным блоком кода, а в
/// <see cref="ChatSession.ApiMessages"/> остаётся короткая запись. Поэтому лента, экспорт,
/// «Поделиться» и поиск о блоках ничего не знают, а старый ответ показывает файл таким, каким он
/// был в момент ответа, что бы с файлом ни стало потом.
/// </para>
/// <para>
/// Показать можно только то, что модель сама открывала в этом чате, и с запретом секретов, как у
/// <c>read_file</c>: текст чужой страницы мог бы попросить её вывести в чат любой файл с диска.
/// </para>
/// </remarks>
internal static class FileBlocks
{
    public const string FenceLanguage = "file";

    /// <summary>Сколько знаков подставлять: подсветка кода идёт на потоке окна.</summary>
    internal const int MaxChars = 100_000;

    /// <summary>Инструменты, вызов которых в истории значит, что модель этот файл видела.</summary>
    private static readonly HashSet<string> FileTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "write_file", "edit_file"
    };

    /// <summary>Что модель попросила показать: файл и, если нужна часть, строки.</summary>
    private sealed record FileRequest(string Path, int? First, int? Last);

    /// <summary>Текст ответа с подставленными файлами. Без блоков возвращается как есть.</summary>
    /// <param name="known">Открывала ли модель этот файл в этом чате.</param>
    public static string Expand(string text, Func<string, bool> known)
    {
        var lines = text.Split('\n');
        var output = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!TryOpen(lines[i], out var marker, out var length, out var info))
            {
                output.Add(lines[i]);
                continue;
            }

            // Обход всех блоков, а не поиск своего: запись внутри чужого блока кода — это пример, а
            // незакрытый блок — оборванный ответ, и обоим положено остаться как есть.
            var close = ClosingLine(lines, i + 1, marker, length);
            if (close < 0)
            {
                output.AddRange(lines[i..]);
                break;
            }

            if (info.Equals(FenceLanguage, StringComparison.OrdinalIgnoreCase) && Parse(lines[(i + 1)..close]) is { } request)
            {
                output.Add(Show(request, known));
            }
            else
            {
                output.AddRange(lines[i..(close + 1)]);
            }

            i = close;
        }

        return string.Join('\n', output);
    }

    /// <summary>
    /// Есть ли вызов файлового инструмента с этим путём в истории: штампы
    /// <see cref="FileToolState"/> живут в памяти, а чат продолжают и после перезапуска.
    /// </summary>
    public static bool InHistory(IReadOnlyList<ChatMessage> history, string path)
    {
        foreach (var message in history)
        {
            foreach (var call in message.ToolCalls ?? [])
            {
                if (FileTools.Contains(call.Function.Name) && Names(call, path))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Names(ToolCall call, string path)
    {
        try
        {
            var named = FileToolPaths.String(ToolArguments.Parse(call.Function.Arguments), "path");
            return FileToolPaths.TryResolve(named, out var resolved, out _) &&
                   string.Equals(resolved, path, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryOpen(string line, out char marker, out int length, out string info)
    {
        var trimmed = line.Trim();
        marker = trimmed.Length > 0 ? trimmed[0] : '\0';
        length = 0;
        info = "";
        if (marker is not ('`' or '~'))
        {
            return false;
        }

        while (length < trimmed.Length && trimmed[length] == marker)
        {
            length++;
        }

        info = trimmed[length..].Trim();
        return length >= 3 && !(marker == '`' && info.Contains('`', StringComparison.Ordinal));
    }

    private static int ClosingLine(string[] lines, int from, char marker, int length)
    {
        for (var i = from; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length >= length && trimmed.All(symbol => symbol == marker))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Запись модели. <c>null</c> — пути нет, и это не запись, а обычный блок кода.</summary>
    private static FileRequest? Parse(string[] body)
    {
        string? path = null;
        int? first = null;
        int? last = null;
        foreach (var line in body)
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var value = line[(colon + 1)..].Trim();
            switch (line[..colon].Trim().ToLowerInvariant())
            {
                case "path":
                    path = value;
                    break;
                case "lines":
                    (first, last) = Range(value);
                    break;
            }
        }

        return string.IsNullOrWhiteSpace(path) ? null : new FileRequest(path, first, last);
    }

    private static (int? First, int? Last) Range(string value)
    {
        var parts = value.Split('-', StringSplitOptions.TrimEntries);
        if (!int.TryParse(parts[0], out var first))
        {
            return (null, null);
        }

        return parts.Length > 1 && int.TryParse(parts[1], out var last) ? (first, last) : (first, first);
    }

    private static string Show(FileRequest request, Func<string, bool> known)
    {
        if (!FileToolPaths.TryResolve(request.Path, out var path, out _) || !File.Exists(path))
        {
            return Refusal("S.FileBlock.Missing", request.Path);
        }

        if (SensitivePaths.IsSensitive(path, out _))
        {
            return Refusal("S.FileBlock.Forbidden", path);
        }

        if (!known(path))
        {
            return Refusal("S.FileBlock.Unknown", path);
        }

        if (DocumentKinds.Of(path) != DocumentKind.Text)
        {
            return Refusal("S.FileBlock.NotText", path);
        }

        try
        {
            var (text, _) = TextFileReader.ReadText(DocumentSource.File(path));
            return Block(path, TextFileReader.SplitLines(text), request);
        }
        catch (Exception exception) when (exception is DocumentException or IOException or UnauthorizedAccessException)
        {
            return Refusal("S.FileBlock.Failed", path);
        }
    }

    private static string Block(string path, List<string> lines, FileRequest request)
    {
        var first = Math.Max(request.First ?? 1, 1);
        var last = Math.Min(request.Last ?? lines.Count, lines.Count);
        if (first > last)
        {
            return Refusal("S.FileBlock.Failed", path);
        }

        var body = string.Join('\n', lines.Skip(first - 1).Take(last - first + 1));
        var cut = body.Length > MaxChars;
        if (cut)
        {
            var end = body.LastIndexOf('\n', MaxChars);
            body = body[..(end > 0 ? end : MaxChars)];
        }

        // Файл сам может содержать ограждение, и короткое закрылось бы посреди него.
        var fence = new string('`', Math.Max(3, LongestTicks(body) + 1));
        var language = CodeLanguages.Normalize(Path.GetExtension(path).TrimStart('.'));
        var block = new StringBuilder(body.Length + 32)
            .Append(fence).Append(language).Append('\n').Append(body).Append('\n').Append(fence);
        if (cut)
        {
            block.Append("\n\n").Append(Loc.Format("S.FileBlock.Cut", MaxChars));
        }

        return block.ToString();
    }

    private static int LongestTicks(string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var symbol in text)
        {
            run = symbol == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    /// <summary>Строка человеку вместо блока; путь — кодом, иначе разметка съела бы обратные косые.</summary>
    private static string Refusal(string key, string path) => Loc.Format(key, "`" + path + "`");
}
