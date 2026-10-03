namespace Amarin.Core;

/// <summary>
/// Обход папки данных пользователя вглубь.
/// </summary>
/// <remarks>
/// Недоступная подпапка пропускается, а не роняет обход: ни отчёт о занятом месте, ни экспорт
/// не та вещь, ради которой стоит показывать человеку исключение. Одна копия на двоих —
/// в <see cref="DataUsage"/> и <see cref="DataBundleExporter"/> лежал один и тот же код.
/// </remarks>
internal static class FileWalk
{
    /// <summary>Все файлы под <paramref name="root"/>, включая вложенные папки.</summary>
    public static IEnumerable<FileInfo> Files(string root, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            yield break;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,

            // Junction внутри папки данных увёл бы обход куда угодно по диску.
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        IEnumerator<FileInfo> walker;
        try
        {
            walker = new DirectoryInfo(root).EnumerateFiles("*", options).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        // Ручной перебор, а не foreach: MoveNext у перечисления файлов бросает на первой же
        // недоступной папке, а обернуть тело foreach в try нельзя — внутри стоит yield.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileInfo current;
            try
            {
                if (!walker.MoveNext())
                {
                    break;
                }

                current = walker.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }

            yield return current;
        }

        walker.Dispose();
    }
}
