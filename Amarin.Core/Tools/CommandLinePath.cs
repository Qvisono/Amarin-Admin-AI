namespace Amarin.Tools;

/// <summary>
/// Какой файл на самом деле запускает командная строка автозапуска (1.37.0, аудит автозапуска).
/// </summary>
/// <remarks>
/// <para>
/// Командные строки служб, задач и Run-ключей пишут как придётся: в кавычках и без, с пробелами в
/// пути без кавычек (<c>C:\Program Files\App\app.exe -k</c>), с переменными окружения, в виде
/// <c>\SystemRoot\…</c> и <c>\??\C:\…</c> у служб, без расширения и без папки. Разбор идёт
/// так же, как ищет файл сама Windows: путь без кавычек растёт по словам, пока не найдётся файл.
/// </para>
/// <para>
/// Если запускает хост — <c>rundll32</c>, <c>regsvr32</c>, оболочка или сценарный движок, — то
/// важен не хост (он подписан Microsoft), а то, что он грузит: проверяется первый аргумент,
/// оказавшийся существующим файлом.
/// </para>
/// </remarks>
internal static class CommandLinePath
{
    /// <summary>Программы, которые запускают чужой файл из своих аргументов.</summary>
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "rundll32", "regsvr32", "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta"
    };

    private static readonly string[] Extensions = [".exe", ".com", ".bat", ".cmd"];

    /// <summary>
    /// Файл, который запускает команда, или null — команду не разобрать. Найденного пути может не
    /// быть на диске: путь всё равно возвращается, и отсутствие файла — уже вывод аудита.
    /// </summary>
    public static string? Resolve(string? command, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var text = Normalize(command);
        if (text.Length == 0)
        {
            return null;
        }

        var (program, rest) = SplitProgram(text, exists);
        if (program is null)
        {
            return null;
        }

        if (Hosts.Contains(Path.GetFileNameWithoutExtension(program)) &&
            FirstFileArgument(rest, exists) is { } loaded)
        {
            return loaded;
        }

        return program;
    }

    /// <summary>Переменные окружения и служебные приставки путей служб.</summary>
    internal static string Normalize(string? command)
    {
        var text = Environment.ExpandEnvironmentVariables(command ?? "").Trim();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (text.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            text = Path.Combine(windows, text[@"\SystemRoot\".Length..]);
        }
        else if (text.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
        {
            text = Path.Combine(windows, text);
        }

        return text;
    }

    /// <summary>Сама программа и остаток строки.</summary>
    private static (string? Program, string Tail) SplitProgram(string text, Func<string, bool> exists)
    {
        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            var quoted = close < 0 ? text[1..] : text[1..close];
            var rest = close < 0 ? "" : text[(close + 1)..];
            return (Locate(quoted, exists) ?? quoted, rest);
        }

        // Путь без кавычек с пробелами: Windows пробует «C:\Program», потом «C:\Program Files\App\app.exe»…
        var words = text.Split(' ');
        for (var count = 1; count <= words.Length; count++)
        {
            var candidate = string.Join(' ', words, 0, count).TrimEnd(',');
            if (Locate(candidate, exists) is { } found)
            {
                return (found, string.Join(' ', words, count, words.Length - count));
            }
        }

        // Ничего не нашлось: путь первым словом — файла нет, но где он должен был быть, видно.
        var first = words[0].TrimEnd(',');
        return (first.Length == 0 ? null : first, string.Join(' ', words.Skip(1)));
    }

    /// <summary>Файл по имени: как есть, с расширением, а без папки — в System32 и папке Windows.</summary>
    private static string? Locate(string candidate, Func<string, bool> exists)
    {
        var name = candidate.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }

        var folders = Path.IsPathRooted(name)
            ? [""]
            : new[]
            {
                Environment.SystemDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            };

        foreach (var folder in folders)
        {
            var path = folder.Length == 0 ? name : Path.Combine(folder, name);
            if (exists(path))
            {
                return path;
            }

            if (!Path.HasExtension(path))
            {
                foreach (var extension in Extensions)
                {
                    if (exists(path + extension))
                    {
                        return path + extension;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Первый аргумент хоста, который оказался файлом: у <c>rundll32</c> — до запятой.</summary>
    private static string? FirstFileArgument(string rest, Func<string, bool> exists)
    {
        foreach (var token in Tokens(rest))
        {
            var value = token.Split(',')[0].Trim();
            if (value.Length == 0 || value[0] is '/' or '-')
            {
                continue;
            }

            if (Path.IsPathRooted(value) && Locate(value, exists) is { } file)
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>Аргументы с учётом кавычек.</summary>
    private static IEnumerable<string> Tokens(string text)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (ch == ' ' && !quoted)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
