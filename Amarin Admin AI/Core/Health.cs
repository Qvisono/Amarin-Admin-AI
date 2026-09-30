using System.Globalization;
using System.Text;
using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>Итог карточки. Порядок — по серьёзности: сводка берёт худший.</summary>
public enum HealthStatus
{
    Unknown,
    Ok,
    Attention,
    Problem
}

/// <summary>Что проверяет карточка.</summary>
public enum HealthArea
{
    Disks,
    System,
    Security,
    Stability,
    Updates
}

/// <summary>Одна карточка панели: итог и факты, по которым он выставлен.</summary>
public sealed class HealthCard
{
    public HealthArea Area { get; set; }

    public HealthStatus Status { get; set; }

    /// <summary>Строки для человека — уже на языке интерфейса.</summary>
    public List<string> Facts { get; set; } = [];
}

/// <summary>Снимок панели целиком.</summary>
public sealed class HealthReport
{
    public DateTime At { get; set; }

    public List<HealthCard> Cards { get; set; } = [];

    public HealthStatus Overall => Cards.Count == 0 ? HealthStatus.Unknown : Cards.Max(card => card.Status);
}

/// <summary>
/// Пороги панели «Состояние ПК» — чистые функции от данных проб.
/// </summary>
/// <remarks>
/// Пороги нарочно простые и объяснимые одной строкой: карточка должна говорить «на C: осталось
/// 4 ГБ», а не «индекс здоровья 0,62». Неизвестное — это <see cref="HealthStatus.Unknown"/>, а не
/// «в порядке»: не сумели проверить — так и говорим.
/// </remarks>
internal static class HealthRules
{
    public const double DiskProblemPercent = 10;
    public const double DiskAttentionPercent = 20;
    public const long DiskProblemBytes = 5L << 30;
    public const long DiskAttentionBytes = 15L << 30;
    public const uint MemoryAttentionLoad = 90;
    public const double CpuAttentionPercent = 95;
    public const double UptimeAttentionDays = 14;
    public const int SignatureAttentionDays = 7;
    public const int ErrorsAttention = 20;

    public static HealthCard Disks(IReadOnlyList<DriveHealth> drives, IReadOnlyList<PhysicalDiskHealth>? physical)
    {
        var card = new HealthCard { Area = HealthArea.Disks, Status = drives.Count == 0 ? HealthStatus.Unknown : HealthStatus.Ok };
        foreach (var drive in drives)
        {
            var status = drive.FreePercent < DiskProblemPercent || drive.FreeBytes < DiskProblemBytes
                ? HealthStatus.Problem
                : drive.FreePercent < DiskAttentionPercent || drive.FreeBytes < DiskAttentionBytes
                    ? HealthStatus.Attention
                    : HealthStatus.Ok;
            card.Status = Worse(card.Status, status);
            card.Facts.Add(Loc.Format("S.Health.DriveFree", drive.Name, Gb(drive.FreeBytes), Gb(drive.TotalBytes),
                Math.Round(drive.FreePercent).ToString(CultureInfo.InvariantCulture)));
        }

        if (physical is null)
        {
            card.Facts.Add(Loc.Get("S.Health.SmartUnknown"));
        }
        else
        {
            foreach (var disk in physical.Where(disk => !disk.Healthy))
            {
                card.Status = HealthStatus.Problem;
                card.Facts.Add(Loc.Format("S.Health.DiskUnhealthy", disk.Name, disk.HealthStatus, disk.OperationalStatus));
            }

            if (physical.Count > 0 && physical.All(disk => disk.Healthy))
            {
                card.Facts.Add(Loc.Format("S.Health.SmartOk", physical.Count));
            }
        }

        return card;
    }

    public static HealthCard System(SystemHealth system)
    {
        var card = new HealthCard { Area = HealthArea.System, Status = HealthStatus.Ok };
        if (system.CpuPercent is { } cpu)
        {
            card.Facts.Add(Loc.Format("S.Health.Cpu", Math.Round(cpu).ToString(CultureInfo.InvariantCulture)));
            if (cpu >= CpuAttentionPercent)
            {
                card.Status = HealthStatus.Attention;
            }
        }

        if (system.MemoryLoad is { } memory)
        {
            card.Facts.Add(Loc.Format("S.Health.Memory", memory));
            if (memory >= MemoryAttentionLoad)
            {
                card.Status = HealthStatus.Attention;
            }
        }

        var days = system.Uptime.TotalDays;
        card.Facts.Add(Loc.Format("S.Health.Uptime", Math.Floor(days).ToString(CultureInfo.InvariantCulture)));
        if (days >= UptimeAttentionDays)
        {
            card.Status = Worse(card.Status, HealthStatus.Attention);
            card.Facts.Add(Loc.Get("S.Health.UptimeLong"));
        }

        return card;
    }

    public static HealthCard Security(SecurityHealth? security)
    {
        var card = new HealthCard { Area = HealthArea.Security, Status = HealthStatus.Unknown };
        if (security is null)
        {
            card.Facts.Add(Loc.Get("S.Health.NotChecked"));
            return card;
        }

        card.Status = HealthStatus.Ok;
        var defender = security.Defender;
        if (!defender.Available)
        {
            // Другой антивирус — не беда; просто не наше дело его оценивать.
            card.Facts.Add(Loc.Get("S.Health.DefenderAbsent"));
        }
        else
        {
            if (defender.AntivirusEnabled == false || defender.RealTimeEnabled == false)
            {
                card.Status = HealthStatus.Problem;
                card.Facts.Add(Loc.Get("S.Health.DefenderOff"));
            }
            else
            {
                card.Facts.Add(Loc.Get("S.Health.DefenderOn"));
            }

            if (defender.SignatureAgeDays is { } age && age > SignatureAttentionDays)
            {
                card.Status = Worse(card.Status, HealthStatus.Attention);
                card.Facts.Add(Loc.Format("S.Health.SignaturesOld", age));
            }
        }

        var off = security.Firewall.Where(profile => !profile.Enabled).Select(profile => profile.Profile).ToList();
        if (off.Count > 0)
        {
            card.Status = HealthStatus.Problem;
            card.Facts.Add(Loc.Format("S.Health.FirewallOff", string.Join(", ", off)));
        }
        else if (security.Firewall.Count > 0)
        {
            card.Facts.Add(Loc.Get("S.Health.FirewallOn"));
        }

        return card;
    }

    public static HealthCard Stability(EventHealth? events)
    {
        var card = new HealthCard { Area = HealthArea.Stability, Status = HealthStatus.Unknown };
        if (events is null)
        {
            card.Facts.Add(Loc.Get("S.Health.NotChecked"));
            return card;
        }

        card.Status = events.Critical > 0
            ? HealthStatus.Problem
            : events.Errors > ErrorsAttention || events.AppCrashes > 0
                ? HealthStatus.Attention
                : HealthStatus.Ok;
        card.Facts.Add(Loc.Format("S.Health.Events", events.Critical, events.Errors));
        if (events.AppCrashes > 0)
        {
            card.Facts.Add(Loc.Format("S.Health.AppCrashes", events.AppCrashes));
        }

        return card;
    }

    public static HealthCard Updates(UpdateHealth updates)
    {
        var card = new HealthCard { Area = HealthArea.Updates, Status = HealthStatus.Ok };
        if (updates.RebootPending)
        {
            card.Status = HealthStatus.Attention;
            card.Facts.Add(Loc.Get("S.Health.RebootPending"));
        }

        if (updates.Pending is { } pending)
        {
            card.Facts.Add(pending == 0 ? Loc.Get("S.Health.UpdatesNone") : Loc.Format("S.Health.UpdatesPending", pending));
            if (pending > 0)
            {
                card.Status = Worse(card.Status, HealthStatus.Attention);
            }
        }
        else
        {
            card.Facts.Add(Loc.Get("S.Health.UpdatesUnknown"));
            if (!updates.RebootPending)
            {
                card.Status = HealthStatus.Unknown;
            }
        }

        return card;
    }

    /// <summary>Контекст для «Разобраться»: что за карточка и что на ней, — чат начнёт с этого.</summary>
    public static string Context(HealthCard card)
    {
        var text = new StringBuilder();
        text.AppendLine(Loc.Format("S.Health.AskIntro", Loc.Get(TitleKey(card.Area)), Loc.Get(StatusKey(card.Status))));
        foreach (var fact in card.Facts)
        {
            text.Append("- ").AppendLine(fact);
        }

        text.Append(Loc.Get("S.Health.AskOutro"));
        return text.ToString();
    }

    public static string TitleKey(HealthArea area) => "S.Health.Area." + area;

    public static string StatusKey(HealthStatus status) => "S.Health.Status." + status;

    private static HealthStatus Worse(HealthStatus a, HealthStatus b) => a > b ? a : b;

    private static string Gb(long bytes) => (bytes / 1073741824.0).ToString("0.#", CultureInfo.InvariantCulture);
}

/// <summary>
/// Сбор панели: пять проб параллельно, каждая карточка — как только готова.
/// </summary>
/// <remarks>
/// Сбой пробы — карточка «не удалось проверить», а не падение панели: у человека с урезанным
/// PowerShell или без Защитника остальные четыре карточки по-прежнему полезны.
/// </remarks>
internal static class HealthCollector
{
    internal static Func<CancellationToken, Task<HealthCard>>[] Live() =>
    [
        async ct => HealthRules.Disks(HealthProbes.Drives(), await HealthProbes.PhysicalDisksAsync(ct).ConfigureAwait(false)),
        async ct => HealthRules.System(await HealthProbes.SystemAsync(ct).ConfigureAwait(false)),
        async ct => HealthRules.Security(await HealthProbes.SecurityAsync(ct).ConfigureAwait(false)),
        async ct => HealthRules.Stability(await HealthProbes.EventsAsync(ct).ConfigureAwait(false)),
        async ct => HealthRules.Updates(await HealthProbes.UpdatesAsync(ct).ConfigureAwait(false))
    ];

    /// <param name="ready">Зовётся с каждой готовой карточкой — с рабочего потока.</param>
    /// <param name="probes">Пробы по порядку <see cref="HealthArea"/>; null — живые. Тесты дают свои.</param>
    public static async Task<HealthReport> CollectAsync(
        Action<HealthCard>? ready,
        CancellationToken cancellationToken,
        IReadOnlyList<Func<CancellationToken, Task<HealthCard>>>? probes = null)
    {
        probes ??= Live();
        var areas = Enum.GetValues<HealthArea>();
        var tasks = probes.Select((probe, index) => Task.Run(async () =>
        {
            HealthCard card;
            try
            {
                card = await probe(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                card = new HealthCard
                {
                    Area = index < areas.Length ? areas[index] : HealthArea.System,
                    Status = HealthStatus.Unknown,
                    Facts = [Loc.Get("S.Health.NotChecked")]
                };
            }

            ready?.Invoke(card);
            return card;
        }, cancellationToken)).ToList();

        var cards = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new HealthReport { At = DateTime.Now, Cards = [.. cards.OrderBy(card => card.Area)] };
    }
}

/// <summary>Последний снимок панели — в памяти и в <c>health.json</c>, чтобы панель не открывалась пустой.</summary>
internal sealed class HealthCache
{
    internal const string FileName = "health.json";

    private readonly Lock _gate = new();
    private string _root;
    private HealthReport? _last;

    public HealthCache(string root) => _root = root;

    public void UseRoot(string root)
    {
        lock (_gate)
        {
            _root = root;
            _last = null;
        }
    }

    public HealthReport? Last
    {
        get
        {
            lock (_gate)
            {
                if (_last is not null)
                {
                    return _last;
                }

                try
                {
                    var path = Path.Combine(_root, FileName);
                    _last = File.Exists(path)
                        ? JsonSerializer.Deserialize<HealthReport>(File.ReadAllText(path), AppJson.Options)
                        : null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    _last = null;
                }

                return _last;
            }
        }
    }

    public void Store(HealthReport report)
    {
        lock (_gate)
        {
            _last = report;
            try
            {
                AppDataFile.WriteAtomic(Path.Combine(_root, FileName), JsonSerializer.Serialize(report, AppJson.Options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Кэш: не записался — в следующий раз панель просто соберётся заново.
            }
        }
    }
}
