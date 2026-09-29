namespace Amarin.Tools;

/// <summary>
/// Где новый файл можно создать без вопроса: «Загрузки» и «Рабочий стол» пользователя.
/// </summary>
/// <remarks>
/// <para>
/// Без вопроса — только <b>новый</b> файл. Что файла нет, проверяет не эта функция, а сама запись
/// (<c>FileMode.CreateNew</c>, флаг <see cref="CreateNewFlag"/>): два параллельных вызова в чате
/// иначе оба увидели бы «файла нет», и второй молча переписал бы первый.
/// </para>
/// <para>
/// Путь сравнивается в каноническом виде (<see cref="SensitivePaths.Canonical"/>), а каждая
/// существующая папка между корнем зоны и файлом проверяется на точку повторной обработки:
/// соединение <c>Загрузки\link → C:\Windows\System32</c> иначе превратило бы «новый файл в
/// загрузках» в новую библиотеку в системной папке.
/// </para>
/// </remarks>
internal static class SafeZone
{
    /// <summary>
    /// Имя служебного поля в аргументах записи: «создать, только если файла нет». Ставит его
    /// только шлюз; от модели оно снимается.
    /// </summary>
    public const string CreateNewFlag = "_create_new";

    /// <summary>Корни зоны. Подменяются в тестах.</summary>
    internal static Func<IReadOnlyList<string>> Roots { get; set; } =
        () => [DownloadPaths.DownloadsDirectory, DownloadPaths.DesktopDirectory];

    /// <summary>Лежит ли путь внутри «Загрузок» или «Рабочего стола» (не сам корень).</summary>
    public static bool Contains(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim().Trim('"')))
        {
            return false;
        }

        var raw = path.Trim().Trim('"');

        // Поток «файл:имя» пишет в чужой существующий файл — это не новый файл.
        if (raw.IndexOf(':', 2) >= 0)
        {
            return false;
        }

        var canonical = SensitivePaths.Canonical(raw);
        foreach (var root in Roots())
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var zone = SensitivePaths.Canonical(root).TrimEnd('\\');
            if (!canonical.StartsWith(zone + "\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return !AnyLinkBetween(zone, canonical);
        }

        return false;
    }

    private static bool AnyLinkBetween(string zone, string canonical)
    {
        var current = Path.GetDirectoryName(canonical);
        while (!string.IsNullOrEmpty(current) &&
               current.Length >= zone.Length &&
               current.StartsWith(zone, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var info = new DirectoryInfo(current);
                if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Не прочитали — не знаем, куда ведёт. Значит, не зона.
                return true;
            }

            current = Path.GetDirectoryName(current);
        }

        return false;
    }
}
