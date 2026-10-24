using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Amarin.Tools;

/// <summary>Один процесс в одном замере.</summary>
/// <param name="Created">Время запуска: вместе с PID отличает процесс от нового с тем же номером.</param>
/// <param name="CpuTicks">Время процессора с запуска, в 100 нс (ядро и пользователь).</param>
/// <param name="IoBytes">Прочитано и записано с запуска: файлы, сеть, устройства.</param>
internal sealed record ProcessSample(int Pid, long Created, string Name, long CpuTicks, long IoBytes, long PrivateBytes);

/// <summary>Замер всей системы.</summary>
/// <param name="CpuPercent">Загрузка всех ядер с прошлого замера; у первого замера — null.</param>
/// <param name="DiskBusyPercent">Занятость дисков; null — счётчик недоступен.</param>
internal sealed record SystemSample(DateTime Time, double? CpuPercent, double? MemoryPercent, double? DiskBusyPercent, IReadOnlyList<ProcessSample> Processes);

/// <summary>Откуда берутся замеры. Подменяется в тестах.</summary>
internal interface IPerfSampler : IDisposable
{
    SystemSample Take(DateTime now);
}

/// <summary>
/// Замеры процессов и системы для <see cref="PerfRecordTool"/>.
/// </summary>
/// <remarks>
/// Процессы открываются с ограниченными правами (<c>PROCESS_QUERY_LIMITED_INFORMATION</c>): их
/// дают и на чужие и повышенные процессы, и без администратора видно почти всё. Описатель
/// держится, пока процесс жив: открывать три сотни процессов на каждый замер — заметная нагрузка
/// от самого замера.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class ProcessSampler : IPerfSampler
{
    private const uint QueryLimited = 0x1000;
    private const uint StillActive = 259;

    private readonly Dictionary<int, (SafeProcessHandle Handle, long Created)> _handles = [];
    private readonly PerformanceCounter? _diskIdle;
    private readonly CpuMeter _cpu = new();
    private bool _primed;

    public ProcessSampler()
    {
        try
        {
            _diskIdle = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", readOnly: true);
            _ = _diskIdle.NextValue();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _diskIdle = null;
        }
    }

    public SystemSample Take(DateTime now)
    {
        // Первый замер только ставит точку отсчёта: загрузка за миг между конструктором и ним —
        // шум, и он ложился в отчёт пиком в первую же секунду.
        var first = !_primed;
        _primed = true;

        var cpu = _cpu.Next();

        double? disk = null;
        try
        {
            var busy = _diskIdle is null ? (double?)null : Math.Clamp(100 - _diskIdle.NextValue(), 0, 100);
            disk = first ? null : busy;
        }
        catch (InvalidOperationException)
        {
            // Счётчик пропал посреди записи (диск отключили) — дальше без него.
        }

        var memory = PerformanceTool.ReadMemory() is { } info ? (double?)info.Load : null;
        return new SystemSample(now, cpu, memory, disk, Processes());
    }

    private List<ProcessSample> Processes()
    {
        var result = new List<ProcessSample>();
        var alive = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var pid = process.Id;
                if (pid == 0)
                {
                    continue;
                }

                alive.Add(pid);
                if (Handle(pid) is not { } entry ||
                    !GetProcessTimes(entry.Handle, out _, out _, out var kernel, out var user))
                {
                    continue;
                }

                long io = 0;
                if (GetProcessIoCounters(entry.Handle, out var counters))
                {
                    io = (long)(counters.ReadTransferCount + counters.WriteTransferCount + counters.OtherTransferCount);
                }

                long privateBytes;
                try
                {
                    privateBytes = process.PrivateMemorySize64;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                result.Add(new ProcessSample(pid, entry.Created, process.ProcessName, kernel + user, io, privateBytes));
            }
        }

        foreach (var pid in _handles.Keys.Where(pid => !alive.Contains(pid)).ToList())
        {
            _handles[pid].Handle.Dispose();
            _ = _handles.Remove(pid);
        }

        return result;
    }

    /// <summary>Описатель живого процесса с этим PID: завершённый заменяется новым.</summary>
    private (SafeProcessHandle Handle, long Created)? Handle(int pid)
    {
        if (_handles.TryGetValue(pid, out var cached))
        {
            if (GetExitCodeProcess(cached.Handle, out var code) && code == StillActive)
            {
                return cached;
            }

            cached.Handle.Dispose();
            _ = _handles.Remove(pid);
        }

        var handle = OpenProcess(QueryLimited, false, pid);
        if (handle.IsInvalid || !GetProcessTimes(handle, out var created, out _, out _, out _))
        {
            handle.Dispose();
            return null;
        }

        _handles[pid] = (handle, created);
        return _handles[pid];
    }

    public void Dispose()
    {
        foreach (var (handle, _) in _handles.Values)
        {
            handle.Dispose();
        }

        _handles.Clear();
        _diskIdle?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
}
