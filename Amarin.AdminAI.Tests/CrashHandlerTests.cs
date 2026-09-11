using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Раньше необработанное исключение закрывало программу молча: глобальных перехватчиков не было,
/// а PerfLog по умолчанию выключен. Эти тесты держат обе половины страховки — журнал на диске и
/// отчёт, который не стыдно переслать.
/// </summary>
public sealed class CrashHandlerTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "amarin-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Crash_log_writes_the_report()
    {
        var dir = TempDir();
        try
        {
            var writer = new CrashLogWriter(dir);
            writer.Write("первая авария");
            writer.Write("вторая авария");

            var text = File.ReadAllText(writer.FilePath);
            Assert.Contains("первая авария", text, StringComparison.Ordinal);
            Assert.Contains("вторая авария", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Crash_log_trims_itself_instead_of_growing_forever()
    {
        var dir = TempDir();
        try
        {
            var writer = new CrashLogWriter(dir);
            var block = new string('x', 8 * 1024);
            for (var i = 0; i < 40; i++)
            {
                writer.Write($"запись {i} {block}");
            }

            var length = new FileInfo(writer.FilePath).Length;
            Assert.True(length <= CrashLogWriter.MaxBytes,
                $"журнал разросся до {length} байт при пороге {CrashLogWriter.MaxBytes}");

            // Подрезается начало, а не конец: свежая авария важнее прошлогодней.
            Assert.Contains("запись 39", File.ReadAllText(writer.FilePath), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Crash_log_stays_quiet_when_the_directory_is_unusable()
    {
        // Файл вместо папки: Directory.CreateDirectory на нём бросит. Журнал обязан промолчать —
        // запись об аварии не должна становиться второй аварией.
        var file = Path.Combine(Path.GetTempPath(), "amarin-crash-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "занято");
        try
        {
            var writer = new CrashLogWriter(Path.Combine(file, "nested"));
            writer.Write("авария");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Report_carries_type_message_stack_and_version()
    {
        var exception = Catch(() => throw new InvalidOperationException("окно уехало за экран"));

        var report = CrashReport.Build(exception, "поток интерфейса");

        Assert.Contains("System.InvalidOperationException", report, StringComparison.Ordinal);
        Assert.Contains("окно уехало за экран", report, StringComparison.Ordinal);
        Assert.Contains("Catch", report, StringComparison.Ordinal);
        Assert.Contains(CrashReport.Version, report, StringComparison.Ordinal);
        Assert.Contains("поток интерфейса", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_unwraps_inner_exceptions()
    {
        var inner = Catch(() => throw new IOException("файл занят"));
        var outer = new InvalidOperationException("не удалось сохранить чат", inner);

        var report = CrashReport.Build(outer, "поток интерфейса");

        Assert.Contains("не удалось сохранить чат", report, StringComparison.Ordinal);
        Assert.Contains("файл занят", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_hides_the_api_key()
    {
        const string key = "vk-secret-key-0123456789";
        var exception = new HttpRequestException($"401 для ключа {key}");

        var report = CrashReport.Build(exception, "поток интерфейса", key);

        Assert.DoesNotContain(key, report, StringComparison.Ordinal);
        Assert.Contains("***", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_hides_a_bearer_token_even_without_the_key_at_hand()
    {
        // Сбой на старте случается раньше, чем прочитан ключ, — но заголовок в тексте уже есть.
        var exception = new HttpRequestException("Authorization: Bearer vk-abcdef0123456789 отклонён");

        var report = CrashReport.Build(exception, "запуск", secret: null);

        Assert.DoesNotContain("vk-abcdef0123456789", report, StringComparison.Ordinal);
        Assert.Contains("Bearer ***", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Scrub_leaves_a_short_secret_alone()
    {
        // Пустой или короткий «секрет» вырезал бы куски обычного текста.
        Assert.Equal("отчёт про abc", CrashReport.Scrub("отчёт про abc", "abc"));
        Assert.Equal("отчёт", CrashReport.Scrub("отчёт", ""));
    }

    [Fact]
    public void Cancellation_is_not_a_crash()
    {
        Assert.True(CrashHandler.IsSilent(new OperationCanceledException()));
        Assert.True(CrashHandler.IsSilent(new TaskCanceledException()));
        Assert.False(CrashHandler.IsSilent(new InvalidOperationException()));
    }

    [Fact]
    public void Human_line_names_the_reason_before_the_exception_text()
    {
        var text = CrashHandler.Describe(new UnauthorizedAccessException(@"C:\Windows\System32"));

        Assert.StartsWith("Нет доступа к файлу или папке.", text, StringComparison.Ordinal);
        Assert.Contains("System32", text, StringComparison.Ordinal);
    }

    private static Exception Catch(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("действие не бросило исключение");
    }
}
