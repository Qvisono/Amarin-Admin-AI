using System.Text.RegularExpressions;

namespace Amarin.Tools;

/// <summary>Есть ли папка записи PATH: есть, точно нет или проверить нельзя.</summary>
internal enum PathExistence
{
    Exists,
    Missing,

    /// <summary>Сетевой путь, отключённый диск, неопределённая переменная: «нет» тут не доказано.</summary>
    Unknown
}

/// <summary>Запись PATH с её номером в своей области и тем, что с ней не так.</summary>
internal sealed record PathEntry(EnvironmentScope Scope, int Number, string Raw, string Expanded, IReadOnlyList<string> Problems);

/// <summary>Разбор и правка PATH как списка записей. Без реестра и диска — проверяется тестами.</summary>
/// <remarks>
/// Windows собирает PATH процесса из машинного и за ним пользовательского, поэтому дубль
/// считается по первому появлению в этом порядке: пользовательская запись, повторяющая машинную,
/// ничего не добавляет. Записи сравниваются раскрытыми и без хвостовой косой: <c>%SystemRoot%</c>
/// и <c>C:\Windows\</c> — одна папка.
/// </remarks>
internal static partial class PathAnalysis
{
    /// <summary>Столько знаков оставляет setx: PATH ровно такой длины почти наверняка им обрезан.</summary>
    public const int SetxLimit = 1024;

    public static List<string> Split(string? value) => [.. (value ?? "").Split(';')];

    public static string Join(IEnumerable<string> entries) => string.Join(';', entries);

    /// <summary>Ключ сравнения записей: раскрытая, без кавычек, пробелов по краям и хвостовой косой.</summary>
    public static string Key(string expanded)
    {
        var value = expanded.Trim().Trim('"').Trim().Replace('/', '\\');
        while (value.Length > 3 && value.EndsWith('\\'))
        {
            value = value[..^1];
        }

        return value.ToUpperInvariant();
    }

    public static IReadOnlyList<PathEntry> Analyze(
        string? machine,
        string? user,
        Func<string, string> expand,
        Func<string, PathExistence> exists)
    {
        var first = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<PathEntry>();
        foreach (var (scope, raw) in new[] { (EnvironmentScope.Machine, machine), (EnvironmentScope.User, user) })
        {
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }

            // «;» в самом конце — привычка установщиков, а не пустая запись посреди списка.
            var entries = Split(raw);
            if (entries.Count > 1 && entries[^1].Trim().Length == 0)
            {
                entries.RemoveAt(entries.Count - 1);
            }

            var number = 0;
            foreach (var entry in entries)
            {
                number++;
                var expanded = expand(entry);
                var problems = new List<string>();
                if (entry.Trim().Length == 0)
                {
                    problems.Add("empty entry");
                    result.Add(new PathEntry(scope, number, entry, expanded, problems));
                    continue;
                }

                if (entry.Contains('"'))
                {
                    problems.Add("quoted (some programs do not strip the quotes)");
                }

                foreach (Match match in Variable().Matches(expanded))
                {
                    problems.Add($"undefined variable {match.Value}");
                }

                var path = expanded.Trim().Trim('"');
                if (path.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    problems.Add("network path (lookups wait for the network)");
                }
                else if (!Variable().IsMatch(path) && !Path.IsPathFullyQualified(path))
                {
                    problems.Add("relative path");
                }

                if (exists(expanded) == PathExistence.Missing)
                {
                    problems.Add("folder does not exist");
                }

                var key = Key(expanded);
                var here = $"{Name(scope)} #{number}";
                if (first.TryGetValue(key, out var earlier))
                {
                    problems.Add($"duplicate of {earlier}");
                }
                else
                {
                    first[key] = here;
                }

                result.Add(new PathEntry(scope, number, entry, expanded, problems));
            }
        }

        return result;
    }

    /// <summary>
    /// Что убрать из PATH области: пустые записи, повторы и папки, которых точно нет. Записи, про
    /// которые «нет» не доказано (сеть, отключённый диск), остаются.
    /// </summary>
    /// <param name="machine">Машинный PATH — для пользовательской области: повтор машинной записи лишний.</param>
    public static (string Value, IReadOnlyList<string> Removed) Clean(
        EnvironmentScope scope,
        string? raw,
        string? machine,
        Func<string, string> expand,
        Func<string, PathExistence> exists)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (scope == EnvironmentScope.User && machine is not null)
        {
            foreach (var entry in Split(machine).Where(entry => entry.Trim().Length > 0))
            {
                seen.Add(Key(expand(entry)));
            }
        }

        var kept = new List<string>();
        var removed = new List<string>();
        if (string.IsNullOrEmpty(raw))
        {
            return ("", removed);
        }

        foreach (var entry in Split(raw))
        {
            if (entry.Trim().Length == 0)
            {
                // Лишняя «;»: Windows её пропускает, а часть программ на ней спотыкается.
                removed.Add("(empty)");
                continue;
            }

            var expanded = expand(entry);
            if (!seen.Add(Key(expanded)) || exists(expanded) == PathExistence.Missing)
            {
                removed.Add(entry);
                continue;
            }

            kept.Add(entry);
        }

        return (Join(kept), removed);
    }

    /// <summary>Есть ли такая папка в PATH — раскрытая и без хвостовой косой.</summary>
    public static bool Contains(string? raw, string entry, Func<string, string> expand)
    {
        var key = Key(expand(entry));
        return Split(raw).Any(item => item.Trim().Length > 0 && Key(expand(item)) == key);
    }

    public static string Add(string? raw, string entry, bool atStart)
    {
        var entries = Split(raw).Where(item => item.Trim().Length > 0).ToList();
        if (atStart)
        {
            entries.Insert(0, entry);
        }
        else
        {
            entries.Add(entry);
        }

        return Join(entries);
    }

    /// <summary>Убирает все записи с этой папкой; пустые записи по дороге не трогает.</summary>
    public static (string Value, int Removed) Remove(string? raw, string entry, Func<string, string> expand)
    {
        var key = Key(expand(entry));
        var entries = Split(raw);
        var kept = entries.Where(item => item.Trim().Length == 0 || Key(expand(item)) != key).ToList();
        return (Join(kept), entries.Count - kept.Count);
    }

    /// <summary>Где папка записи: есть, нет или проверить нельзя — без догадок о том, чего не видно.</summary>
    public static PathExistence Probe(string expanded)
    {
        var path = expanded.Trim().Trim('"');
        if (path.Length == 0 || Variable().IsMatch(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path))
        {
            return PathExistence.Unknown;
        }

        try
        {
            if (Directory.Exists(path))
            {
                return PathExistence.Exists;
            }

            // Нет самого диска (флешка вынута) — «папки нет» не доказано.
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).IsReady ? PathExistence.Missing : PathExistence.Unknown;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return PathExistence.Unknown;
        }
    }

    public static string Name(EnvironmentScope scope) => scope == EnvironmentScope.User ? "user" : "machine";

    [GeneratedRegex("%[^%;]+%")]
    private static partial Regex Variable();
}
