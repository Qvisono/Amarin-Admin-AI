using System.Collections.Frozen;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Где ИИ создаёт новый документ без вопроса: везде, кроме системы, программ, данных приложений,
/// чужих профилей и сети.
/// </summary>
/// <remarks>
/// <para>
/// Человек просил: документ, созданный нейросетью, разрешения не требует. До 1.33.0 без вопроса
/// создавалось только новое в «Загрузках» и на «Рабочем столе» (<see cref="SafeZone"/>), и просьба
/// положить отчёт в «Документы» или на диск D спрашивала каждый раз.
/// </para>
/// <para>
/// Молча — только документ (<see cref="IsDocumentFile"/>): скрипт или программа, положенные в
/// любую папку, могли бы запуститься сами, а документ — нет. Системные папки, папки программ и
/// данных приложений (там же автозагрузка) остаются за вопросом. Сеть — тоже: молчаливая запись
/// на чужой сервер была бы утечкой без следа. Что файла ещё нет, проверяет сама запись
/// (<see cref="SafeZone.CreateNewFlag"/>), как и в <see cref="SafeZone"/>; и так же, как там,
/// ссылка (junction) по пути к файлу снимает «молча»: папка «Документы\link» могла бы вести в
/// системную.
/// </para>
/// </remarks>
internal static class DocumentZone
{
    private static readonly FrozenSet<string> DocumentExtensions = new[]
    {
        ".docx", ".xlsx", ".pdf", ".txt", ".md", ".markdown", ".csv", ".tsv", ".html", ".htm", ".rtf",
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Документ или картинка — то, что не запускается само, если его положить в папку.</summary>
    public static bool IsDocumentFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && DocumentExtensions.Contains(Path.GetExtension(path.Trim().Trim('"')));

    /// <summary>Можно ли создать здесь новое без вопроса: место не системное, не чужое и не сетевое.</summary>
    public static bool Allows(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var raw = path.Trim().Trim('"');

        // Сеть и поток «файл:имя» (он пишет в чужой существующий файл) — не сюда.
        if (raw.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(raw) || raw.IndexOf(':', 2) >= 0)
        {
            return false;
        }

        var canonical = SensitivePaths.Canonical(raw);
        if (!LocalDrive(canonical) || Blocked().Any(root => Under(canonical, root)))
        {
            return false;
        }

        // Папка пользователей — только свой профиль.
        var profile = Canonical(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var users = Path.GetDirectoryName(profile);
        if (!string.IsNullOrEmpty(users) && Under(canonical, users) && !Under(canonical, profile))
        {
            return false;
        }

        return !AnyLinkOnTheWay(canonical);
    }

    private static IEnumerable<string> Blocked()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] folders =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            local,
            string.IsNullOrEmpty(local) ? "" : Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow")
        ];
        return folders.Where(folder => !string.IsNullOrWhiteSpace(folder)).Select(Canonical);
    }

    private static bool LocalDrive(string canonical)
    {
        try
        {
            return Path.GetPathRoot(canonical) is { Length: > 0 } root &&
                   new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Есть ли среди существующих папок пути ссылка: тогда настоящее место — не то, что написано.</summary>
    private static bool AnyLinkOnTheWay(string canonical)
    {
        var current = Path.GetDirectoryName(canonical);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var info = new DirectoryInfo(current);
                if (info.Exists && info.LinkTarget is not null)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return true;
            }

            current = Path.GetDirectoryName(current);
        }

        return false;
    }

    private static string Canonical(string path) => SensitivePaths.Canonical(path).TrimEnd('\\');

    private static bool Under(string path, string root) =>
        root.Length > 0 &&
        (path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
}
