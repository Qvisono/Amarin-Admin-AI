namespace Amarin.Core;

/// <summary>Что делать со старыми чатами (F3).</summary>
public enum RetentionMode
{
    Off,
    Archive,
    Delete
}

/// <summary>Хранение чатов: убирать в архив или удалять чаты, не менявшиеся дольше N дней.</summary>
public sealed class ChatRetention
{
    public RetentionMode Mode { get; set; } = RetentionMode.Off;

    public int Days { get; set; } = 180;
}

/// <summary>Что чистится кнопками на странице данных.</summary>
internal enum CleanupTarget
{
    /// <summary>Журналы сбоев и замеров (<c>*.log</c> в папке диагностики).</summary>
    Logs,

    /// <summary>Снимки для отката изменений — кроме нескольких последних.</summary>
    Snapshots,

    /// <summary>Уже сделанные экспорты чатов (<c>shared/</c>).</summary>
    Shared,

    /// <summary>Готовые снимки фона (<c>*.cache.png</c>) — пересоздаются из самой картинки.</summary>
    BackgroundCache
}

internal readonly record struct CleanupSize(long Bytes, int Files);

/// <summary>Крупный чат: сколько весит на диске вместе со вложениями.</summary>
internal readonly record struct ChatSize(string Id, long Bytes);

/// <summary>
/// Очистка места (F3) и правила хранения чатов. Ходит по диску — зовётся не с потока интерфейса.
/// </summary>
/// <remarks>
/// Каждая цель узнаёт свои файлы по тому же правилу, по которому их считает «Занято на диске»
/// (<see cref="DataUsage"/>): иначе кнопка «очистить 40 МБ» освобождала бы другое число.
/// Ни чаты, ни настройки, ни ключи ни одна цель не трогает.
/// </remarks>
internal static class DataCleanup
{
    /// <summary>Сколько последних снимков оставить по умолчанию: откатить последние действия остаётся можно.</summary>
    public const int DefaultKeepSnapshots = 5;

    /// <summary>Меньше недели хранения не бывает: опечатка «1» стёрла бы почти всё.</summary>
    public const int MinRetentionDays = 7;

    public static CleanupSize Measure(CleanupTarget target, string appRoot, string localRoot, int keepSnapshots)
    {
        long bytes = 0;
        var files = 0;
        foreach (var file in FilesOf(target, appRoot, localRoot, keepSnapshots))
        {
            bytes += file.Length;
            files++;
        }

        return new CleanupSize(bytes, files);
    }

    /// <summary>Удаляет и возвращает, сколько освобождено. Занятый файл пропускается молча.</summary>
    public static CleanupSize Run(CleanupTarget target, string appRoot, string localRoot, int keepSnapshots)
    {
        long bytes = 0;
        var files = 0;
        foreach (var file in FilesOf(target, appRoot, localRoot, keepSnapshots).ToList())
        {
            try
            {
                var length = file.Length;
                file.Delete();
                bytes += length;
                files++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Журнал пишется прямо сейчас или файл открыт — удалится в следующий раз.
            }
        }

        if (target == CleanupTarget.Snapshots)
        {
            foreach (var folder in OldSnapshotFolders(localRoot, keepSnapshots))
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return new CleanupSize(bytes, files);
    }

    private static IEnumerable<FileInfo> FilesOf(CleanupTarget target, string appRoot, string localRoot, int keepSnapshots)
    {
        switch (target)
        {
            case CleanupTarget.Logs:
                foreach (var file in FileWalk.Files(localRoot, CancellationToken.None))
                {
                    if (DataUsage.ClassifyLocalFile(Relative(localRoot, file.FullName)) == DataUsage.LogsKey)
                    {
                        yield return file;
                    }
                }

                break;

            case CleanupTarget.Snapshots:
                foreach (var folder in OldSnapshotFolders(localRoot, keepSnapshots))
                {
                    foreach (var file in FileWalk.Files(folder, CancellationToken.None))
                    {
                        yield return file;
                    }
                }

                break;

            case CleanupTarget.Shared:
                foreach (var file in FileWalk.Files(appRoot, CancellationToken.None))
                {
                    if (DataUsage.ClassifyAppFile(Relative(appRoot, file.FullName)) == DataUsage.SharedKey)
                    {
                        yield return file;
                    }
                }

                break;

            case CleanupTarget.BackgroundCache:
                foreach (var file in FileWalk.Files(appRoot, CancellationToken.None))
                {
                    if (file.Name.EndsWith(".cache.png", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return file;
                    }
                }

                break;
        }
    }

    /// <summary>
    /// Папки снимков сверх <paramref name="keep"/> последних. Порядок — по имени, как у
    /// <c>ChangeRollbackStore.PruneOldSnapshots</c>: имена начинаются с отметки времени.
    /// </summary>
    private static IReadOnlyList<string> OldSnapshotFolders(string localRoot, int keep)
    {
        var root = Path.Combine(localRoot, "snapshots");
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.GetDirectories(root)
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Skip(Math.Max(0, keep))
            .ToList();
    }

    /// <summary>
    /// Самые тяжёлые чаты профиля: файл переписки плюс папка его вложений, если она есть.
    /// </summary>
    public static IReadOnlyList<ChatSize> LargestChats(string chatsFolder, IEnumerable<string> ids, int count)
    {
        var sizes = new List<ChatSize>();
        foreach (var id in ids)
        {
            long bytes = 0;
            var file = new FileInfo(Path.Combine(chatsFolder, id + ".json"));
            if (file.Exists)
            {
                bytes += file.Length;
            }

            var folder = Path.Combine(chatsFolder, id);
            if (Directory.Exists(folder))
            {
                bytes += FileWalk.Files(folder, CancellationToken.None).Sum(item => item.Length);
            }

            if (bytes > 0)
            {
                sizes.Add(new ChatSize(id, bytes));
            }
        }

        return sizes.OrderByDescending(size => size.Bytes).Take(count).ToList();
    }

    /// <summary>
    /// Какие чаты пора убрать по правилу хранения. Закреплённые и занятые (идёт ход, открыт) не
    /// трогаются никогда; в режиме архива уже убранные в архив не считаются.
    /// </summary>
    public static IReadOnlyList<string> PickForRetention(
        IEnumerable<ChatIndexEntry> items,
        ChatOrganizer.State organize,
        ChatRetention? retention,
        DateTime now,
        IReadOnlySet<string> busy)
    {
        if (retention is not { Mode: not RetentionMode.Off })
        {
            return [];
        }

        var cutoff = now.AddDays(-Math.Max(MinRetentionDays, retention.Days));
        return items
            .Where(item => item.UpdatedAt < cutoff && !item.IsPinned && !busy.Contains(item.Id))
            .Where(item => retention.Mode == RetentionMode.Delete ||
                           !(organize.Chats.TryGetValue(item.Id, out var placement) && placement.Archived))
            .Select(item => item.Id)
            .ToList();
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/').ToLowerInvariant();
}
