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
/// <para>
/// У провайдера журнала может не быть — см. <see cref="ProviderSpec.HasUsageHistory"/>. Тогда
/// отчёт строится из собственного журнала программы, как он строится и для обычного ключа
/// Venice, которому журнал не отдают.
/// </para>
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

    /// <summary>
    /// Отчёт по ключу Venice. Перегрузка для тех, у кого на руках только строка: до версии
    /// 1.23.0 провайдер был один, и знать его было незачем.
    /// </summary>
    public Task<SpendReport> GetReportAsync(
        string? secret,
        SpendPeriod period,
        bool force,
        CancellationToken cancellationToken = default) =>
        GetReportAsync(
            new ApiCredential(LlmProvider.Venice, secret ?? ""), period, force, cancellationToken);

    public async Task<SpendReport> GetReportAsync(
        ApiCredential credential,
        SpendPeriod period,
        bool force,
        CancellationToken cancellationToken = default)
    {
        var today = DateTime.Now;
        if (string.IsNullOrWhiteSpace(credential.Secret))
        {
            return new SpendReport
            {
                Status = SpendStatus.NoKey,
                Period = period
            };
        }

        var models = ResolveModels?.Invoke();
        var source = await SourceForAsync(credential, period, today, force, cancellationToken)
            .ConfigureAwait(false);
        return SpendPeriods.Build(source.File, period, today, models, source.Status, source.Error);
    }

    /// <summary>
    /// Отчёт сразу по нескольким ключам — режим «все ключи» на странице «Key &amp; Info».
    /// </summary>
    /// <remarks>
    /// С версии 1.23.0 платит не один ключ: у каждого слота моделей свой, и сумма по одному
    /// ключу больше не отвечает на вопрос «сколько стоит программа».
    /// <para>
    /// Ключи разбираются по отпечатку секрета: ключ из окружения и его же копия, сохранённая
    /// в <c>keys.json</c>, — это один и тот же счёт и один и тот же файл журнала, и сложив их
    /// как разные, график показал бы двойные деньги.
    /// </para>
    /// </remarks>
    public async Task<SpendReport> GetReportAsync(
        IReadOnlyList<ApiCredential> credentials,
        SpendPeriod period,
        bool force,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var today = DateTime.Now;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<ApiCredential>();
        foreach (var credential in credentials)
        {
            if (!string.IsNullOrWhiteSpace(credential.Secret) &&
                seen.Add(ApiKeyStore.Fingerprint(credential.Secret)))
            {
                unique.Add(credential);
            }
        }

        if (unique.Count == 0)
        {
            return new SpendReport { Status = SpendStatus.NoKey, Period = period };
        }

        var models = ResolveModels?.Invoke();
        var files = new List<SpendHistoryFile>(unique.Count);
        var status = SpendStatus.Ready;
        string? error = null;

        // Последовательно, а не разом: Venice ограничивает частоту запросов, и веер по всем
        // ключам сразу отвечал бы отказом ровно тогда, когда ключей стало много.
        foreach (var credential in unique)
        {
            var source = await SourceForAsync(credential, period, today, force, cancellationToken)
                .ConfigureAwait(false);
            files.Add(source.File);
            status = Worse(status, source.Status);
            error ??= source.Error;
        }

        var combined = SpendFold.Combine(files);

        // Отказ одного ключа не повод объявить пустым весь график: у остальных данные есть,
        // и «устарело» честнее, чем «не вышло».
        if (status == SpendStatus.Failed && combined.Days.Count > 0)
        {
            status = SpendStatus.Stale;
        }

        return SpendPeriods.Build(combined, period, today, models, status, error);
    }

    /// <summary>Чем хуже, тем важнее: итог по нескольким ключам берёт худшее из состояний.</summary>
    private static SpendStatus Worse(SpendStatus left, SpendStatus right) =>
        Rank(right) > Rank(left) ? right : left;

    private static int Rank(SpendStatus status) => status switch
    {
        SpendStatus.Ready => 0,
        SpendStatus.NoKey => 1,
        SpendStatus.Local => 2,
        SpendStatus.Stale => 3,
        _ => 4
    };

    /// <summary>Журнал одного ключа и то, откуда он взялся.</summary>
    private readonly record struct SpendSource(SpendHistoryFile File, SpendStatus Status, string? Error);

    /// <summary>
    /// Достаёт журнал одного ключа: из Venice, из кэша или из собственного журнала программы.
    /// </summary>
    /// <remarks>
    /// Отдельно от сборки отчёта, потому что режим «все ключи» сводит именно журналы: у готовых
    /// отчётов левый край периода «всё время» у каждого ключа свой, и сложить их в одну ось
    /// уже нельзя.
    /// </remarks>
    private async Task<SpendSource> SourceForAsync(
        ApiCredential credential,
        SpendPeriod period,
        DateTime today,
        bool force,
        CancellationToken cancellationToken)
    {
        var secret = credential.Secret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            return new SpendSource(new SpendHistoryFile(), SpendStatus.NoKey, null);
        }

        // Журнала списаний у провайдера может не быть вовсе — тогда своего журнала программы
        // не «не хватает», он единственный источник, и ходить в сеть незачем.
        if (!ProviderSpec.For(credential.Provider).HasUsageHistory)
        {
            return Local(secret);
        }

        // Ключ уже отказал — идём сразу в свой журнал, не тревожа сеть впустую.
        if (_needsAdminKey.Contains(ApiKeyStore.Fingerprint(secret)))
        {
            return Local(secret);
        }

        var file = _store.Load(secret);
        if (!force && !DueForSync(secret))
        {
            return new SpendSource(file, SpendStatus.Ready, null);
        }

        try
        {
            await SyncAsync(credential, file, period, today, cancellationToken).ConfigureAwait(false);
            _lastSync[ApiKeyStore.Fingerprint(secret)] = DateTime.UtcNow;
            _store.Save(secret, file);
            return new SpendSource(file, SpendStatus.Ready, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (VeniceAdminKeyRequiredException)
        {
            // Не поломка, а свойство ключа: журнал Venice открыт только админ-ключам, а для
            // запросов к моделям человек держит обычный. Свой журнал у программы есть.
            _needsAdminKey.Add(ApiKeyStore.Fingerprint(secret));
            return Local(secret);
        }
        catch (Exception exception) when (exception is VeniceApiException or HttpRequestException)
        {
            // Отказ сети — обычный ответ, а не авария. Есть что показать из кэша — показываем
            // и говорим, что данные сохранённые; нет — отдаём свой журнал, он всегда под рукой.
            if (file.Days.Count > 0)
            {
                return new SpendSource(file, SpendStatus.Stale, exception.Message);
            }

            var local = Local(secret);
            var money = SpendPeriods.Build(local.File, period, today, null, SpendStatus.Local);
            return money.TotalUsd > 0 || money.TotalDiem > 0
                ? local
                : new SpendSource(file, SpendStatus.Failed, exception.Message);
        }
    }

    /// <summary>Собственный журнал программы по этому ключу.</summary>
    private SpendSource Local(string secret)
    {
        _ledger.Flush();
        return new SpendSource(_ledger.Read(secret), SpendStatus.Local, null);
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
        !_lastSync.TryGetValue(ApiKeyStore.Fingerprint(secret), out var last) ||
        DateTime.UtcNow - last > Throttle;

    /// <summary>
    /// Дотягивает журнал от последних покрытых суток до сейчас.
    /// </summary>
    /// <remarks>
    /// Границы периода Venice принимает только на первом запросе — вместе с курсором фильтры
    /// слать нельзя, — поэтому обход продолжается одним лишь курсором.
    /// </remarks>
    private async Task SyncAsync(
        ApiCredential credential,
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
                    credentialOverride: credential,
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
