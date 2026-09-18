namespace Amarin.Core;

/// <summary>
/// Собирает отчёт о тратах: читает кэш, дотягивает недостающее у Venice, складывает точки.
/// </summary>
/// <remarks>
/// Источник истины — журнал самого Venice (<c>billing/usage-history</c>), а не внутренний
/// счётчик программы. В журнал попадает всё, за что списали: ответы, придуманные заголовки
/// чатов, скрытые сводки, поиск в сети, картинки — и вдобавок то, чего программа у себя не
/// видит: неудачные попытки из цепочки замен модели списываются, а до <c>AddCost</c> не
/// доходят. Заодно журнал знает про траты, сделанные вообще не отсюда, и умеет отвечать
/// отдельно по каждому ключу.
/// </remarks>
internal sealed class SpendService
{
    /// <summary>
    /// Перехлёст назад при догрузке. Venice изредка проводит списание задним числом, и без
    /// него такая запись не попала бы в журнал никогда.
    /// </summary>
    private static readonly TimeSpan Overlap = TimeSpan.FromHours(6);

    /// <summary>
    /// Чаще этого за журналом не ходим: страница открывается по каждому клику в настройках,
    /// а меняется журнал в лучшем случае раз в минуту. Кнопка «обновить» этот запрет обходит.
    /// </summary>
    private static readonly TimeSpan Throttle = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Потолок обхода: тысяча записей на страницу, двадцать страниц. Дальше страница настроек
    /// уходила бы в многоминутную выкачку, которую человек не заказывал.
    /// </summary>
    private const int MaxPages = 20;

    private readonly VeniceClient _venice;
    private readonly SpendHistoryStore _store;
    private readonly SpendLedger _ledger;
    private readonly Dictionary<string, DateTime> _lastSync = new(StringComparer.Ordinal);

    /// <summary>
    /// Ключи, по которым Venice отказал в журнале: он открыт только админ-ключам.
    /// </summary>
    /// <remarks>
    /// Запоминаем, чтобы не биться в закрытую дверь при каждой смене отрезка: ответ по одному
    /// и тому же ключу не изменится до перезапуска программы.
    /// </remarks>
    private readonly HashSet<string> _needsAdminKey = new(StringComparer.Ordinal);

    public SpendService(VeniceClient venice, SpendHistoryStore store, SpendLedger ledger)
    {
        _venice = venice;
        _store = store;
        _ledger = ledger;
    }

    /// <summary>Каталог моделей для разбора sku. Ставится снаружи, как у <see cref="VeniceClient"/>.</summary>
    public Func<IReadOnlyList<VeniceModelInfo>?>? ResolveModels { get; set; }

    public async Task<SpendReport> GetReportAsync(
        string? secret,
        SpendPeriod period,
        bool force,
        CancellationToken cancellationToken = default)
    {
        var today = DateTime.Now;
        if (string.IsNullOrWhiteSpace(secret))
        {
            return new SpendReport
            {
                Status = SpendStatus.NoKey,
                Period = period
            };
        }

        var models = ResolveModels?.Invoke();

        // Ключ уже отказал — идём сразу в свой журнал, не тревожа сеть впустую.
        if (_needsAdminKey.Contains(VeniceKeyStore.Fingerprint(secret)))
        {
            return LocalReport(secret, period, today, models);
        }

        var file = _store.Load(secret);
        if (!force && !DueForSync(secret))
        {
            return SpendPeriods.Build(file, period, today, models);
        }

        try
        {
            await SyncAsync(secret, file, period, today, cancellationToken).ConfigureAwait(false);
            _lastSync[VeniceKeyStore.Fingerprint(secret)] = DateTime.UtcNow;
            _store.Save(secret, file);
            return SpendPeriods.Build(file, period, today, models);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (VeniceAdminKeyRequiredException)
        {
            // Не поломка, а свойство ключа: журнал Venice открыт только админ-ключам, а для
            // запросов к моделям человек держит обычный. Свой журнал у программы есть.
            _needsAdminKey.Add(VeniceKeyStore.Fingerprint(secret));
            return LocalReport(secret, period, today, models);
        }
        catch (Exception exception) when (exception is VeniceApiException or HttpRequestException)
        {
            // Отказ сети — обычный ответ, а не авария. Есть что показать из кэша — показываем
            // и говорим, что данные сохранённые; нет — отдаём свой журнал, он всегда под рукой.
            if (file.Days.Count > 0)
            {
                return SpendPeriods.Build(
                    file, period, today, models, SpendStatus.Stale, exception.Message);
            }

            var local = LocalReport(secret, period, today, models);
            return local.TotalUsd > 0 || local.TotalDiem > 0
                ? local
                : SpendPeriods.Build(file, period, today, models, SpendStatus.Failed, exception.Message);
        }
    }

    /// <summary>Отчёт по собственному журналу программы.</summary>
    private SpendReport LocalReport(
        string secret,
        SpendPeriod period,
        DateTime today,
        IReadOnlyList<VeniceModelInfo>? models)
    {
        _ledger.Flush();
        return SpendPeriods.Build(
            _ledger.Read(secret), period, today, models, SpendStatus.Local);
    }

    /// <summary>
    /// Полночь этих суток как мгновение.
    /// </summary>
    /// <remarks>
    /// Даты в корзинах местные и приходят с <c>Kind=Unspecified</c> после чтения с диска, а
    /// от <c>DateTime.Now.Date</c> — с <c>Kind=Local</c>. Конструктор
    /// <see cref="DateTimeOffset"/> на этой разнице бросает, поэтому вид указывается явно.
    /// </remarks>
    private static DateTimeOffset StartOfLocalDay(DateTime localDate) =>
        new DateTimeOffset(DateTime.SpecifyKind(localDate, DateTimeKind.Local)).ToUniversalTime();

    private bool DueForSync(string secret) =>
        !_lastSync.TryGetValue(VeniceKeyStore.Fingerprint(secret), out var last) ||
        DateTime.UtcNow - last > Throttle;

    /// <summary>
    /// Дотягивает журнал от последних покрытых суток до сейчас.
    /// </summary>
    /// <remarks>
    /// Границы периода Venice принимает только на первом запросе — вместе с курсором фильтры
    /// слать нельзя, — поэтому обход продолжается одним лишь курсором.
    /// </remarks>
    private async Task SyncAsync(
        string secret,
        SpendHistoryFile file,
        SpendPeriod period,
        DateTime today,
        CancellationToken cancellationToken)
    {
        var wanted = SpendPeriods.Start(period, today);

        // Кэш уже дотягивается до начала запрошенного периода — значит нужен только хвост,
        // от последних покрытых суток. Не дотягивается (спрашивают год, а качали неделю) —
        // тянем весь период заново: отличить «не тратили» от «не смотрели» иначе нечем.
        var reachesBack = file.CoveredFrom is { } earliest && earliest <= wanted;
        var start = reachesBack && file.CoveredThrough is { } through && through > wanted
            ? through
            : wanted;
        var from = StartOfLocalDay(start) - Overlap;

        var records = new List<VeniceUsageRecord>();
        string? cursor = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var response = await _venice
                .GetUsageHistoryAsync(
                    page == 0 ? from : null,
                    null,
                    cursor,
                    pageSize: 1000,
                    apiKeyOverride: secret,
                    cancellationToken)
                .ConfigureAwait(false);

            records.AddRange(response.Data);
            cursor = response.NextCursor;
            if (string.IsNullOrWhiteSpace(cursor))
            {
                break;
            }
        }

        SpendFold.Merge(file, records, from);

        file.CoveredFrom = file.CoveredFrom is { } earlier && earlier < wanted ? earlier : wanted;

        // Вчерашние сутки — последние, что дописаны до конца. Сегодняшние ещё идут, и
        // запомнить их как покрытые значило бы потерять всё, что потратят до полуночи.
        file.CoveredThrough = today.Date.AddDays(-1);
    }
}
