using System.Collections.Concurrent;
using System.Globalization;

namespace Amarin.Core;

/// <summary>Чем кончилось чтение файла, у которого есть запасная копия.</summary>
public enum GuardedFileState
{
    /// <summary>Файла нет — первый запуск или его удалили намеренно.</summary>
    Missing,

    /// <summary>Файл прочитан.</summary>
    Ok,

    /// <summary>Файл был повреждён, взята запасная копия.</summary>
    RestoredFromBackup,

    /// <summary>Повреждены и файл, и копия — дальше заводские значения.</summary>
    Reset,

    /// <summary>Файл не открылся (занят, нет прав). Он не тронут, и повреждённым не считается.</summary>
    Unreadable
}

/// <summary>Что случилось с файлом данных при запуске — для сообщения человеку.</summary>
/// <param name="FileName">Имя файла: settings.json, profiles.json, keys.json.</param>
/// <param name="BrokenCopy">Куда отложен повреждённый файл.</param>
public sealed record DataFileIncident(string FileName, GuardedFileState State, string BrokenCopy);

/// <summary>
/// Файлы, которые нельзя терять молча: настройки, реестр профилей, ключи. При каждой удачной
/// записи рядом лежит <c>.bak</c> — последняя читаемая версия; повреждённый файл
/// откладывается в <c>.broken-&lt;время&gt;</c>, а на его место встаёт копия.
/// </summary>
/// <remarks>
/// <para>
/// До 1.28.0 повреждённый <c>settings.json</c> молча читался как заводской, и первое же
/// сохранение (любой переключатель) переписывало файл человека заводскими значениями — без
/// следа и без слова. Теперь повреждённый файл никогда не перезаписывается: он уезжает в
/// сторону, человек узнаёт об этом сообщением (<see cref="DataFileIncidents"/>).
/// </para>
/// <para>
/// Отсутствующий файл повреждённым не считается и из копии не поднимается: его могли удалить
/// сами, чтобы сбросить настройки, — атомарная запись пропавшего файла не оставляет. Файл,
/// который не открылся (занят антивирусом, нет прав), тоже не повреждён: трогать его нельзя.
/// </para>
/// </remarks>
internal static class GuardedJsonFile
{
    private const int ReadAttempts = 3;

    public static string BackupPath(string path) => path + ".bak";

    /// <summary>Приставка отложенных повреждённых копий: <c>settings.json.broken-20260930-101500</c>.</summary>
    public static string BrokenPrefix(string path) => path + ".broken-";

    /// <summary>
    /// Пишет файл атомарно и следом — его запасную копию. Копия пишется только после удачной
    /// записи самого файла, поэтому в ней всегда то, что программа сочла правильным.
    /// </summary>
    public static void Write(string path, string contents)
    {
        AppDataFile.WriteAtomic(path, contents);
        try
        {
            AppDataFile.WriteAtomic(BackupPath(path), contents);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Копия — страховка, а не условие записи: не записалась — осталась прежняя.
        }
    }

    /// <summary>
    /// Читает файл и при повреждении восстанавливает его из копии.
    /// </summary>
    /// <param name="isValid">Разбирается ли текст. Не бросает.</param>
    /// <param name="text">Текст, который можно разбирать, или <c>null</c>.</param>
    public static GuardedFileState Read(
        string path,
        Func<string, bool> isValid,
        out string? text,
        DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(isValid);
        text = null;
        if (!File.Exists(path))
        {
            return GuardedFileState.Missing;
        }

        if (!TryReadText(path, out var current))
        {
            return GuardedFileState.Unreadable;
        }

        if (IsValidSafe(isValid, current))
        {
            text = current;
            return GuardedFileState.Ok;
        }

        var broken = SetAside(path, now ?? DateTime.Now);
        var backup = BackupPath(path);
        if (File.Exists(backup) && TryReadText(backup, out var saved) && IsValidSafe(isValid, saved))
        {
            // Поверх повреждённого копия ложится, только если он сохранён в стороне.
            if (broken.Length > 0)
            {
                try
                {
                    AppDataFile.WriteAtomic(path, saved);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Вернуть на место не вышло — читаем копию и так, файл запишется при сохранении.
                }
            }

            text = saved;
            DataFileIncidents.Report(new DataFileIncident(Path.GetFileName(path), GuardedFileState.RestoredFromBackup, broken));
            return GuardedFileState.RestoredFromBackup;
        }

        DataFileIncidents.Report(new DataFileIncident(Path.GetFileName(path), GuardedFileState.Reset, broken));
        return GuardedFileState.Reset;
    }

    private static bool IsValidSafe(Func<string, bool> isValid, string text)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(text) && isValid(text);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryReadText(string path, out string text)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                text = File.ReadAllText(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttempts || ex is UnauthorizedAccessException)
                {
                    text = "";
                    return false;
                }

                Thread.Sleep(40 * attempt);
            }
        }
    }

    /// <summary>Отодвигает повреждённый файл; вернёт путь, куда он уехал (или пустую строку).</summary>
    private static string SetAside(string path, DateTime now)
    {
        var target = BrokenPrefix(path) + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        for (var suffix = 1; File.Exists(target); suffix++)
        {
            target = BrokenPrefix(path) + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + suffix;
        }

        try
        {
            File.Move(path, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Файл держат открытым — переименовать нельзя, а прочитать можно: тогда хотя бы
            // копия. Не вышло и она — поверх повреждённого копия из .bak не ляжет.
        }

        try
        {
            File.Copy(path, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}

/// <summary>
/// Повреждённые файлы данных, найденные при чтении. Окно показывает их человеку после первого
/// кадра: чтение идёт раньше, чем есть кому показать.
/// </summary>
/// <remarks>Статическое состояние — тест, который его наполняет, обязан его вычистить (<see cref="Drain"/>).</remarks>
public static class DataFileIncidents
{
    private static readonly ConcurrentQueue<DataFileIncident> Pending = new();

    public static void Report(DataFileIncident incident) => Pending.Enqueue(incident);

    public static IReadOnlyList<DataFileIncident> Drain()
    {
        var list = new List<DataFileIncident>();
        while (Pending.TryDequeue(out var incident))
        {
            list.Add(incident);
        }

        return list;
    }
}

/// <summary>Текст сообщения о повреждённых файлах данных.</summary>
internal static class DataFileIncidentText
{
    public static string Describe(IReadOnlyList<DataFileIncident> incidents)
    {
        var lines = incidents.Select(incident =>
        {
            var key = incident.State == GuardedFileState.RestoredFromBackup
                ? "S.DataSafety.Restored"
                : "S.DataSafety.Reset";
            var line = Loc.Format(key, incident.FileName);
            return incident.BrokenCopy.Length == 0
                ? line
                : line + " " + Loc.Format("S.DataSafety.KeptAt", incident.BrokenCopy);
        });

        return string.Join("\n\n", lines);
    }
}
