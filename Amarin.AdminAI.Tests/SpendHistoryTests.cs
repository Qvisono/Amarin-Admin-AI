using System.Net;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Журнал трат Venice: разбор ответа, свёртка в сутки и кэш на диске.
/// </summary>
/// <remarks>
/// Ничего из этого не видно глазами: ошибка в знаке превратит пополнение в трату, ошибка
/// в поясе пересадит вечерние траты на завтрашнюю точку, а забытое замещение при перехлёсте
/// будет удваивать вчерашний день при каждом открытии страницы.
/// </remarks>
public sealed class SpendFoldTests
{
    private static VeniceUsageRecord Record(
        string timestamp, decimal amount, string sku = "grok-4-6-llm-output-mtoken", string currency = "USD") =>
        new()
        {
            Amount = amount,
            Currency = currency,
            Sku = sku,
            Timestamp = DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture)
        };

    private static DateTimeOffset From(string local) =>
        new DateTimeOffset(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture)).ToUniversalTime();

    [Fact]
    public void A_debit_becomes_a_positive_amount_of_spending()
    {
        var file = new SpendHistoryFile();
        SpendFold.Merge(file, [Record("2026-09-10T12:00:00Z", -0.25m)], From("2026-09-01"));

        Assert.Equal(0.25m, Assert.Single(file.Days).Usd);
    }

    /// <summary>
    /// Пополнение приходит положительным. Уйди оно в траты со своим знаком — день провалился
    /// бы ниже нуля, и «сколько потрачено» превратилось бы в «сальдо».
    /// </summary>
    [Fact]
    public void A_top_up_is_not_spending()
    {
        var file = new SpendHistoryFile();
        SpendFold.Merge(
            file,
            [Record("2026-09-10T12:00:00Z", -0.25m), Record("2026-09-10T13:00:00Z", 10m)],
            From("2026-09-01"));

        Assert.Equal(0.25m, Assert.Single(file.Days).Usd);
    }

    /// <summary>
    /// Сутки местные: человек читает «вчера» по своим часам. Проверяется через пояс,
    /// а не через сегодняшнюю дату, чтобы тест не зависел от машины.
    /// </summary>
    [Fact]
    public void Records_fall_into_local_days()
    {
        var file = new SpendHistoryFile();
        var late = new DateTimeOffset(2026, 9, 10, 23, 30, 0, TimeSpan.Zero);
        SpendFold.Merge(
            file,
            [new VeniceUsageRecord { Amount = -1m, Currency = "USD", Sku = "s", Timestamp = late }],
            From("2026-09-01"));

        Assert.Equal(late.ToLocalTime().Date, Assert.Single(file.Days).Date);
    }

    /// <summary>
    /// Перехлёст назад берётся при каждой догрузке. Без замещения дней он удваивал бы
    /// вчерашний день при каждом открытии страницы.
    /// </summary>
    [Fact]
    public void Re_downloading_an_overlapping_window_does_not_double_a_day()
    {
        var file = new SpendHistoryFile();
        var records = new[] { Record("2026-09-10T12:00:00Z", -0.25m) };

        SpendFold.Merge(file, records, From("2026-09-01"));
        SpendFold.Merge(file, records, From("2026-09-01"));

        Assert.Equal(0.25m, Assert.Single(file.Days).Usd);
    }

    /// <summary>Дни до окна запроса трогать нельзя: их в этом ответе просто нет.</summary>
    [Fact]
    public void Days_before_the_window_survive_a_refresh()
    {
        var file = new SpendHistoryFile();
        SpendFold.Merge(file, [Record("2026-09-01T12:00:00Z", -1m)], From("2026-08-25"));
        SpendFold.Merge(file, [Record("2026-09-10T12:00:00Z", -2m)], From("2026-09-05"));

        Assert.Equal(2, file.Days.Count);
        Assert.Equal(3m, file.Days.Sum(day => day.Usd));
    }

    [Fact]
    public void Diem_never_mixes_into_the_dollar_column()
    {
        var file = new SpendHistoryFile();
        SpendFold.Merge(
            file,
            [Record("2026-09-10T12:00:00Z", -0.25m), Record("2026-09-10T12:05:00Z", -4m, currency: "DIEM")],
            From("2026-09-01"));

        var day = Assert.Single(file.Days);
        Assert.Equal(0.25m, day.Usd);
        Assert.Equal(4m, day.Diem);
    }

    /// <summary>Кредиты тарифа считаются в долларах — они идут в ту же колонку.</summary>
    [Fact]
    public void Bundled_credits_count_as_dollars()
    {
        var file = new SpendHistoryFile();
        SpendFold.Merge(
            file,
            [Record("2026-09-10T12:00:00Z", -0.5m, currency: "BUNDLED_CREDITS")],
            From("2026-09-01"));

        Assert.Equal(0.5m, Assert.Single(file.Days).Usd);
        Assert.Equal(0m, file.Days[0].Diem);
    }

    [Fact]
    public void Requests_are_counted_per_sku()
    {
        var file = new SpendHistoryFile();
        SpendFold.Merge(
            file,
            [
                Record("2026-09-10T12:00:00Z", -1m, "grok-4-6-llm-input-mtoken"),
                Record("2026-09-10T12:01:00Z", -2m, "grok-4-6-llm-output-mtoken"),
                Record("2026-09-10T12:02:00Z", -3m, "grok-4-6-llm-output-mtoken")
            ],
            From("2026-09-01"));

        var day = Assert.Single(file.Days);
        Assert.Equal(2, day.Skus.Count);
        Assert.Equal(3, day.Skus.Sum(sku => sku.Requests));
        Assert.Equal(6m, day.Usd);
    }

    /// <summary>Год с запасом и не больше: иначе файл растёт без конца.</summary>
    [Fact]
    public void The_cache_is_trimmed_to_its_ceiling()
    {
        var file = new SpendHistoryFile();
        for (var i = 0; i < SpendHistoryStore.MaxDays + 40; i++)
        {
            file.Days.Add(new SpendDay { Date = new DateTime(2024, 1, 1).AddDays(i), Usd = 1m });
        }

        SpendHistoryStore.Trim(file);

        Assert.Equal(SpendHistoryStore.MaxDays, file.Days.Count);

        // Обрезается начало: свежее нужнее.
        Assert.Equal(new DateTime(2024, 1, 1).AddDays(40), file.Days[0].Date);
    }
}

/// <summary>Отчёт: точки без пропусков, итоги и разбивка.</summary>
public sealed class SpendReportTests
{
    private static readonly DateTime Today = new(2026, 9, 18);

    private static SpendHistoryFile WithDays(params (string Date, decimal Usd)[] days)
    {
        var file = new SpendHistoryFile();
        foreach (var (date, usd) in days)
        {
            file.Days.Add(new SpendDay
            {
                Date = DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture),
                Usd = usd,
                Skus = [new SpendSkuBucket { Sku = "grok-4-6-llm-output-mtoken", Usd = usd, Requests = 1 }]
            });
        }

        return file;
    }

    /// <summary>
    /// Дни без трат остаются точками. Пропусти их — и ось сожмётся, а график соврёт о том,
    /// когда именно тратили.
    /// </summary>
    [Fact]
    public void Empty_days_stay_on_the_axis()
    {
        var report = SpendPeriods.Build(WithDays(("2026-09-18", 1m)), SpendPeriod.Week, Today, null);

        Assert.Equal(7, report.Points.Count);
        Assert.Equal(6, report.Points.Count(point => point.Usd == 0));
        Assert.Equal(1m, report.TotalUsd);
    }

    [Fact]
    public void Days_outside_the_period_do_not_count()
    {
        var report = SpendPeriods.Build(
            WithDays(("2026-09-18", 1m), ("2026-01-01", 99m)), SpendPeriod.Week, Today, null);

        Assert.Equal(1m, report.TotalUsd);
    }

    [Theory]
    [InlineData(SpendPeriod.Day, 1)]
    [InlineData(SpendPeriod.Week, 7)]
    [InlineData(SpendPeriod.Month, 30)]
    [InlineData(SpendPeriod.Quarter, 90)]
    [InlineData(SpendPeriod.Year, 365)]
    public void Each_period_has_a_point_per_day(SpendPeriod period, int expected) =>
        Assert.Equal(expected, SpendPeriods.Build(new SpendHistoryFile(), period, Today, null).Points.Count);

    /// <summary>
    /// Вход и выход одной модели — одна строка. Иначе у человека с единственной моделью вышло
    /// бы две, дающие в сумме итог, и он решил бы, что программа считает дважды.
    /// </summary>
    [Fact]
    public void Input_and_output_of_one_model_are_one_row()
    {
        var file = new SpendHistoryFile();
        file.Days.Add(new SpendDay
        {
            Date = Today,
            Usd = 3m,
            Skus =
            [
                new SpendSkuBucket { Sku = "grok-4-6-llm-input-mtoken", Usd = 1m, Requests = 1 },
                new SpendSkuBucket { Sku = "grok-4-6-llm-output-mtoken", Usd = 2m, Requests = 1 }
            ]
        });

        var models = new List<VeniceModelInfo> { new() { Id = "grok-4-6" } };
        var row = Assert.Single(SpendPeriods.Build(file, SpendPeriod.Week, Today, models).Models);

        Assert.Equal("Grok 4.6", row.Title);
        Assert.Equal(3m, row.Usd);
    }

    /// <summary>Разбивка обязана сходиться с итогом — иначе деньги где-то прячутся.</summary>
    [Fact]
    public void The_rows_add_up_to_the_total()
    {
        var file = new SpendHistoryFile();
        file.Days.Add(new SpendDay
        {
            Date = Today,
            Usd = 6m,
            Skus =
            [
                new SpendSkuBucket { Sku = "grok-4-6-llm-output-mtoken", Usd = 1m, Requests = 1 },
                new SpendSkuBucket { Sku = "web-search-request", Usd = 2m, Requests = 1 },
                new SpendSkuBucket { Sku = "нечто-невиданное", Usd = 3m, Requests = 1 }
            ]
        });

        var report = SpendPeriods.Build(file, SpendPeriod.Week, Today, null);

        Assert.Equal(report.TotalUsd, report.Models.Sum(row => row.Usd));
        Assert.Equal(6m, report.TotalUsd);
    }

    [Fact]
    public void Credits_are_dollars_times_a_hundred() =>
        Assert.Equal(1234m, SpendReport.ToCredits(12.34m));

    // ───────────────────────── «Всё время» ─────────────────────────
    //
    // Раньше этот отрезок был жёстким: сегодня минус потолок хранения, то есть всегда около
    // года. Человек, потративший первые деньги неделю назад, видел ось на тринадцать месяцев
    // и линию, прижатую к правому краю.

    [Fact]
    public void All_time_starts_at_the_first_spending_day()
    {
        var report = SpendPeriods.Build(
            WithDays(("2026-08-09", 2m), ("2026-09-18", 1m)), SpendPeriod.All, Today, null);

        Assert.Equal(new DateTime(2026, 8, 9), report.Points[0].Date);
        Assert.Equal(41, report.Points.Count);
        Assert.Equal(3m, report.TotalUsd);
    }

    /// <summary>
    /// Считаем по деньгам, а не по наличию записи: нулевой день в журнале заводится и сам собой,
    /// и начинать с него значило бы снова показать пустоту слева от первой траты.
    /// </summary>
    [Fact]
    public void A_zero_day_before_the_first_spending_is_not_the_left_edge()
    {
        var report = SpendPeriods.Build(
            WithDays(("2026-07-01", 0m), ("2026-09-18", 1m)), SpendPeriod.All, Today, null);

        Assert.Equal(Today, report.Points[0].Date);
    }

    /// <summary>Первая трата случилась сегодня — отрезок в один день, и это правда.</summary>
    [Fact]
    public void A_single_spending_day_gives_a_single_point() =>
        Assert.Single(SpendPeriods.Build(WithDays(("2026-09-18", 1m)), SpendPeriod.All, Today, null).Points);

    /// <summary>
    /// Трат нет вовсе — привычная недельная сетка. Поверх неё всё равно ляжет объяснение,
    /// почему пусто, а одинокая точка под ним читалась бы как поломка.
    /// </summary>
    [Fact]
    public void All_time_without_any_spending_falls_back_to_a_week()
    {
        var report = SpendPeriods.Build(new SpendHistoryFile(), SpendPeriod.All, Today, null);

        Assert.Equal(7, report.Points.Count);
        Assert.Equal(0m, report.TotalUsd);
    }

    /// <summary>
    /// Страховка от правленого руками файла: сам <c>Trim</c> старше потолка ничего не хранит,
    /// но день из позапрошлого года растянул бы ось на годы.
    /// </summary>
    [Fact]
    public void A_day_older_than_the_storage_ceiling_is_clamped()
    {
        var report = SpendPeriods.Build(
            WithDays(("2020-01-01", 5m), ("2026-09-18", 1m)), SpendPeriod.All, Today, null);

        Assert.Equal(Today.AddDays(-399), report.Points[0].Date);
        Assert.Equal(400, report.Points.Count);
    }

    /// <summary>
    /// Окно выкачки не изменилось: спрашивать журнал Venice надо во весь потолок хранения,
    /// иначе «Всё время» показало бы только то, что программа успела увидеть сама.
    /// </summary>
    [Fact]
    public void The_fetch_window_still_reaches_the_storage_ceiling() =>
        Assert.Equal(Today.AddDays(-399), SpendPeriods.Start(SpendPeriod.All, Today));
}

/// <summary>Обход страниц журнала и кэш на диске — с поддельным Venice.</summary>
public sealed class SpendServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amarin-usage-" + Guid.NewGuid().ToString("N"));

    public SpendServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Pages(params string[] bodies) : HttpMessageHandler
    {
        private int _index;

        public List<string> Urls { get; } = [];

        /// <summary>Ответ, которым отвечать вместо очередной страницы.</summary>
        public (HttpStatusCode Code, string Body)? Refusal { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.PathAndQuery);
            if (Refusal is { } refusal)
            {
                return Task.FromResult(new HttpResponseMessage(refusal.Code)
                {
                    Content = new StringContent(refusal.Body, Encoding.UTF8, "application/json")
                });
            }

            var body = _index < bodies.Length ? bodies[_index++] : """{"data":[],"nextCursor":null}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private (SpendService Service, Pages Handler) Build(params string[] bodies)
    {
        var handler = new Pages(bodies);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var venice = new VeniceClient(http, new AgentOptions { ApiKey = "key-1234567890" });
        return (new SpendService(venice, new SpendHistoryStore(_root), new SpendLedger(_root)), handler);
    }

    private (SpendService Service, Pages Handler, SpendLedger Ledger) BuildWithLedger(params string[] bodies)
    {
        var handler = new Pages(bodies);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var venice = new VeniceClient(http, new AgentOptions { ApiKey = "key-1234567890" });
        var ledger = new SpendLedger(_root);
        return (new SpendService(venice, new SpendHistoryStore(_root), ledger), handler, ledger);
    }

    [Fact]
    public async Task A_missing_key_is_reported_and_costs_no_request()
    {
        var (service, handler) = Build();

        var report = await service.GetReportAsync("", SpendPeriod.Week, force: true);

        Assert.Equal(SpendStatus.NoKey, report.Status);
        Assert.Empty(handler.Urls);
    }

    /// <summary>
    /// Venice запрещает слать фильтры вместе с курсором: границы периода уходят только
    /// в первом запросе, дальше — один курсор.
    /// </summary>
    [Fact]
    public async Task Filters_go_only_on_the_first_page()
    {
        var (service, handler) = Build(
            """{"data":[{"amount":-1,"currency":"USD","sku":"grok-4-6-llm-output-mtoken","timestamp":"2026-09-10T12:00:00Z"}],"nextCursor":"c1"}""",
            """{"data":[],"nextCursor":null}""");

        await service.GetReportAsync("key-1234567890", SpendPeriod.Week, force: true);

        Assert.Equal(2, handler.Urls.Count);
        Assert.Contains("startTimestamp=", handler.Urls[0], StringComparison.Ordinal);
        Assert.Contains("cursor=c1", handler.Urls[1], StringComparison.Ordinal);
        Assert.DoesNotContain("startTimestamp=", handler.Urls[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_walk_stops_when_the_cursor_runs_out()
    {
        var (service, handler) = Build("""{"data":[],"nextCursor":null}""");

        await service.GetReportAsync("key-1234567890", SpendPeriod.Week, force: true);

        Assert.Single(handler.Urls);
    }

    /// <summary>Второй заход подряд не ходит в сеть: журнал так часто не меняется.</summary>
    [Fact]
    public async Task A_second_look_within_the_throttle_is_served_from_the_cache()
    {
        var (service, handler) = Build("""{"data":[],"nextCursor":null}""");

        await service.GetReportAsync("key-1234567890", SpendPeriod.Week, force: true);
        var before = handler.Urls.Count;
        await service.GetReportAsync("key-1234567890", SpendPeriod.Week, force: false);

        Assert.Equal(before, handler.Urls.Count);
    }

    [Fact]
    public async Task The_cache_outlives_the_service()
    {
        var (service, _) = Build(
            """{"data":[{"amount":-2.5,"currency":"USD","sku":"grok-4-6-llm-output-mtoken","timestamp":"2026-09-10T12:00:00Z"}],"nextCursor":null}""");
        await service.GetReportAsync("key-1234567890", SpendPeriod.Year, force: true);

        // Новая служба, сеть отвечает пустотой — цифры обязаны прийти с диска.
        var (second, _) = Build("""{"data":[],"nextCursor":null}""");
        var report = await second.GetReportAsync("key-1234567890", SpendPeriod.Year, force: false);

        Assert.Equal(2.5m, report.TotalUsd);
    }

    /// <summary>
    /// Первый заход тянет неделю, второй спрашивает год. По неделе в кэше на год ответить
    /// нечем, и молчаливый ноль читался бы как «за год не потратили ни цента».
    /// </summary>
    [Fact]
    public async Task Asking_for_a_longer_period_refetches_instead_of_reporting_zero()
    {
        var older = DateTime.Now.AddDays(-100).ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

        var (service, handler) = Build(
            """{"data":[],"nextCursor":null}""",
            $$"""{"data":[{"amount":-7,"currency":"USD","sku":"grok-4-6-llm-output-mtoken","timestamp":"{{older}}"}],"nextCursor":null}""");

        await service.GetReportAsync("key-1234567890", SpendPeriod.Week, force: true);
        var report = await service.GetReportAsync("key-1234567890", SpendPeriod.Year, force: true);

        Assert.Equal(2, handler.Urls.Count);
        Assert.Equal(7m, report.TotalUsd);
    }

    [Fact]
    public void The_cache_file_is_named_after_a_fingerprint_not_the_key()
    {
        var path = new SpendHistoryStore(_root).PathFor("vk-secret-key-value");

        Assert.DoesNotContain("secret", path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".json", path, StringComparison.Ordinal);
    }
}
