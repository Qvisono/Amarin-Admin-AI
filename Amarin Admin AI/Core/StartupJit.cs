using System.Runtime;

namespace Amarin.Core;

/// <summary>
/// Фоновая компиляция кода запуска (multicore JIT): запуск записывает, какие методы
/// понадобились, а следующий запуск той же версии компилирует их на свободных ядрах заранее,
/// пока поток интерфейса разбирает разметку.
/// </summary>
/// <remarks>
/// ReadyToRun покрывает не всё: разборщик PowerShell из предкомпиляции исключён ради веса exe,
/// а обобщённые типы и часть WPF всё равно идут через JIT на потоке интерфейса. Профиль лежит
/// рядом с журналами в <c>%LOCALAPPDATA%</c>: это кэш машины, а не данные человека, поэтому в
/// архив он не едет и при «Удалить все данные» не нужен. Имя — по версии: профиль прежней
/// сборки описывает чужой код, и его файл удаляется.
/// </remarks>
internal static class StartupJit
{
    private const string Extension = ".jitprofile";

    public static void Start(string version)
    {
        try
        {
            var directory = Path.Combine(CrashLog.DefaultDirectory(), "jit");
            Directory.CreateDirectory(directory);

            var name = "startup-" + version + Extension;
            foreach (var stale in Directory.EnumerateFiles(directory, "*" + Extension))
            {
                if (!string.Equals(Path.GetFileName(stale), name, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(stale);
                }
            }

            ProfileOptimization.SetProfileRoot(directory);
            ProfileOptimization.StartProfile(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Без профиля запуск идёт так же, как шёл, только без заблаговременной компиляции.
        }
    }
}
