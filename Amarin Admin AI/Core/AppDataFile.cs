using System.Collections.Concurrent;
using System.Text;

namespace Amarin.Core;

internal static class AppDataFile
{
    /// <summary>Замок на каждый путь: писатели одного файла в этом процессе идут по очереди.</summary>
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Сколько раз повторить подмену, если файл на миг занят чужим процессом.</summary>
    private const int MoveAttempts = 5;

    /// <summary>Текст в UTF-8 с BOM — так, как его всегда писал <c>File.WriteAllText</c>.</summary>
    public static void WriteAtomic(string path, string contents)
    {
        // Одним буфером, а не склейкой: переписка с картинками весит мегабайты, и лишняя копия
        // ложилась бы в кучу больших объектов на каждое сохранение.
        var preamble = Encoding.UTF8.Preamble;
        var bytes = new byte[preamble.Length + Encoding.UTF8.GetByteCount(contents)];
        preamble.CopyTo(bytes);
        Encoding.UTF8.GetBytes(contents, bytes.AsSpan(preamble.Length));
        WriteAtomicBytes(path, bytes);
    }

    /// <summary>
    /// Пишет файл целиком или не пишет вовсе: сначала во временный рядом, потом переименованием.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Временный файл у каждой записи свой. Общий «имя.tmp» сталкивал два потока, сохраняющих
    /// один файл одновременно (привязка служб окна и фоновый перенос трат оба пишут
    /// settings.json): второй получал IOException «файл занят другим процессом», и сохранение
    /// падало.
    /// </para>
    /// <para>
    /// Своих временных файлов мало: Windows не даёт двум переименованиям одновременно заменить
    /// один и тот же файл и отвечает второму «доступ запрещён». Поэтому писатели одного пути
    /// в этом процессе ещё и встают в очередь, а подмена повторяется несколько раз — на случай,
    /// когда файл на миг открыл чужой процесс (антивирус, индексатор). Побеждает последний.
    /// </para>
    /// </remarks>
    public static void WriteAtomicBytes(string path, byte[] contents)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = $"{full}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, contents);
            lock (Locks.GetOrAdd(full, _ => new object()))
            {
                MoveWithRetry(temp, full);
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void MoveWithRetry(string temp, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < MoveAttempts &&
                                       ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Хвост записи, которая и так не удалась; убрать его — любезность, а не обязанность.
        }
    }
}
