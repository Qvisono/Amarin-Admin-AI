using System.Globalization;

namespace Amarin.Core;

/// <summary>
/// Лимиты трат (E1): на весь профиль, на отдельный ключ и на один ход. Всё выключено, пока
/// человек не впишет сумму.
/// </summary>
/// <remarks>
/// Считаются только доллары из собственного журнала трат (<see cref="SpendLedger"/>): траты
/// в Diem у Venice и траты вне программы журнал не видит, а значит, и лимит их не видит.
/// </remarks>
public sealed class SpendLimits
{
    /// <summary>Все ключи профиля за сегодняшний день.</summary>
    public decimal? DayUsd { get; set; }

    /// <summary>Все ключи профиля за календарный месяц.</summary>
    public decimal? MonthUsd { get; set; }

    /// <summary>
    /// Потолок одного хода чата вместе с его агентами: дойдя до него, ход спрашивает, продолжать
    /// ли. Не запрет, а вопрос — ход с агентом законно стоит дороже обычного ответа.
    /// </summary>
    public decimal? TurnUsd { get; set; }

    /// <summary>С какой доли лимита предупреждать. Заводское — 80%.</summary>
    public int WarnPercent { get; set; } = 80;

    /// <summary>Лимиты отдельных ключей: отпечаток секрета (<see cref="ApiKeyStore.Fingerprint"/>) → суммы.</summary>
    /// <remarks>
    /// По отпечатку, а не по <c>Id</c> строки: ключ из окружения и его копия в <c>keys.json</c> —
    /// один счёт, так же разбираются и журналы трат.
    /// </remarks>
    public Dictionary<string, KeySpendLimit> Keys { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Лимит одного ключа.</summary>
public sealed class KeySpendLimit
{
    public decimal? DayUsd { get; set; }

    public decimal? MonthUsd { get; set; }
}

/// <summary>Какой лимит сработал.</summary>
internal enum SpendLimitKind
{
    ProfileDay,
    ProfileMonth,
    KeyDay,
    KeyMonth,
    Turn,
    Run
}

/// <summary>Сколько потрачено за сегодня и за месяц — по ключу запроса и по всем ключам профиля.</summary>
internal readonly record struct SpendTotals(decimal KeyDay, decimal KeyMonth, decimal ProfileDay, decimal ProfileMonth);

/// <summary>Один лимит против потраченного.</summary>
internal sealed record SpendBreach(SpendLimitKind Kind, decimal Limit, decimal Spent)
{
    public bool Reached => Spent >= Limit;
}

/// <summary>
/// Отказ по лимиту. Наследник <see cref="VeniceApiException"/>: всё, что уже умеет показать
/// отказ провайдера человеку или вернуть его модели текстом, показывает и этот.
/// </summary>
internal sealed class SpendLimitException(SpendBreach breach)
    : VeniceApiException(SpendRules.Describe(breach))
{
    public SpendBreach Breach { get; } = breach;
}

/// <summary>Чистые правила лимитов: без часов, диска и окна — их проверяют тесты напрямую.</summary>
internal static class SpendRules
{
    /// <summary>
    /// Все включённые лимиты против потраченного. Порядок — от самого узкого: сработавших
    /// может быть несколько, и назвать человеку стоит тот, который он скорее всего и ставил.
    /// </summary>
    public static IReadOnlyList<SpendBreach> Evaluate(SpendLimits? limits, string fingerprint, SpendTotals totals)
    {
        var list = new List<SpendBreach>(4);
        if (limits is null)
        {
            return list;
        }

        if (limits.Keys.TryGetValue(fingerprint, out var key))
        {
            Add(list, SpendLimitKind.KeyDay, key.DayUsd, totals.KeyDay);
            Add(list, SpendLimitKind.KeyMonth, key.MonthUsd, totals.KeyMonth);
        }

        Add(list, SpendLimitKind.ProfileDay, limits.DayUsd, totals.ProfileDay);
        Add(list, SpendLimitKind.ProfileMonth, limits.MonthUsd, totals.ProfileMonth);
        return list;
    }

    private static void Add(List<SpendBreach> list, SpendLimitKind kind, decimal? limit, decimal spent)
    {
        if (limit is > 0m)
        {
            list.Add(new SpendBreach(kind, limit.Value, spent));
        }
    }

    /// <summary>Порог предупреждения: доля лимита, за которой пора сказать.</summary>
    public static bool ShouldWarn(SpendBreach breach, int warnPercent) =>
        !breach.Reached &&
        warnPercent is > 0 and < 100 &&
        breach.Spent >= breach.Limit * warnPercent / 100m;

    /// <summary>
    /// Ключ «уже предупреждали»: вид лимита, ключ и начало периода. Новый день или месяц —
    /// новый ключ, и предупреждение прозвучит снова.
    /// </summary>
    public static string WarnKey(SpendBreach breach, string fingerprint, DateTime now) => breach.Kind switch
    {
        SpendLimitKind.KeyDay => $"key-day|{fingerprint}|{now:yyyy-MM-dd}",
        SpendLimitKind.KeyMonth => $"key-month|{fingerprint}|{now:yyyy-MM}",
        SpendLimitKind.ProfileDay => $"day|{now:yyyy-MM-dd}",
        SpendLimitKind.ProfileMonth => $"month|{now:yyyy-MM}",
        _ => $"{breach.Kind}"
    };

    /// <summary>
    /// Сумма из поля настроек. Пусто, ноль, минус и не число — «лимита нет»: нулевой лимит
    /// запретил бы любой запрос, а это делается не лимитом, а удалением ключа.
    /// </summary>
    public static decimal? ParseUsd(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = text.Trim().TrimStart('$').Trim().Replace(',', '.');
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0m
            ? decimal.Round(value, 2, MidpointRounding.AwayFromZero)
            : null;
    }

    /// <summary>Сумма обратно в поле: без знака доллара, два знака.</summary>
    public static string FormatField(decimal? value) =>
        value is > 0m ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : "";

    /// <summary>Строка отказа: её видит человек, а через отказ инструмента — и модель.</summary>
    public static string Describe(SpendBreach breach) =>
        Loc.Format(
            breach.Kind == SpendLimitKind.Turn ? "S.Limit.TurnStopped" : "S.Limit.Reached",
            KindName(breach.Kind),
            Money(breach.Limit),
            Money(breach.Spent));

    public static string KindName(SpendLimitKind kind) => Loc.Get(kind switch
    {
        SpendLimitKind.ProfileDay => "S.Limit.Kind.ProfileDay",
        SpendLimitKind.ProfileMonth => "S.Limit.Kind.ProfileMonth",
        SpendLimitKind.KeyDay => "S.Limit.Kind.KeyDay",
        SpendLimitKind.KeyMonth => "S.Limit.Kind.KeyMonth",
        SpendLimitKind.Turn => "S.Limit.Kind.Turn",
        _ => "S.Limit.Kind.Run"
    });

    /// <summary>Доллары двумя знаками: лимиты ставят в долларах и центах, а не в долях цента.</summary>
    public static string Money(decimal value) =>
        "$" + value.ToString("0.00", CultureInfo.InvariantCulture);
}

/// <summary>
/// Счётчик одного хода или одного прогона по расписанию: сколько потрачено и что уже решено.
/// </summary>
/// <remarks>
/// Отдельно от <see cref="VeniceTurnContext"/>: ход закрывает свой контекст от агента
/// (<see cref="VeniceTurnScope.Suppress"/>), чтобы цена агента не посчиталась в ответе дважды, а
/// лимиту нужна как раз полная сумма — ход с агентом и есть то, что дорого.
/// </remarks>
internal sealed class SpendMeter
{
    private readonly Lock _gate = new();
    private decimal _usd;

    /// <summary>Человека рядом нет (расписание): не спрашиваем, а отказываем.</summary>
    public bool Unattended { get; init; }

    /// <summary>Потолок прогона по расписанию (<c>ScheduledJob.MaxCostUsd</c>). Null — без потолка.</summary>
    public decimal? Cap { get; init; }

    /// <summary>Человек разрешил этому ходу идти дальше лимита периода.</summary>
    public bool LimitsWaived { get; set; }

    /// <summary>Человек ответил «остановить»: остальные запросы хода не спрашивают снова.</summary>
    public bool Stopped { get; set; }

    /// <summary>При какой сумме спросить о потолке хода в следующий раз. Null — при самом потолке.</summary>
    public decimal? NextAskAt { get; set; }

    /// <summary>Потрачено ходом, включая агентов. Под замком: инструменты раунда идут параллельно.</summary>
    public decimal Usd
    {
        get
        {
            lock (_gate)
            {
                return _usd;
            }
        }
    }

    public void Add(VeniceCost cost)
    {
        if (cost is not { Usd: > 0m })
        {
            return;
        }

        lock (_gate)
        {
            _usd += cost.Usd;
        }
    }
}

/// <summary>Ambient-счётчик хода, по образцу <see cref="VeniceTurnScope"/>.</summary>
internal static class SpendScope
{
    private static readonly AsyncLocal<SpendMeter?> CurrentMeter = new();

    public static SpendMeter? Current => CurrentMeter.Value;

    public static IDisposable Push(SpendMeter meter)
    {
        var previous = CurrentMeter.Value;
        CurrentMeter.Value = meter;
        return new Popper(() => CurrentMeter.Value = previous);
    }

    private sealed class Popper(Action restore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                restore();
            }
        }
    }
}

/// <summary>Что спросить у человека: лимит периода или потолок хода.</summary>
internal sealed record SpendQuestion(SpendBreach Breach, decimal TurnSpent);

/// <summary>
/// Проверка лимитов перед платным запросом. Один на программу; в <see cref="AgentOptions.SpendGate"/>
/// попадает его <see cref="CheckAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Лимит периода внутри хода с человеком — вопрос «Продолжить этот ход?», а не молчаливый отказ:
/// обрыв посреди работы агента оставил бы систему наполовину изменённой. Вне хода (заголовок,
/// сводка, отчёт) и в расписании — отказ без вопроса.
/// </para>
/// <para>
/// Вопросы идут по одному (<see cref="_asking"/>): у одного хода инструменты раунда платят
/// параллельно, а у программы до трёх ходов сразу, и окно уведомлений одно — новый вопрос
/// снял бы прежний ответом «нет».
/// </para>
/// </remarks>
/// <param name="settings">
/// Настройки в памяти, а не с диска: проверка стоит перед каждым платным запросом. Null — службы
/// ещё не собраны, и лимитов нет.
/// </param>
internal sealed class SpendGuard(Func<AppSettings?> settings, SpendLedger ledger)
{
    private readonly SemaphoreSlim _asking = new(1, 1);
    private readonly Lock _gate = new();
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    /// <summary>Спросить человека; true — продолжать. Ставит окно, когда оно собрано.</summary>
    public Func<SpendQuestion, CancellationToken, Task<bool>>? Ask { get; set; }

    /// <summary>Трата дошла до порога предупреждения. Раз за период на каждый лимит.</summary>
    public Action<SpendBreach>? Warned { get; set; }

    /// <summary>Часы. Подменяются в тестах: день и месяц решают, какие траты считать.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.Now;

    public async Task CheckAsync(ApiCredential credential, CancellationToken cancellationToken)
    {
        var meter = SpendScope.Current;
        if (meter?.Cap is { } cap && cap > 0m && meter.Usd >= cap)
        {
            throw new SpendLimitException(new SpendBreach(SpendLimitKind.Run, cap, meter.Usd));
        }

        var limits = settings()?.SpendLimits;
        if (limits is null)
        {
            return;
        }

        var now = Now();
        var fingerprint = ApiKeyStore.Fingerprint(credential.Secret);
        var breaches = limits.DayUsd is null && limits.MonthUsd is null && limits.Keys.Count == 0
            ? []
            : SpendRules.Evaluate(limits, fingerprint, ledger.Totals(credential.Secret, now));

        if (breaches.FirstOrDefault(breach => breach.Reached) is { } reached && meter?.LimitsWaived != true)
        {
            await AskOrRefuseAsync(meter, reached, cancellationToken, answered: () => meter!.LimitsWaived)
                .ConfigureAwait(false);
            meter!.LimitsWaived = true;
        }

        foreach (var breach in breaches)
        {
            if (!SpendRules.ShouldWarn(breach, limits.WarnPercent))
            {
                continue;
            }

            bool first;
            lock (_gate)
            {
                first = _warned.Add(SpendRules.WarnKey(breach, fingerprint, now));
            }

            if (first)
            {
                Warned?.Invoke(breach);
            }
        }

        // Потолок хода — только там, где есть кого спросить: у расписания свой потолок прогона.
        if (limits.TurnUsd is > 0m && meter is { Unattended: false })
        {
            var ceiling = limits.TurnUsd.Value;
            if (meter.Usd >= (meter.NextAskAt ?? ceiling))
            {
                var breach = new SpendBreach(SpendLimitKind.Turn, ceiling, meter.Usd);
                await AskOrRefuseAsync(meter, breach, cancellationToken, answered: () => meter.Usd < (meter.NextAskAt ?? ceiling))
                    .ConfigureAwait(false);

                // Следующий вопрос — ещё через один потолок: иначе ход спрашивал бы на каждом запросе.
                if (meter.Usd >= (meter.NextAskAt ?? ceiling))
                {
                    meter.NextAskAt = meter.Usd + ceiling;
                }
            }
        }
    }

    /// <param name="answered">
    /// Решено ли уже, пока ждали очереди: параллельный запрос того же хода мог спросить раньше,
    /// и спрашивать второй раз о том же незачем.
    /// </param>
    private async Task AskOrRefuseAsync(
        SpendMeter? meter,
        SpendBreach breach,
        CancellationToken cancellationToken,
        Func<bool> answered)
    {
        if (meter is null || meter.Unattended || meter.Stopped || Ask is not { } ask)
        {
            throw new SpendLimitException(breach);
        }

        await _asking.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (meter.Stopped)
            {
                throw new SpendLimitException(breach);
            }

            if (answered())
            {
                return;
            }

            if (!await ask(new SpendQuestion(breach, meter.Usd), cancellationToken).ConfigureAwait(false))
            {
                meter.Stopped = true;
                throw new SpendLimitException(breach);
            }
        }
        finally
        {
            _asking.Release();
        }
    }
}
