using System.Diagnostics;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>Какие программы были открыты — и когда это записано.</summary>
/// <param name="AtShutdown">Записано в момент выключения Windows, а не периодически.</param>
internal sealed record SessionSnapshotFile(DateTime TakenUtc, bool AtShutdown, IReadOnlyList<OpenProgram> Programs);

/// <summary>Чем кончился возврат программ.</summary>
internal sealed record RestoreResult(IReadOnlyList<string> Opened, IReadOnlyList<string> Failed, bool NothingToDo);

/// <summary>
/// Программы, открытые при выключении компьютера (<c>last-session.json</c>), и их возврат.
/// </summary>
/// <remarks>
/// <para>
/// Пишется, только пока ждёт задача «вернуть программы»: в момент выключения Windows
/// (<c>SessionEnding</c>) и раз в несколько минут в фоне — программу могли закрыть раньше
/// выключения, а сбой питания до выключения не доходит вовсе. Без такой задачи список открытых
/// программ не собирается: незачем.
/// </para>
/// <para>
/// Возвращаются сами программы, без документов в них: что было открыто внутри, знает только
/// программа. Уже открытые пропускаются, запускаются по одной с паузой — двадцать программ разом
/// при входе в Windows встали бы колом.
/// </para>
/// </remarks>
internal static class SessionSnapshot
{
    internal const string FileName = "last-session.json";

    /// <summary>Больше этого не возвращается: длинный список — скорее ошибка, чем рабочий стол человека.</summary>
    internal const int MaxPrograms = 20;

    private static readonly JsonSerializerOptions Json = new(AppJson.Options) { WriteIndented = false };

    public static void Save(string root, IReadOnlyList<OpenProgram> programs, bool atShutdown, DateTime nowUtc)
    {
        try
        {
            var snapshot = new SessionSnapshotFile(nowUtc, atShutdown, [.. programs.Take(MaxPrograms)]);
            AppDataFile.WriteAtomic(Path.Combine(root, FileName), JsonSerializer.Serialize(snapshot, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Снимок — справка для будущей задачи: не записался сейчас — запишется через несколько минут.
        }
    }

    public static SessionSnapshotFile? Load(string root)
    {
        try
        {
            var path = Path.Combine(root, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<SessionSnapshotFile>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Открывает программы из снимка, которых сейчас нет на экране.</summary>
    /// <param name="start">Запуск одной программы; подменяется в тестах.</param>
    public static async Task<RestoreResult> RestoreAsync(
        SessionSnapshotFile? snapshot,
        IReadOnlyList<OpenProgram> open,
        Func<OpenProgram, bool>? start,
        TimeSpan pause,
        CancellationToken cancellationToken)
    {
        if (snapshot is null || snapshot.Programs.Count == 0)
        {
            return new RestoreResult([], [], NothingToDo: true);
        }

        var running = open.Select(program => program.AppUserModelId ?? program.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = snapshot.Programs.Where(program => !running.Contains(program.AppUserModelId ?? program.Path)).ToList();
        if (pending.Count == 0)
        {
            return new RestoreResult([], [], NothingToDo: true);
        }

        start ??= Start;
        var opened = new List<string>();
        var failed = new List<string>();
        foreach (var program in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (start(program) ? opened : failed).Add(Path.GetFileNameWithoutExtension(program.Name));
            await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
        }

        return new RestoreResult(opened, failed, NothingToDo: false);
    }

    private static bool Start(OpenProgram program)
    {
        try
        {
            using var process = program.AppUserModelId is { Length: > 0 } app
                ? Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + app) { UseShellExecute = true })
                : File.Exists(program.Path)
                    ? Process.Start(new ProcessStartInfo(program.Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(program.Path) ?? "" })
                    : null;
            return process is not null || program.AppUserModelId is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }
}
