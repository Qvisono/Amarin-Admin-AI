using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Text;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Пути, которые инструменты не читают и в которые не пишут, что бы ни попросила модель.
/// </summary>
/// <remarks>
/// <para>
/// Чтение: файлы, в которых лежат чужие секреты — пароли и куки браузеров, ключи SSH и GnuPG,
/// хранилища Windows и менеджеров паролей, ключи самой программы, кусты реестра SAM/SECURITY.
/// Модель читает то, что ей велит текст со страницы, а тот же агент отправляет данные в сеть:
/// пересказать такой файл в ответ — уже утечка.
/// </para>
/// <para>
/// Запись: папка данных программы и папка снимков для отката. Запись в <c>settings.json</c>
/// переключила бы режим подтверждений, в <c>instructions/</c> и <c>chats/</c> — подложила бы
/// указания будущим чатам, в снимок — испортила бы откат.
/// </para>
/// <para>
/// Путь сравнивается в каноническом виде: без <c>\\?\</c>, без хвостовых точек и пробелов у
/// частей (Windows их отбрасывает, и <c>"Login Data."</c> открывает тот же файл), без
/// альтернативного потока (<c>:$DATA</c>), с раскрытыми короткими именами 8.3 и ссылками,
/// когда путь существует.
/// </para>
/// </remarks>
internal static class SensitivePaths
{
    private static readonly FrozenSet<string> SecretFileNames = new[]
    {
        "login data", "login data for account", "cookies", "local state", "web data",
        "key3.db", "key4.db", "logins.json", "cookies.sqlite", "cert9.db", "signons.sqlite",
        "ntds.dit"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> SecretFolders = new[] { ".ssh", ".gnupg" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> VaultExtensions = new[] { ".kdbx", ".kdb", ".psafe3" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> HiveNames = new[] { "sam", "security", "system" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Корень данных программы. Подменяется в тестах.</summary>
    internal static Func<string> ProgramDataRoot { get; set; } = () => AppPaths.Root;

    /// <summary>Папка снимков для отката. Подменяется в тестах.</summary>
    internal static Func<string> SnapshotsRoot { get; set; } = () => ChangeRollbackStore.Root;

    /// <summary>
    /// Путь указывает на секрет, который читать нельзя. <paramref name="reason"/> — текст отказа
    /// для модели.
    /// </summary>
    public static bool IsSensitive(string? path, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var candidate in Candidates(path))
        {
            if (Classify(candidate) is { } what)
            {
                reason = Refusal(what);
                return true;
            }
        }

        return false;
    }

    /// <summary>Путь лежит в папке данных программы или в папке её снимков.</summary>
    public static bool IsProgramData(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var roots = new[] { ProgramDataRoot(), SnapshotsRoot() }
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Canonical(root).TrimEnd('\\'))
            .ToArray();

        foreach (var candidate in Candidates(path))
        {
            foreach (var root in roots)
            {
                if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Раздел HKLM, где Windows держит учётные записи и секреты LSA: <c>SAM</c>, <c>SECURITY</c>.
    /// </summary>
    public static bool IsSecretHive(string? subKey)
    {
        var first = (subKey ?? "").Trim().TrimStart('\\').Split('\\', 2)[0];
        return first.Equals("SAM", StringComparison.OrdinalIgnoreCase) ||
               first.Equals("SECURITY", StringComparison.OrdinalIgnoreCase);
    }

    public const string SecretHiveRefusal =
        "ЗАПРЕЩЕНО: разделы HKLM\\SAM и HKLM\\SECURITY хранят учётные записи и секреты Windows. " +
        "Инструменты их не читают и не меняют. Не повторяй попытку ни этим, ни другим инструментом.";

    /// <summary>Отказ модели на запись в данные программы.</summary>
    public static string ProgramDataRefusal(string path) =>
        $"ЗАПРЕЩЕНО: {path} лежит в папке данных самой программы (настройки, ключи, переписки, " +
        "инструкции, снимки для отката). Инструменты туда не пишут: такая запись меняла бы " +
        "поведение программы в обход человека. Не повторяй попытку; если нужно сохранить файл — " +
        "сохрани его в «Загрузки» или на «Рабочий стол».";

    /// <summary>
    /// Путь в каноническом виде: обратные косые, без <c>\\?\</c>, без <c>.</c> и <c>..</c>,
    /// без хвостовых точек и пробелов у частей и без альтернативного потока.
    /// </summary>
    internal static string Canonical(string path)
    {
        var value = path.Trim().Trim('"').Replace('/', '\\');
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            value = @"\\" + value[8..];
        }
        else if (value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                 value.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            value = value[4..];
        }

        var unc = value.StartsWith(@"\\", StringComparison.Ordinal);
        var parts = value.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];

            // Поток есть у любой части, кроме буквы диска: «file.txt:$DATA», «file:hidden».
            var colon = part.IndexOf(':');
            if (colon >= 0 && !(i == 0 && colon == 1))
            {
                part = part[..colon];
            }

            part = part.TrimEnd('.', ' ');
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (kept.Count > 1)
                {
                    kept.RemoveAt(kept.Count - 1);
                }

                continue;
            }

            kept.Add(part);
        }

        var joined = string.Join('\\', kept);
        return unc ? @"\\" + joined : joined;
    }

    /// <summary>
    /// Варианты пути для проверки: как написан и, если он существует, с раскрытыми короткими
    /// именами и ссылками — запрет не должен обходиться через «C:\Users\ADMINI~1\.ssh».
    /// </summary>
    private static IEnumerable<string> Candidates(string path)
    {
        var canonical = Canonical(path);
        yield return canonical;

        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        string? full;
        try
        {
            full = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            yield break;
        }

        var longName = LongPath(full);
        if (longName is not null)
        {
            var expanded = Canonical(longName);
            if (!expanded.Equals(canonical, StringComparison.OrdinalIgnoreCase))
            {
                yield return expanded;
            }
        }

        var target = LinkTarget(full);
        if (target is not null)
        {
            yield return Canonical(target);
        }
    }

    private static string? Classify(string canonical)
    {
        var parts = canonical.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var name = parts[^1];
        if (SecretFileNames.Contains(name))
        {
            return name.Equals("ntds.dit", StringComparison.OrdinalIgnoreCase) ? "hive" : "browser";
        }

        if (VaultExtensions.Contains(Path.GetExtension(name)))
        {
            return "vault";
        }

        for (var i = 0; i < parts.Length; i++)
        {
            if (SecretFolders.Contains(parts[i]))
            {
                return "keys";
            }

            if (i + 1 < parts.Length &&
                parts[i].Equals("microsoft", StringComparison.OrdinalIgnoreCase) &&
                parts[i + 1] is var next &&
                (next.Equals("credentials", StringComparison.OrdinalIgnoreCase) ||
                 next.Equals("protect", StringComparison.OrdinalIgnoreCase) ||
                 next.Equals("vault", StringComparison.OrdinalIgnoreCase)))
            {
                return "windows-vault";
            }

            if (i + 2 < parts.Length &&
                parts[i].Equals("appdata", StringComparison.OrdinalIgnoreCase) &&
                parts[i + 2].Equals("bitwarden", StringComparison.OrdinalIgnoreCase))
            {
                return "vault";
            }
        }

        // Кусты реестра: по имени файла внутри System32\config (и RegBack) и \repair.
        var hive = Path.GetFileNameWithoutExtension(name);
        if (HiveNames.Contains(hive) || HiveNames.Contains(name))
        {
            var folder = parts.Length >= 2 ? parts[^2] : "";
            var above = parts.Length >= 3 ? parts[^3] : "";
            if ((folder.Equals("config", StringComparison.OrdinalIgnoreCase) &&
                 above.Equals("system32", StringComparison.OrdinalIgnoreCase)) ||
                (folder.Equals("regback", StringComparison.OrdinalIgnoreCase) &&
                 above.Equals("config", StringComparison.OrdinalIgnoreCase)) ||
                folder.Equals("repair", StringComparison.OrdinalIgnoreCase))
            {
                return "hive";
            }
        }

        if (name.Equals("keys.json", StringComparison.OrdinalIgnoreCase) && IsProgramData(canonical))
        {
            return "program-keys";
        }

        return null;
    }

    private static string Refusal(string what)
    {
        var subject = what switch
        {
            "browser" => "файл браузера с сохранёнными паролями, куками или ключом их шифрования",
            "keys" => "папка с закрытыми ключами SSH/GnuPG",
            "windows-vault" => "хранилище учётных данных Windows (Credentials/Protect/Vault)",
            "vault" => "база менеджера паролей",
            "hive" => "куст реестра с учётными записями и секретами Windows",
            "program-keys" => "файл с API-ключами самой программы",
            _ => "файл с секретами"
        };

        return $"ЗАПРЕЩЕНО: это {subject}. Инструменты такие файлы не читают и не копируют: их " +
               "содержимое — чужие пароли и ключи. Не повторяй попытку ни этим, ни другим " +
               "инструментом (в том числе PowerShell). Если задача требует этих данных, скажи " +
               "пользователю, что программа к ним не обращается.";
    }

    private static string? LongPath(string path)
    {
        try
        {
            var buffer = new StringBuilder(1024);
            var length = GetLongPathNameW(path, buffer, buffer.Capacity);
            return length > 0 && length < buffer.Capacity ? buffer.ToString() : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string? LinkTarget(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetLongPathNameW(string shortPath, StringBuilder longPath, int bufferLength);
}
