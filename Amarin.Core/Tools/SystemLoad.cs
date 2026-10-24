using System.Runtime.InteropServices;

namespace Amarin.Tools;

/// <summary>
/// Загрузка всех ядер между двумя замерами — по <c>GetSystemTimes</c>, как в Диспетчере задач.
/// </summary>
/// <remarks>
/// Вынесено из <see cref="ProcessSampler"/> (1.37.0): живым плиткам «Состояния ПК» нужна только
/// эта цифра, а не обход трёх сотен процессов на каждом замере.
/// </remarks>
internal sealed class CpuMeter
{
    private long _idle;
    private long _total;
    private bool _primed;

    /// <summary>Загрузка с прошлого вызова, 0..100; у первого — null: точка отсчёта, а не показание.</summary>
    public double? Next()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        // Время ядра включает простой: занятость — всё, кроме простоя.
        var total = kernel + user;
        var spent = total - _total;
        double? cpu = _primed && spent > 0
            ? Math.Clamp(100.0 * (spent - (idle - _idle)) / spent, 0, 100)
            : null;

        _idle = idle;
        _total = total;
        _primed = true;
        return cpu;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}

/// <summary>Одно показание живых плиток: процессор, память и системный диск; null — не прочли.</summary>
internal sealed record SystemLoad(double? CpuPercent, double? MemoryPercent, DriveHealth? SystemDrive);

/// <summary>
/// Показания живых плиток «Состояния ПК» (1.37.0). Только то, что читается из .NET без
/// PowerShell, — замер идёт раз в две секунды, пока плашка открыта.
/// </summary>
internal sealed class SystemLoadMeter
{
    private readonly CpuMeter _cpu = new();

    public SystemLoad Take()
    {
        var cpu = _cpu.Next();
        var memory = PerformanceTool.ReadMemory() is { } info ? (double?)info.Load : null;
        return new SystemLoad(cpu, memory, SystemDrive());
    }

    private static DriveHealth? SystemDrive()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? new DriveHealth(drive.Name.TrimEnd('\\'), drive.TotalSize, drive.AvailableFreeSpace) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
