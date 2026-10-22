using System.Globalization;
using System.Text;

namespace Amarin.Tools;

/// <summary>
/// Сводка записанной нагрузки: средние и пики системы, эпизоды высокой нагрузки с виновниками,
/// рост памяти и ввод-вывод процессов. Без системы — проверяется тестами на придуманных замерах.
/// </summary>
/// <remarks>
/// <para>
/// Задача записи — «иногда тормозит»: моментальный снимок ловит тормоз только если он
/// случился ровно сейчас. Поэтому главное в отчёте — эпизоды: когда нагрузка была высокой и кто
/// её давал в это время, а не только средние за всё время, в которых короткий пик тонет.
/// </para>
/// <para>
/// Средняя загрузка процесса считается по всей длительности записи, а не по его жизни: процесс,
/// проживший десять секунд на полную, нагрузил машину меньше, чем тот, что грузил её все пять
/// минут.
/// </para>
/// </remarks>
internal sealed class PerfRecorder
{
    /// <summary>С этого уровня нагрузка считается высокой и попадает в эпизод.</summary>
    internal const double CpuHigh = 80;

    internal const double DiskHigh = 90;
    internal const double MemoryHigh = 90;

    /// <summary>Столько эпизодов в отчёте: дальше — только их число.</summary>
    private const int MaxEpisodes = 8;

    /// <summary>Столько виновников запоминается на каждый замер.</summary>
    private const int PerPoint = 5;

    private sealed class Tracked(string name, int pid, long privateFirst, long ioFirst)
    {
        public string Name { get; } = name;
        public int Pid { get; } = pid;
        public double CpuSum { get; set; }
        public double CpuPeak { get; set; }
        public DateTime PeakAt { get; set; }
        public long PrivateFirst { get; } = privateFirst;
        public long PrivateLast { get; set; } = privateFirst;
        public long IoFirst { get; } = ioFirst;
        public long IoLast { get; set; } = ioFirst;
    }

    private sealed record Point(DateTime Time, double? Cpu, double? Memory, double? Disk, IReadOnlyList<(string Name, double Cpu)> ByCpu,
        IReadOnlyList<(string Name, long Io)> ByIo);

    private readonly int _cores;
    private readonly Dictionary<(int Pid, long Created), Tracked> _processes = [];
    private readonly List<Point> _points = [];
    private SystemSample? _previous;
    private int _intervals;

    public PerfRecorder(int cores) => _cores = Math.Max(1, cores);

    public void Add(SystemSample sample)
    {
        var seconds = _previous is null ? 0 : (sample.Time - _previous.Time).TotalSeconds;
        var before = _previous?.Processes
            .GroupBy(process => (process.Pid, process.Created))
            .ToDictionary(group => group.Key, group => group.First());
        var cpu = new List<(string Name, double Cpu)>();
        var io = new List<(string Name, long Io)>();
        foreach (var process in sample.Processes)
        {
            var key = (process.Pid, process.Created);
            if (!_processes.TryGetValue(key, out var tracked))
            {
                tracked = new Tracked(process.Name, process.Pid, process.PrivateBytes, process.IoBytes);
                _processes[key] = tracked;
            }

            tracked.PrivateLast = process.PrivateBytes;
            tracked.IoLast = process.IoBytes;
            if (seconds <= 0 || before is null || !before.TryGetValue(key, out var previous))
            {
                continue;
            }

            var share = Math.Clamp(100.0 * (process.CpuTicks - previous.CpuTicks) / (seconds * 1e7 * _cores), 0, 100);
            tracked.CpuSum += share;
            if (share > tracked.CpuPeak)
            {
                tracked.CpuPeak = share;
                tracked.PeakAt = sample.Time;
            }

            cpu.Add((process.Name, share));
            io.Add((process.Name, Math.Max(0, process.IoBytes - previous.IoBytes)));
        }

        if (seconds > 0)
        {
            _intervals++;
        }

        _points.Add(new Point(sample.Time, sample.CpuPercent, sample.MemoryPercent, sample.DiskBusyPercent,
            [.. cpu.OrderByDescending(item => item.Cpu).Take(PerPoint)],
            [.. io.OrderByDescending(item => item.Io).Take(PerPoint)]));
        _previous = sample;
    }

    public string Report(int top, TimeSpan interval)
    {
        var sb = new StringBuilder();
        if (_points.Count < 2)
        {
            return "Not enough samples to report.";
        }

        var first = _points[0].Time;
        var last = _points[^1].Time;
        sb.AppendLine(Invariant($"Recorded {Duration(last - first)}: {_points.Count} samples every {interval.TotalSeconds:0} s, {first:HH:mm:ss}-{last:HH:mm:ss}."));
        AppendMetric(sb, "System CPU", point => point.Cpu, CpuHigh);
        AppendMetric(sb, "Memory in use", point => point.Memory, MemoryHigh);
        AppendMetric(sb, "Disk busy", point => point.Disk, DiskHigh);

        var episodes = Episodes();
        sb.AppendLine(episodes.Count == 0
            ? Invariant($"High load periods: none (CPU under {CpuHigh:0}%, disk under {DiskHigh:0}%, memory under {MemoryHigh:0}%).")
            : $"High load periods: {episodes.Count}");
        foreach (var episode in episodes.Take(MaxEpisodes))
        {
            sb.AppendLine("  " + episode);
        }

        if (episodes.Count > MaxEpisodes)
        {
            sb.AppendLine($"  ... and {episodes.Count - MaxEpisodes} more.");
        }

        var intervals = Math.Max(1, _intervals);
        sb.AppendLine("Processes by average CPU (share of all cores over the whole recording):");
        foreach (var process in _processes.Values.Where(item => item.CpuSum > 0).OrderByDescending(item => item.CpuSum).Take(top))
        {
            sb.AppendLine(Invariant($"  {process.Name} (PID {process.Pid}): average {process.CpuSum / intervals:0.#}%, peak {process.CpuPeak:0.#}% at {process.PeakAt:HH:mm:ss}"));
        }

        var growth = _processes.Values
            .Where(item => item.PrivateLast - item.PrivateFirst >= 20L * 1024 * 1024)
            .OrderByDescending(item => item.PrivateLast - item.PrivateFirst)
            .Take(top)
            .ToList();
        sb.AppendLine(growth.Count == 0 ? "Memory growth: no process grew by 20 MB or more." : "Memory growth (private bytes, first seen to last):");
        foreach (var process in growth)
        {
            sb.AppendLine($"  {process.Name} (PID {process.Pid}): +{Size(process.PrivateLast - process.PrivateFirst)} ({Size(process.PrivateFirst)} to {Size(process.PrivateLast)})");
        }

        var busy = _processes.Values
            .Where(item => item.IoLast - item.IoFirst > 0)
            .OrderByDescending(item => item.IoLast - item.IoFirst)
            .Take(top)
            .ToList();
        if (busy.Count > 0)
        {
            sb.AppendLine("I/O during the recording (files, network and devices):");
            foreach (var process in busy)
            {
                sb.AppendLine($"  {process.Name} (PID {process.Pid}): {Size(process.IoLast - process.IoFirst)}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private void AppendMetric(StringBuilder sb, string label, Func<Point, double?> value, double high)
    {
        var points = _points.Where(point => value(point) is not null).ToList();
        if (points.Count == 0)
        {
            sb.AppendLine($"{label}: not available.");
            return;
        }

        var peak = points.MaxBy(point => value(point)!.Value)!;
        var above = points.Count(point => value(point) >= high);
        sb.AppendLine(Invariant($"{label}: average {points.Average(point => value(point)!.Value):0}%, peak {value(peak):0}% at {peak.Time:HH:mm:ss}") +
                      (above > 0 ? Invariant($"; {above} of {points.Count} samples at {high:0}% or more.") : "."));
    }

    /// <summary>Подряд идущие замеры с высокой нагрузкой — одним эпизодом, с главными виновниками.</summary>
    private List<string> Episodes()
    {
        var result = new List<string>();
        var start = -1;
        for (var i = 0; i <= _points.Count; i++)
        {
            var high = i < _points.Count && IsHigh(_points[i]);
            if (high && start < 0)
            {
                start = i;
            }
            else if (!high && start >= 0)
            {
                result.Add(Episode(_points.GetRange(start, i - start)));
                start = -1;
            }
        }

        return result;
    }

    private static bool IsHigh(Point point) =>
        point.Cpu >= CpuHigh || point.Disk >= DiskHigh || point.Memory >= MemoryHigh;

    private static string Episode(List<Point> points)
    {
        var parts = new List<string>();
        if (points.Max(point => point.Cpu ?? 0) is var cpu && cpu >= CpuHigh)
        {
            parts.Add(Invariant($"CPU up to {cpu:0}%"));
        }

        if (points.Max(point => point.Disk ?? 0) is var disk && disk >= DiskHigh)
        {
            parts.Add(Invariant($"disk busy up to {disk:0}%"));
        }

        if (points.Max(point => point.Memory ?? 0) is var memory && memory >= MemoryHigh)
        {
            parts.Add(Invariant($"memory up to {memory:0}%"));
        }

        var byCpu = points.SelectMany(point => point.ByCpu)
            .GroupBy(item => item.Name)
            .Select(group => (Name: group.Key, Cpu: group.Sum(item => item.Cpu) / points.Count))
            .Where(item => item.Cpu >= 1)
            .OrderByDescending(item => item.Cpu)
            .Take(3)
            .Select(item => Invariant($"{item.Name} {item.Cpu:0}%"));
        var byIo = points.SelectMany(point => point.ByIo)
            .GroupBy(item => item.Name)
            .Select(group => (Name: group.Key, Io: group.Sum(item => item.Io)))
            .Where(item => item.Io > 0)
            .OrderByDescending(item => item.Io)
            .Take(3)
            .Select(item => $"{item.Name} {Size(item.Io)}");

        var span = points.Count == 1
            ? Invariant($"{points[0].Time:HH:mm:ss}")
            : Invariant($"{points[0].Time:HH:mm:ss}-{points[^1].Time:HH:mm:ss}");
        return $"{span}: {string.Join(", ", parts)}; CPU by: {string.Join(", ", byCpu.DefaultIfEmpty("none"))}; I/O by: {string.Join(", ", byIo.DefaultIfEmpty("none"))}";
    }

    private static string Duration(TimeSpan span) =>
        span.TotalMinutes >= 1 ? Invariant($"{(int)span.TotalMinutes} min {span.Seconds} s") : Invariant($"{span.Seconds} s");

    internal static string Size(long bytes) =>
        bytes >= 1L << 30
            ? Invariant($"{bytes / (double)(1L << 30):0.#} GB")
            : Invariant($"{bytes / (double)(1L << 20):0.#} MB");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
