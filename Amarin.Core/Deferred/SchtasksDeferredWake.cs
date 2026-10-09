using System.Diagnostics;
using System.Text;

namespace Amarin.Core;

/// <summary>
/// Будит закрытую программу через Планировщик Windows (<c>schtasks.exe</c>): к ближайшему сроку
/// и при входе в Windows, пока есть ждущие задачи. Задач нет — задача Планировщика удаляется.
/// </summary>
/// <remarks>
/// <para>
/// Планировщик зовётся, только когда нужное отличается от поставленного: проверка сроков идёт
/// раз в минуту, а запуск <c>schtasks</c> — это процесс. Что поставлено, помнит файл-отметка в
/// <c>%LOCALAPPDATA%</c> (как и профиль JIT — это дело машины, а не профиля): без неё каждый запуск
/// программы, где отложенных задач никогда не было, удалял бы несуществующую задачу.
/// </para>
/// <para>
/// Отказ Планировщика (групповая политика, служба выключена) — не авария: задачи сработают, пока
/// программа открыта, а вкладка «Отложенные» об отказе скажет (<see cref="Problem"/>).
/// </para>
/// </remarks>
internal sealed class SchtasksDeferredWake : IDeferredWake
{
    /// <summary>Раньше этого срок не ставится: прошедший срок Планировщик наверстал бы сразу.</summary>
    internal static readonly TimeSpan MinLead = TimeSpan.FromMinutes(1);

    private readonly string _exePath;
    private readonly string _sid;
    private readonly string _marker;
    private readonly Func<IReadOnlyList<string>, (int ExitCode, string Error)> _schtasks;
    private readonly Func<DateTime> _utcNow;
    private readonly Lock _gate = new();
    private string? _applied;

    /// <param name="marker">Файл-отметка того, что поставлено в Планировщике.</param>
    /// <param name="schtasks">Запуск <c>schtasks.exe</c> с аргументами; подменяется в тестах.</param>
    public SchtasksDeferredWake(
        string exePath,
        string userSid,
        string marker,
        Func<IReadOnlyList<string>, (int ExitCode, string Error)>? schtasks = null,
        Func<DateTime>? utcNow = null)
    {
        _exePath = exePath;
        _sid = userSid;
        _marker = marker;
        _schtasks = schtasks ?? Run;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Почему Планировщик отказал в последний раз; null — всё поставлено.</summary>
    public string? Problem { get; private set; }

    /// <summary>Отказ появился или прошёл.</summary>
    public event Action? ProblemChanged;

    public void Reconcile(bool pending, DateTime? dueUtc)
    {
        lock (_gate)
        {
            // Отметка — по самому сроку, а не по сдвинутому: просроченная задача, которую не пустил
            // занятый чат, иначе переставляла бы задачу Планировщика на каждой проверке.
            var want = pending ? StateKey(dueUtc) : "";
            _applied ??= ReadMarker();
            if (want == _applied)
            {
                return;
            }

            var due = dueUtc is { } value && value < _utcNow() + MinLead ? _utcNow() + MinLead : dueUtc;
            var name = DeferredWakeTask.NameFor(_sid);
            var (exitCode, error) = pending ? Create(name, due) : _schtasks(["/Delete", "/TN", name, "/F"]);

            // Удалить то, чего уже нет, — тоже успех: задачу мог убрать сам человек.
            if (exitCode == 0 || !pending)
            {
                _applied = want;
                WriteMarker(want);
                Report(null);
                return;
            }

            Report(string.IsNullOrWhiteSpace(error) ? "schtasks exit " + exitCode : error.Trim());
        }
    }

    /// <summary>Что поставлено: путь программы и срок до минуты — смена любого переставляет задачу.</summary>
    private string StateKey(DateTime? dueUtc) =>
        _exePath + "|" + (dueUtc is { } due ? due.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture) : "logon");

    private (int, string) Create(string name, DateTime? dueUtc)
    {
        var file = Path.Combine(Path.GetTempPath(), "amarin-wake-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            // Планировщик читает XML только в UTF-16 с меткой порядка байтов.
            File.WriteAllText(file, DeferredWakeTask.Build(_exePath, _sid, dueUtc), Encoding.Unicode);
            return _schtasks(["/Create", "/TN", name, "/XML", file, "/F"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (-1, ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Временный файл без секретов: его уберёт очистка временной папки.
            }
        }
    }

    private void Report(string? problem)
    {
        if (problem == Problem)
        {
            return;
        }

        Problem = problem;
        ProblemChanged?.Invoke();
    }

    private string? ReadMarker()
    {
        try
        {
            return File.Exists(_marker) ? File.ReadAllText(_marker) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteMarker(string state)
    {
        try
        {
            if (state.Length == 0)
            {
                File.Delete(_marker);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_marker) ?? ".");
            File.WriteAllText(_marker, state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Без отметки задача просто переставится в следующий раз.
        }
    }

    private static (int ExitCode, string Error) Run(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return (-1, "schtasks did not start");
            }

            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
            {
                process.Kill();
                return (-1, "schtasks timed out");
            }

            return (process.ExitCode, error.Wait(TimeSpan.FromSeconds(2)) ? error.Result : "");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }
}

/// <summary>Без Планировщика: тесты и окно вне настоящей программы.</summary>
internal sealed class NoDeferredWake : IDeferredWake
{
    public static readonly NoDeferredWake Instance = new();

    public void Reconcile(bool pending, DateTime? dueUtc)
    {
    }
}
