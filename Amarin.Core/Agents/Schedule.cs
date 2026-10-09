using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amarin.Core;

/// <summary>Как часто задача запускается.</summary>
public enum ScheduleKind
{
    Daily,
    Weekly,
    EveryHours,
    AtStartup
}

/// <summary>Чем кончился прогон по отметке агента в последней строке отчёта.</summary>
public enum ScheduleStatus
{
    Unknown,
    Ok,
    Attention,
    Problem,
    Failed
}

/// <summary>Задача по расписанию: что поручить агенту и когда.</summary>
public sealed class ScheduledJob
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Задание агенту — как если бы его написал человек.</summary>
    public string Prompt { get; set; } = "";

    public ScheduleKind Kind { get; set; } = ScheduleKind.Daily;

    /// <summary>Время суток «ЧЧ:мм» — для ежедневных и еженедельных.</summary>
    public string Time { get; set; } = "09:00";

    /// <summary>Дни недели для еженедельных.</summary>
    public List<DayOfWeek> Days { get; set; } = [DayOfWeek.Monday];

    public int EveryHours { get; set; } = 6;

    public bool Enabled { get; set; } = true;

    public DateTime Created { get; set; }

    public DateTime? LastRun { get; set; }

    public ScheduleStatus? LastStatus { get; set; }

    public string? LastChatId { get; set; }

    /// <summary>Потолок цены одного прогона в долларах; null — без потолка.</summary>
    public decimal? MaxCostUsd { get; set; }
}

/// <summary>
/// Сроки задач. Чистые функции от «сейчас»: часы подменяются в тестах, а не ждутся.
/// </summary>
/// <remarks>
/// Задача с пропущенными сроками (компьютер был выключен) запускается <b>один</b> раз: срок
/// считается наступившим, если последний плановый момент позже последнего прогона, — сколько
/// бы моментов ни прошло с тех пор. Новая задача отсчитывается от создания, а не от начала
/// времён: иначе созданная в полдень «ежедневно в 9:00» запустилась бы тут же.
/// </remarks>
internal static class ScheduleClock
{
    public static TimeSpan TimeOfDay(ScheduledJob job) =>
        TimeSpan.TryParseExact(job.Time?.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var time) &&
        time >= TimeSpan.Zero && time < TimeSpan.FromDays(1)
            ? time
            : new TimeSpan(9, 0, 0);

    public static int Hours(ScheduledJob job) => Math.Clamp(job.EveryHours, 1, 24 * 7);

    /// <summary>Последний плановый момент не позже <paramref name="now"/>; null — таких нет.</summary>
    public static DateTime? LastOccurrence(ScheduledJob job, DateTime now) => job.Kind switch
    {
        ScheduleKind.Daily => RecurrenceMath.LastOccurrence(TimeOfDay(job), null, now),
        ScheduleKind.Weekly => RecurrenceMath.LastOccurrence(TimeOfDay(job), job.Days ?? [], now),
        _ => null
    };

    /// <summary>Пора ли запускать задачу.</summary>
    public static bool IsDue(ScheduledJob job, DateTime now, DateTime appStartedAt)
    {
        if (!job.Enabled || string.IsNullOrWhiteSpace(job.Prompt))
        {
            return false;
        }

        var baseline = job.LastRun ?? job.Created;
        return job.Kind switch
        {
            ScheduleKind.AtStartup => (job.LastRun ?? DateTime.MinValue) < appStartedAt,
            ScheduleKind.EveryHours => now - baseline >= TimeSpan.FromHours(Hours(job)),
            _ => LastOccurrence(job, now) is { } occurrence && occurrence > baseline
        };
    }

    /// <summary>Следующий плановый момент — для подписи в списке; null — при следующем запуске программы.</summary>
    public static DateTime? Next(ScheduledJob job, DateTime now)
    {
        switch (job.Kind)
        {
            case ScheduleKind.EveryHours:
            {
                var next = (job.LastRun ?? job.Created).AddHours(Hours(job));
                return next < now ? now : next;
            }

            case ScheduleKind.Daily:
                return RecurrenceMath.NextOccurrence(TimeOfDay(job), null, now);

            case ScheduleKind.Weekly:
                return RecurrenceMath.NextOccurrence(TimeOfDay(job), job.Days ?? [], now);

            default:
                return null;
        }
    }
}

/// <summary>Что дописывается к заданию агента по расписанию, и как читается его отметка.</summary>
internal static partial class ScheduleReport
{
    /// <summary>Правило для агента — формат последней строки, без разбора случаев.</summary>
    public const string Rule = """

        This task runs on a schedule with nobody watching. Only read and inspect: nothing on this PC may be changed.
        End the report with one last line exactly in this form: STATUS: OK, STATUS: ATTENTION or STATUS: PROBLEM.
        OK means nothing needs the user; ATTENTION means something is worth a look; PROBLEM means something is wrong.
        """;

    /// <summary>Отметка из отчёта: последняя строка вида STATUS: … .</summary>
    public static ScheduleStatus Parse(string? report)
    {
        if (string.IsNullOrWhiteSpace(report))
        {
            return ScheduleStatus.Unknown;
        }

        var matches = StatusLine().Matches(report);
        if (matches.Count == 0)
        {
            return ScheduleStatus.Unknown;
        }

        return matches[^1].Groups[1].Value.ToUpperInvariant() switch
        {
            "OK" => ScheduleStatus.Ok,
            "ATTENTION" => ScheduleStatus.Attention,
            "PROBLEM" => ScheduleStatus.Problem,
            _ => ScheduleStatus.Unknown
        };
    }

    /// <summary>Отчёт без служебной строки — человеку она ни к чему.</summary>
    public static string Strip(string? report) =>
        StatusLine().Replace(report ?? "", "").TrimEnd();

    /// <summary>Нужно ли звать человека: находки или сбой.</summary>
    public static bool Notable(ScheduleStatus status) =>
        status is ScheduleStatus.Attention or ScheduleStatus.Problem or ScheduleStatus.Failed;

    [GeneratedRegex(@"^[ \t*_`]*STATUS:\s*(OK|ATTENTION|PROBLEM)\b[^\r\n]*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex StatusLine();
}

/// <summary>Строка журнала прогонов.</summary>
public sealed record ScheduleRunEntry(
    DateTime Time,
    string JobId,
    string Name,
    ScheduleStatus Status,
    decimal CostUsd,
    string? ChatId,
    string? Error = null);

/// <summary>
/// Задачи профиля (<c>schedule.json</c>) и журнал их прогонов (<c>schedule-runs.jsonl</c>).
/// </summary>
internal sealed class ScheduleBook
{
    internal const string FileName = "schedule.json";
    internal const string LogName = "schedule-runs.jsonl";

    private readonly Lock _gate = new();
    private string _root;

    public ScheduleBook(string root) => _root = root;

    public void UseRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        lock (_gate)
        {
            _root = root;
        }
    }

    private string Root
    {
        get
        {
            lock (_gate)
            {
                return _root;
            }
        }
    }

    /// <summary>Все задачи. Нет файла или он битый — пусто; никогда не бросает.</summary>
    public List<ScheduledJob> Load() => LoadFrom(Root);

    public bool Save(IReadOnlyList<ScheduledJob> jobs)
    {
        lock (_gate)
        {
            return SaveTo(_root, jobs);
        }
    }

    /// <summary>Меняет одну задачу под замком: прогон и страница пишут файл из разных мест.</summary>
    public bool Update(string id, Action<ScheduledJob> change)
    {
        lock (_gate)
        {
            var jobs = LoadFrom(_root);
            var job = jobs.FirstOrDefault(item => item.Id == id);
            if (job is null)
            {
                return false;
            }

            change(job);
            return SaveTo(_root, jobs);
        }
    }

    private static List<ScheduledJob> LoadFrom(string root)
    {
        var path = Path.Combine(root, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var jobs = JsonSerializer.Deserialize<List<ScheduledJob>>(File.ReadAllText(path), AppJson.Options) ?? [];
            return jobs.Where(job => job is not null && !string.IsNullOrWhiteSpace(job.Id)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static bool SaveTo(string root, IReadOnlyList<ScheduledJob> jobs)
    {
        try
        {
            AppDataFile.WriteAtomic(Path.Combine(root, FileName), JsonSerializer.Serialize(jobs, AppJson.Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Append(ScheduleRunEntry entry)
    {
        try
        {
            lock (_gate)
            {
                File.AppendAllText(Path.Combine(_root, LogName),
                    JsonSerializer.Serialize(entry, CompactJson) + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Журнал прогонов — справка: без строки в нём прогон всё равно лежит чатом.
        }
    }

    /// <summary>Последние прогоны, новые первыми. Битые строки пропускаются.</summary>
    public List<ScheduleRunEntry> Recent(int count)
    {
        var path = Path.Combine(Root, LogName);
        var entries = new List<ScheduleRunEntry>();
        try
        {
            if (!File.Exists(path))
            {
                return entries;
            }

            foreach (var line in File.ReadLines(path).Reverse().Take(count * 2))
            {
                try
                {
                    if (JsonSerializer.Deserialize<ScheduleRunEntry>(line, CompactJson) is { } entry)
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                }

                if (entries.Count >= count)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return entries;
    }

    private static readonly JsonSerializerOptions CompactJson = new(AppJson.Options) { WriteIndented = false };
}

/// <summary>Итог одного прогона — от того, кто его выполнял.</summary>
internal sealed record ScheduleRunOutcome(ScheduleStatus Status, decimal CostUsd, string? ChatId, string? Error = null);

/// <summary>
/// Проверяет сроки раз в минуту и запускает наступившие задачи по одной.
/// </summary>
/// <remarks>
/// По одной, а не разом: задачи идут без человека, и две проверки параллельно — это двойной
/// счёт и чужие ответы вперемешку. Отметка о прогоне ставится <b>до</b> запуска: иначе минутный
/// таймер, сработавший посреди долгого прогона, запустил бы ту же задачу второй раз.
/// </remarks>
internal sealed class ScheduleRunner : IDisposable
{
    private readonly ScheduleBook _book;
    private readonly Func<ScheduledJob, CancellationToken, Task<ScheduleRunOutcome>> _run;
    private readonly Func<DateTime> _clock;
    private readonly DateTime _startedAt;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Timer? _timer;

    public ScheduleRunner(
        ScheduleBook book,
        Func<ScheduledJob, CancellationToken, Task<ScheduleRunOutcome>> run,
        Func<DateTime>? clock = null)
    {
        _book = book;
        _run = run;
        _clock = clock ?? (() => DateTime.Now);
        _startedAt = _clock();
    }

    /// <summary>Прогон закончился — страница перечитывает список, окно показывает уведомление.</summary>
    public event Action<ScheduledJob, ScheduleRunOutcome>? Completed;

    /// <summary>
    /// Запускает таймер. Первая проверка — через <paramref name="firstDelay"/>: запуск программы и
    /// так нагружен, а пропущенная задача подождёт полминуты.
    /// </summary>
    public void Start(TimeSpan firstDelay) =>
        _timer ??= new Timer(_ => _ = TickQuietlyAsync(), null, firstDelay, TimeSpan.FromMinutes(1));

    /// <summary>Тик таймера: исключение здесь некому поймать, а следующий тик должен состояться.</summary>
    private async Task TickQuietlyAsync()
    {
        try
        {
            await TickAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Сбой одной проверки не повод гасить расписание: через минуту будет следующая.
        }
    }

    /// <summary>Одна проверка сроков: наступившие задачи — по очереди.</summary>
    internal async Task TickAsync()
    {
        if (!await _busy.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var now = _clock();
            foreach (var job in _book.Load().Where(job => ScheduleClock.IsDue(job, now, _startedAt)))
            {
                if (_stop.IsCancellationRequested)
                {
                    return;
                }

                await RunOneAsync(job).ConfigureAwait(false);
            }
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>«Запустить сейчас»: вне очереди сроков, но не параллельно идущему прогону.</summary>
    public async Task<bool> RunNowAsync(string id)
    {
        if (_book.Load().FirstOrDefault(job => job.Id == id) is not { } job || string.IsNullOrWhiteSpace(job.Prompt))
        {
            return false;
        }

        await _busy.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            await RunOneAsync(job).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task RunOneAsync(ScheduledJob job)
    {
        var started = _clock();
        _book.Update(job.Id, stored => stored.LastRun = started);
        job.LastRun = started;

        ScheduleRunOutcome outcome;
        try
        {
            outcome = await _run(job, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            outcome = new ScheduleRunOutcome(ScheduleStatus.Failed, 0m, null, ex.Message);
        }

        _book.Update(job.Id, stored =>
        {
            stored.LastStatus = outcome.Status;
            stored.LastChatId = outcome.ChatId ?? stored.LastChatId;
        });
        _book.Append(new ScheduleRunEntry(started, job.Id, job.Name, outcome.Status, outcome.CostUsd, outcome.ChatId, outcome.Error));
        Completed?.Invoke(job, outcome);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _timer?.Dispose();
        _timer = null;
    }
}

/// <summary>Готовые задачи.</summary>
internal static class ScheduleTemplates
{
    /// <summary>«Проверка здоровья ПК» — текст задания агенту.</summary>
    public static ScheduledJob HealthCheck() => new()
    {
        Name = Loc.Get("S.Schedule.HealthName"),
        Prompt = Loc.Get("S.Schedule.HealthPrompt"),
        Kind = ScheduleKind.Daily,
        Time = "09:00",
        Created = DateTime.Now
    };
}
