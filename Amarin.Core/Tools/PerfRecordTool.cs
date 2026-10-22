using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

/// <summary>
/// Запись нагрузки за несколько минут — для «иногда тормозит», чего моментальный снимок
/// <see cref="PerformanceTool"/> не ловит.
/// </summary>
/// <remarks>
/// Запись держит вызов всю длительность: агент ждёт её, как ждёт долгий скрипт. «Стоп» её
/// прерывает. Около полутора сотен замеров на любую длительность: чаще — отчёт не точнее, а
/// процессы открываются чаще; реже — короткий пик проскочит между замерами.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PerfRecordTool : ITool
{
    public const string ToolName = "perf_record";
    public const int DefaultMinutes = 5;
    public const int MaxMinutes = 30;

    private const int TargetSamples = 150;

    private readonly Func<IPerfSampler> _sampler;
    private readonly TimeProvider _time;

    public PerfRecordTool()
        : this(() => new ProcessSampler(), TimeProvider.System)
    {
    }

    /// <param name="sampler">Источник замеров; тесты подставляют придуманные.</param>
    /// <param name="time">Часы ожидания между замерами; тесты двигают их сами.</param>
    internal PerfRecordTool(Func<IPerfSampler> sampler, TimeProvider time)
    {
        _sampler = sampler;
        _time = time;
    }

    public string Name => ToolName;

    public string Description =>
        "Records the load of the PC and of every process for a number of minutes and reports averages, peaks, the " +
        "periods of high CPU, disk or memory load with the processes behind them, memory growth and I/O. For " +
        "slowdowns that come and go; performance shows only one moment. The call waits for the whole duration.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "minutes": {
              "type": "integer",
              "description": "How long to record: 1-30 (default 5). The call waits this long"
            },
            "top": {
              "type": "integer",
              "description": "Processes per list in the report: 3-25 (default 10)"
            }
          }
        }
        """);

    /// <summary>Длительность из аргументов — её же показывает строка ожидания агента.</summary>
    public static int Minutes(JsonElement arguments) =>
        Math.Clamp(FileToolPaths.Int(arguments, "minutes") ?? DefaultMinutes, 1, MaxMinutes);

    internal static TimeSpan Interval(int minutes) =>
        TimeSpan.FromSeconds(Math.Clamp((int)Math.Round(minutes * 60.0 / TargetSamples), 2, 12));

    // Замеры синхронны и открывают процессы: весь цикл — на пуле, а не на потоке, который позвал.
    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        Task.Run(() => RecordAsync(arguments, cancellationToken), cancellationToken);

    private async Task<ToolResult> RecordAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var minutes = Minutes(arguments);
        var top = Math.Clamp(FileToolPaths.Int(arguments, "top") ?? 10, 3, 25);
        var interval = Interval(minutes);
        var recorder = new PerfRecorder(Environment.ProcessorCount);

        using var sampler = _sampler();
        var end = _time.GetUtcNow() + TimeSpan.FromMinutes(minutes);
        recorder.Add(sampler.Take(_time.GetLocalNow().DateTime));
        while (_time.GetUtcNow() < end)
        {
            await Task.Delay(interval, _time, cancellationToken).ConfigureAwait(false);
            recorder.Add(sampler.Take(_time.GetLocalNow().DateTime));
        }

        return ToolResult.Ok(recorder.Report(top, interval));
    }
}
