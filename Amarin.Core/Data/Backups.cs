using System.Globalization;
using System.Text.RegularExpressions;

namespace Amarin.Core;

public enum BackupInterval
{
    Daily,
    Weekly
}

/// <summary>
/// Автоматическое резервное копирование (F1). Выключено, пока человек не включит.
/// </summary>
public sealed class BackupSettings
{
    public bool Enabled { get; set; }

    public BackupInterval Interval { get; set; } = BackupInterval.Daily;

    /// <summary>Куда класть копии. Пусто — <see cref="Backups.DefaultFolder"/>.</summary>
    public string? Folder { get; set; }

    /// <summary>Сколько последних копий хранить; старые удаляются после удачной новой.</summary>
    public int Keep { get; set; } = 7;

    /// <summary>Когда сделана последняя удачная копия.</summary>
    public DateTime? LastAt { get; set; }

    public string? LastFile { get; set; }

    public long LastBytes { get; set; }

    /// <summary>Чем кончилась последняя неудачная попытка. Сбрасывается удачной.</summary>
    public string? LastError { get; set; }

    public DateTime? LastErrorAt { get; set; }
}

/// <summary>Итог одной копии.</summary>
internal sealed record BackupResult(string Path, long Bytes, IReadOnlyList<string> Pruned);

/// <summary>
/// Резервные копии (F1): когда пора, как назвать, что удалить. Сам архив пишет
/// <see cref="DataBundleExporter"/> — тот же, что у ручного экспорта, поэтому ключи, журнал трат,
/// черновики и пароли машин в копию не попадают по тем же правилам (<see cref="DataBundle.CategoryOf"/>).
/// </summary>
internal static class Backups
{
    public const string Prefix = "amarin-backup-";

    /// <summary>После неудачи — не раньше чем через час: иначе полный диск дёргался бы каждую проверку.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// Папка по умолчанию — «Документы»: её знает человек и её же берут программы резервного
    /// копирования Windows. Папка данных программы для этого не годится: копия должна пережить
    /// «Удалить все данные» и потерю самой папки.
    /// </summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Amarin Admin AI Backups");

    public static string FolderOf(BackupSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Folder) ? DefaultFolder : settings.Folder.Trim();

    public static TimeSpan Period(BackupInterval interval) =>
        interval == BackupInterval.Weekly ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);

    /// <summary>Пора ли делать копию.</summary>
    public static bool IsDue(BackupSettings? settings, DateTime now)
    {
        if (settings is not { Enabled: true })
        {
            return false;
        }

        if (settings.LastErrorAt is { } failed && now - failed < RetryAfter &&
            (settings.LastAt is null || failed > settings.LastAt))
        {
            return false;
        }

        return settings.LastAt is not { } last || now - last >= Period(settings.Interval) || last > now;
    }

    public static string FileName(DateTime now) =>
        Prefix + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + DataBundle.FileExtension;

    private static readonly Regex OwnName = new(
        "^" + Regex.Escape(Prefix) + @"\d{8}-\d{6}" + Regex.Escape(DataBundle.FileExtension) + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Какие файлы удалить, чтобы осталось <paramref name="keep"/> последних. Только наши копии,
    /// узнанные по имени целиком: папку человек мог выбрать общую, и всё прочее в ней не наше.
    /// </summary>
    public static IReadOnlyList<string> ToPrune(IEnumerable<string> fileNames, int keep) =>
        fileNames
            .Where(name => OwnName.IsMatch(name))
            .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
            .Skip(Math.Max(1, keep))
            .ToList();

    /// <summary>
    /// Делает копию: весь профиль (<see cref="DataCategory.All"/>) в новый файл, затем чистка.
    /// Чистка — только после удачной записи: иначе сбой оставил бы человека без копий вовсе.
    /// </summary>
    /// <exception cref="IOException">Диск полон, папка недоступна.</exception>
    /// <exception cref="UnauthorizedAccessException">Нет прав на папку.</exception>
    public static BackupResult Run(string root, string profileId, string folder, int keep, DateTime now, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName(now));
        new DataBundleExporter(root, profileId).Write(path, DataCategory.All, cancellationToken);

        var pruned = new List<string>();
        foreach (var name in ToPrune(Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>(), keep))
        {
            try
            {
                File.Delete(Path.Combine(folder, name));
                pruned.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Занятый старый файл удалится в следующий раз; новая копия уже есть.
            }
        }

        return new BackupResult(path, new FileInfo(path).Length, pruned);
    }
}
