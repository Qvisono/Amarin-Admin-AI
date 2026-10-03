using System.Text;

namespace Amarin.Core;

/// <summary>
/// Журнал аварий рядом с <see cref="PerfLog"/>: <c>%LOCALAPPDATA%\AmarinAdminAI\crash.log</c>.
/// </summary>
/// <remarks>
/// До этого класса после краха у человека не оставалось ни строки диагностики: глобального
/// перехватчика не было вовсе, а <see cref="PerfLog"/> по умолчанию выключен и включается
/// переменной окружения, о которой пользователь не знает. Поэтому журнал аварий пишется всегда.
/// Все ошибки записи глушатся: запись об аварии не имеет права стать второй аварией.
/// </remarks>
internal sealed class CrashLogWriter
{
    /// <summary>Выше этого размера файл подрезается, чтобы не расти бесконечно.</summary>
    internal const long MaxBytes = 256 * 1024;

    /// <summary>Сколько хвоста остаётся после подрезки.</summary>
    internal const int KeepBytes = 64 * 1024;

    private const string TrimMarker = "... [начало журнала обрезано]";

    private readonly Lock _gate = new();

    public CrashLogWriter(string directory) =>
        FilePath = Path.Combine(directory, "crash.log");

    public string FilePath { get; }

    /// <summary>Дописывает отчёт в журнал. Никогда не бросает.</summary>
    public void Write(string report)
    {
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(FilePath, report + Environment.NewLine, Encoding.UTF8);
                TrimIfTooLarge();
            }
            catch
            {
                // Папка недоступна, диск полон, файл занят — молчим.
            }
        }
    }

    private void TrimIfTooLarge()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length <= MaxBytes)
        {
            return;
        }

        var text = File.ReadAllText(FilePath, Encoding.UTF8);
        var cut = text.Length - KeepBytes;
        if (cut <= 0)
        {
            return;
        }

        // Режем по границе строки, иначе хвост начнётся с середины стека.
        var newline = text.IndexOf('\n', cut);
        var tail = newline >= 0 ? text[(newline + 1)..] : text[cut..];
        File.WriteAllText(FilePath, TrimMarker + Environment.NewLine + tail, Encoding.UTF8);
    }
}

internal static class CrashLog
{
    private static readonly Lazy<CrashLogWriter> Writer = new(() => new CrashLogWriter(DefaultDirectory()));

    public static CrashLogWriter Default => Writer.Value;

    public static void Write(string report) => Default.Write(report);

    /// <summary>Тот же корень, что у <see cref="PerfLog"/> — вся диагностика в одном месте.</summary>
    internal static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmarinAdminAI");
}
