using System.Text.Json;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Запись нагрузки: сводка по придуманным замерам, цикл записи на часах, которые не ждут, и
/// живой сборщик замеров этого ПК.
/// </summary>
public sealed class PerfRecordToolTests
{
    private static readonly DateTime Start = new(2026, 10, 22, 14, 0, 0);

    /// <summary>Процесс «hog» грузит одно из двух ядер наполовину во втором и третьем интервале.</summary>
    private static List<SystemSample> Samples()
    {
        const long second = 10_000_000;
        var hogTicks = new[] { 0L, 0, 2 * second, 4 * second, 4 * second };
        var cpu = new double?[] { null, 10, 95, 90, 12 };
        return [.. Enumerable.Range(0, 5).Select(i => new SystemSample(
            Start.AddSeconds(2 * i),
            cpu[i],
            50,
            i == 2 ? 99 : 5,
            [
                new ProcessSample(10, 1, "hog", hogTicks[i], i * 100L * 1024 * 1024, (100 + (i * 30)) * 1024L * 1024),
                new ProcessSample(20, 1, "idle-app", i * second / 100, 0, 50 * 1024 * 1024)
            ]))];
    }

    [Fact]
    public void The_report_names_the_spike_its_culprit_memory_growth_and_io()
    {
        var recorder = new PerfRecorder(cores: 2);
        foreach (var sample in Samples())
        {
            recorder.Add(sample);
        }

        var report = recorder.Report(top: 5, TimeSpan.FromSeconds(2));

        Assert.Contains("5 samples every 2 s", report, StringComparison.Ordinal);
        Assert.Contains("High load periods: 1", report, StringComparison.Ordinal);
        Assert.Contains("14:00:04-14:00:06", report, StringComparison.Ordinal);
        Assert.Contains("disk busy up to 99%", report, StringComparison.Ordinal);
        Assert.Contains("CPU by: hog 50%", report, StringComparison.Ordinal);

        // Средняя загрузка — по всей записи: 2 интервала по 50% из 4 — 25%.
        Assert.Contains("hog (PID 10): average 25%, peak 50%", report, StringComparison.Ordinal);
        Assert.Contains("hog (PID 10): +120 MB", report, StringComparison.Ordinal);
        Assert.DoesNotContain("idle-app (PID 20): +", report, StringComparison.Ordinal);
        Assert.Contains("hog (PID 10): 400 MB", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reused_pid_is_another_process()
    {
        var recorder = new PerfRecorder(cores: 1);
        recorder.Add(new SystemSample(Start, null, 1, 1, [new ProcessSample(7, 1, "old", 0, 0, 0)]));
        recorder.Add(new SystemSample(Start.AddSeconds(2), 1, 1, 1, [new ProcessSample(7, 2, "new", 50_000_000, 0, 0)]));

        // Новый процесс с тем же PID в первом своём замере загрузки не даёт: прошлого у него нет.
        Assert.DoesNotContain("new (PID 7): average", recorder.Report(5, TimeSpan.FromSeconds(2)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_recording_of_many_processes_fits_the_model_limit()
    {
        var recorder = new PerfRecorder(cores: 8);
        for (var i = 0; i < 150; i++)
        {
            var processes = Enumerable.Range(1, 300)
                .Select(pid => new ProcessSample(pid, 1, $"process-{pid}", i * pid * 1000L, i * pid * 4096L, (pid + i) * 1024L * 1024))
                .ToList();
            recorder.Add(new SystemSample(Start.AddSeconds(12 * i), i % 7 == 0 ? 95 : 30, i % 11 == 0 ? 95 : 40, i % 5 == 0 ? 99 : 10, processes));
        }

        Assert.True(recorder.Report(25, TimeSpan.FromSeconds(12)).Length < 12_000);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(5, 2)]
    [InlineData(10, 4)]
    [InlineData(30, 12)]
    public void About_150_samples_whatever_the_length(int minutes, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), PerfRecordTool.Interval(minutes));

    [Fact]
    public void Minutes_are_clamped()
    {
        Assert.Equal(1, PerfRecordTool.Minutes(JsonSerializer.SerializeToElement(new { minutes = 0 })));
        Assert.Equal(PerfRecordTool.MaxMinutes, PerfRecordTool.Minutes(JsonSerializer.SerializeToElement(new { minutes = 500 })));
        Assert.Equal(PerfRecordTool.DefaultMinutes, PerfRecordTool.Minutes(JsonSerializer.SerializeToElement(new { })));
    }

    [Fact]
    public async Task The_recording_samples_until_the_time_is_up()
    {
        var sampler = new ListSampler(Samples());
        var tool = new PerfRecordTool(() => sampler, new InstantTime(Start));

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { minutes = 1 }));

        Assert.True(result.Success, result.Output);
        Assert.Equal(31, sampler.Taken);
        Assert.True(sampler.Disposed);
    }

    [Fact]
    public void The_live_sampler_sees_this_process()
    {
        using var sampler = new ProcessSampler();
        _ = sampler.Take(DateTime.Now);
        Thread.Sleep(200);
        var sample = sampler.Take(DateTime.Now);

        Assert.Contains(sample.Processes, process => process.Pid == Environment.ProcessId && process.Created != 0);
        Assert.NotNull(sample.CpuPercent);
        Assert.NotNull(sample.MemoryPercent);
    }

    /// <summary>Отдаёт заготовленные замеры по кругу и считает, сколько взяли.</summary>
    private sealed class ListSampler(List<SystemSample> samples) : IPerfSampler
    {
        public int Taken { get; private set; }

        public bool Disposed { get; private set; }

        public SystemSample Take(DateTime now) => samples[Taken++ % samples.Count] with { Time = now };

        public void Dispose() => Disposed = true;
    }
}

/// <summary>Часы, на которых ожидание не ждёт: каждый таймер сразу сдвигает время и срабатывает.</summary>
internal sealed class InstantTime(DateTime start) : TimeProvider
{
    // Пояс у времени снимается: часы теста живут в UTC, какой бы Kind ни пришёл.
    private DateTimeOffset _now = new(DateTime.SpecifyKind(start, DateTimeKind.Unspecified), TimeSpan.Zero);

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            _now += dueTime;
            _ = ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        return new Silent();
    }

    private sealed class Silent : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
