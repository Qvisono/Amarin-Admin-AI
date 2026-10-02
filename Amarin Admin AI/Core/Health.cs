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

    /// <summary>Факты целыми фразами — из них собирается просьба «Разобраться».</summary>
    public List<string> Facts { get; set; } = [];

    /// <summary>
    /// Что показывает панель: подпись и значение, как в «Сведениях о системе». Пусто у снимка
    /// прежней версии — тогда панель показывает <see cref="Facts"/>.
    /// </summary>
    public List<HealthItem> Items { get; set; } = [];

    /// <summary>
    /// Готовая просьба для «Разобраться»: что проверено, что нашлось и что сделать, — без
    /// упоминания панели. Собирается из сырых данных пробы, пока они есть: в снимке их уже нет.
    /// Пусто у снимка прежней версии — тогда просьба собирается из фактов.
    /// </summary>
    public string? Ask { get; set; }
}

/// <summary>Строка панели: подпись слева, значение справа.</summary>
/// <remarks>
/// Прежде панель показывала факты фразами («Процессор загружен на 59%») с цветной плашкой статуса
/// у каждой области — и читалась как отчёт, а не как сводка. Теперь цвет есть только у значения,
/// которое требует внимания, а всё в порядке — обычным текстом.
/// </remarks>
public sealed class HealthItem
{
    public string Label { get; set; } = "";

    public string Value { get; set; } = "";

    public HealthStatus Status { get; set; } = HealthStatus.Ok;

    /// <summary>Занятая доля 0…1 — полоска, как у дисков в Проводнике; null — без полоски.</summary>
    public double? Used { get; set; }
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
            card.Items.Add(new HealthItem
            {
                Label = drive.Name,
                Value = Loc.Format("S.Health.Item.DriveFree", Size(drive.FreeBytes), Size(drive.TotalBytes)),
                Status = status,
                Used = Math.Clamp(1 - drive.FreePercent / 100, 0, 1)
            });
        }

        var disksLabel = Loc.Get("S.Health.Item.PhysicalDisks");
        if (physical is null)
        {
            card.Facts.Add(Loc.Get("S.Health.SmartUnknown"));
            card.Items.Add(new HealthItem { Label = disksLabel, Value = Loc.Get("S.Health.Item.Unknown"), Status = HealthStatus.Unknown });
        }
        else
        {
            foreach (var disk in physical.Where(disk => !disk.Healthy))
            {
                card.Status = HealthStatus.Problem;
                card.Facts.Add(Loc.Format("S.Health.DiskUnhealthy", disk.Name, disk.HealthStatus, disk.OperationalStatus));

                // Состояние — словами Windows («Warning, Predictive Failure»): их и стоит искать.
                card.Items.Add(new HealthItem
                {
                    Label = disk.Name,
                    Value = string.Join(", ", new[] { disk.HealthStatus, disk.OperationalStatus }.Where(part => !string.IsNullOrWhiteSpace(part))),
                    Status = HealthStatus.Problem
                });
            }

            if (physical.Count > 0 && physical.All(disk => disk.Healthy))
            {
                card.Facts.Add(Loc.Format("S.Health.SmartOk", physical.Count));
                card.Items.Add(new HealthItem { Label = disksLabel, Value = Loc.Get("S.Health.Item.Healthy") });
            }
        }

        var task = physical?.Any(disk => !disk.Healthy) == true ? "S.Health.Ask.DisksFailing" : "S.Health.Ask.DisksSpace";
        card.Ask = Request("S.Health.Ask.DisksIntro", card.Facts, task);
        return card;
    }

    public static HealthCard System(SystemHealth system)
    {
        var card = new HealthCard { Area = HealthArea.System, Status = HealthStatus.Ok };
        if (system.CpuPercent is { } cpu)
        {
            var busy = cpu >= CpuAttentionPercent;
            card.Facts.Add(Loc.Format("S.Health.Cpu", Math.Round(cpu).ToString(CultureInfo.InvariantCulture)));
            card.Items.Add(new HealthItem { Label = Loc.Get("S.Health.Item.Cpu"), Value = Percent(cpu), Status = busy ? HealthStatus.Attention : HealthStatus.Ok });
            if (busy)
            {
                card.Status = HealthStatus.Attention;
            }
        }

        if (system.MemoryLoad is { } memory)
        {
            var full = memory >= MemoryAttentionLoad;
            card.Facts.Add(Loc.Format("S.Health.Memory", memory));
            card.Items.Add(new HealthItem { Label = Loc.Get("S.Health.Item.Memory"), Value = Percent(memory), Status = full ? HealthStatus.Attention : HealthStatus.Ok });
            if (full)
            {
                card.Status = HealthStatus.Attention;
            }
        }

        var days = system.Uptime.TotalDays;
        var wholeDays = Math.Floor(days).ToString(CultureInfo.InvariantCulture);
        var stale = days >= UptimeAttentionDays;
        card.Facts.Add(Loc.Format("S.Health.Uptime", wholeDays));
        card.Items.Add(new HealthItem
        {
            Label = Loc.Get("S.Health.Item.Uptime"),
            Value = Loc.Format("S.Health.Item.Days", wholeDays),
            Status = stale ? HealthStatus.Attention : HealthStatus.Ok
        });
        if (stale)
        {
            card.Status = Worse(card.Status, HealthStatus.Attention);
            card.Facts.Add(Loc.Get("S.Health.UptimeLong"));
        }

        card.Ask = Request("S.Health.Ask.SystemIntro", card.Facts,
            days >= UptimeAttentionDays ? "S.Health.Ask.SystemUptime" : "S.Health.Ask.SystemLoad");
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
        var defenderLabel = Loc.Get("S.Health.Item.Defender");
        if (!defender.Available)
        {
            // Другой антивирус — не беда; просто не наше дело его оценивать.
            card.Facts.Add(Loc.Get("S.Health.DefenderAbsent"));
            card.Items.Add(new HealthItem { Label = defenderLabel, Value = Loc.Get("S.Health.Item.DefenderAbsent"), Status = HealthStatus.Unknown });
        }
        else
        {
            if (defender.AntivirusEnabled == false || defender.RealTimeEnabled == false)
            {
                card.Status = HealthStatus.Problem;
                card.Facts.Add(Loc.Get("S.Health.DefenderOff"));
                card.Items.Add(new HealthItem { Label = defenderLabel, Value = Loc.Get("S.Health.Item.DefenderOff"), Status = HealthStatus.Problem });
            }
            else
            {
                card.Facts.Add(Loc.Get("S.Health.DefenderOn"));
                card.Items.Add(new HealthItem { Label = defenderLabel, Value = Loc.Get("S.Health.Item.DefenderOn") });
            }

            if (defender.SignatureAgeDays is { } age && age > SignatureAttentionDays)
            {
                card.Status = Worse(card.Status, HealthStatus.Attention);
                card.Facts.Add(Loc.Format("S.Health.SignaturesOld", age));
                card.Items.Add(new HealthItem
                {
                    Label = Loc.Get("S.Health.Item.Signatures"),
                    Value = Loc.Format("S.Health.Item.SignaturesAge", age),
                    Status = HealthStatus.Attention
                });
            }
        }

        var off = security.Firewall.Where(profile => !profile.Enabled).Select(profile => profile.Profile).ToList();
        var firewallLabel = Loc.Get("S.Health.Item.Firewall");
        if (off.Count > 0)
        {
            card.Status = HealthStatus.Problem;
            card.Facts.Add(Loc.Format("S.Health.FirewallOff", string.Join(", ", off)));
            card.Items.Add(new HealthItem
            {
                Label = firewallLabel,
                Value = Loc.Format("S.Health.Item.FirewallOff", string.Join(", ", off)),
                Status = HealthStatus.Problem
            });
        }
        else if (security.Firewall.Count > 0)
        {
            card.Facts.Add(Loc.Get("S.Health.FirewallOn"));
            card.Items.Add(new HealthItem { Label = firewallLabel, Value = Loc.Get("S.Health.Item.FirewallOn") });
        }

        card.Ask = Request("S.Health.Ask.SecurityIntro", card.Facts, "S.Health.Ask.SecurityTask");
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
        card.Items.Add(new HealthItem
        {
            Label = Loc.Get("S.Health.Item.Errors"),
            Value = Count(events.Errors),
            Status = events.Errors > ErrorsAttention ? HealthStatus.Attention : HealthStatus.Ok
        });
        card.Items.Add(new HealthItem
        {
            Label = Loc.Get("S.Health.Item.Critical"),
            Value = Count(events.Critical),
            Status = events.Critical > 0 ? HealthStatus.Problem : HealthStatus.Ok
        });
        if (events.AppCrashes > 0)
        {
            card.Facts.Add(Loc.Format("S.Health.AppCrashes", events.AppCrashes));
            card.Items.Add(new HealthItem { Label = Loc.Get("S.Health.Item.Crashes"), Value = Count(events.AppCrashes), Status = HealthStatus.Attention });
        }

        // Журналы и окно времени названы прямо: модель не видит панели и иначе гадала бы,
        // откуда эти числа и за какой срок.
        var lines = new List<string> { Loc.Format("S.Health.Ask.EventCounts", events.Critical, events.Errors) };
        if (events.AppCrashes > 0)
        {
            lines.Add(Loc.Format("S.Health.Ask.AppCrashes", events.AppCrashes));
        }

        card.Ask = Request("S.Health.Ask.StabilityIntro", lines, "S.Health.Ask.StabilityTask");
        return card;
    }

    public static HealthCard Updates(UpdateHealth updates)
    {
        var card = new HealthCard { Area = HealthArea.Updates, Status = HealthStatus.Ok };
        var pendingLabel = Loc.Get("S.Health.Item.Pending");
        if (updates.Pending is { } pending)
        {
            card.Facts.Add(pending == 0 ? Loc.Get("S.Health.UpdatesNone") : Loc.Format("S.Health.UpdatesPending", pending));
            card.Items.Add(new HealthItem
            {
                Label = pendingLabel,
                Value = pending == 0 ? Loc.Get("S.Health.Item.None") : Count(pending),
                Status = pending == 0 ? HealthStatus.Ok : HealthStatus.Attention
            });
            if (pending > 0)
            {
                card.Status = HealthStatus.Attention;
            }
        }
        else
        {
            card.Facts.Add(Loc.Get("S.Health.UpdatesUnknown"));
            card.Items.Add(new HealthItem { Label = pendingLabel, Value = Loc.Get("S.Health.Item.Unknown"), Status = HealthStatus.Unknown });
            card.Status = HealthStatus.Unknown;
        }

        // Ждущая перезагрузка — повод для внимания, даже когда список обновлений не прочёлся.
        if (updates.RebootPending)
        {
            card.Status = HealthStatus.Attention;
            card.Facts.Insert(0, Loc.Get("S.Health.RebootPending"));
            card.Items.Add(new HealthItem
            {
                Label = Loc.Get("S.Health.Item.Restart"),
                Value = Loc.Get("S.Health.Item.RestartNeeded"),
                Status = HealthStatus.Attention
            });
        }

        card.Ask = Request("S.Health.Ask.UpdatesIntro", card.Facts, "S.Health.Ask.UpdatesTask");
        return card;
    }

    /// <summary>
    /// Текст для «Разобраться» — новый чат начнёт с него. Модель не знает ни панели, ни её
    /// разделов, поэтому просьба сама говорит, что проверено на этом ПК, что нашлось и что
    /// сделать. Снимок прежней версии без готовой просьбы — та же форма из фактов.
    /// </summary>
    public static string Context(HealthCard card) =>
        !string.IsNullOrWhiteSpace(card.Ask)
            ? card.Ask
            : Request(Loc.Format("S.Health.Ask.FallbackIntro", Loc.Get(TitleKey(card.Area))), card.Facts, "S.Health.AskOutro", introIsText: true);

    /// <summary>Вступление, факты строками «- …», просьба.</summary>
    private static string Request(string intro, IEnumerable<string> facts, string taskKey, bool introIsText = false)
    {
        var text = new StringBuilder();
        text.AppendLine(introIsText ? intro : Loc.Get(intro));
        foreach (var fact in facts)
        {
            text.Append("- ").AppendLine(fact);
        }

        text.Append(Loc.Get(taskKey));
        return text.ToString();
    }

    public static string TitleKey(HealthArea area) => "S.Health.Area." + area;

    private static HealthStatus Worse(HealthStatus a, HealthStatus b) => a > b ? a : b;

    private static string Gb(long bytes) => (bytes / 1073741824.0).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// Объём, как его пишет Проводник: «465 ГБ», «1,8 ТБ», «4,2 ГБ». Десятые — частью строки
    /// перевода, а не культурой потока: язык интерфейса задаёт сама программа, и запятая у
    /// русского интерфейса не должна зависеть от региональных настроек Windows.
    /// </summary>
    internal static string Size(long bytes)
    {
        var gb = bytes / 1073741824.0;
        if (gb >= 1000)
        {
            var tenths = (long)Math.Round(bytes / 1099511627776.0 * 10);
            return Loc.Format("S.Health.Item.Tb", tenths / 10, tenths % 10);
        }

        if (gb >= 10)
        {
            return Loc.Format("S.Health.Item.Gb", Math.Round(gb).ToString(CultureInfo.InvariantCulture));
        }

        var small = (long)Math.Round(gb * 10);
        return Loc.Format("S.Health.Item.GbTenths", small / 10, small % 10);
    }

    private static string Percent(double value) => Math.Round(value).ToString(CultureInfo.InvariantCulture) + "%";

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
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
