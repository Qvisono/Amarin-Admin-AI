using System.Text;

namespace Amarin.Core;

internal static class AppDataFile
{
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
    /// Временный файл у каждой записи свой. Общий «имя.tmp» сталкивал два потока, сохраняющих
    /// один файл одновременно (привязка служб окна и фоновый перенос трат оба пишут
    /// settings.json): второй получал IOException «файл занят другим процессом», и сохранение
    /// падало. Теперь побеждает последний, как и положено при записи целиком.
    /// </remarks>
    public static void WriteAtomicBytes(string path, byte[] contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
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
