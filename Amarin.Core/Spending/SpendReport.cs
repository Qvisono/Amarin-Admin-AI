namespace Amarin.Core;

/// <summary>Одна точка графика: сутки и сколько за них ушло.</summary>
public sealed record SpendPoint(DateTime Date, decimal Usd, decimal Diem, int Requests);

/// <summary>Строка разбивки: модель или служебная статья и её доля.</summary>
public sealed record SpendModelRow(string Title, string Detail, decimal Usd, decimal Diem, int Requests);

/// <summary>Чем кончилась попытка собрать отчёт.</summary>
public enum SpendStatus
{
    /// <summary>Данные из журнала самого Venice — самые полные, какие бывают.</summary>
    Ready,

    /// <summary>
    /// Данные из собственного журнала программы: Venice отдаёт свой только админ-ключу.
    /// </summary>
    Local,

    /// <summary>Ключа нет вовсе — ни своего, ни из окружения.</summary>
    NoKey,

    /// <summary>Сеть не ответила, но на диске лежит журнал прошлых заходов.</summary>
    Stale,

    /// <summary>Сеть не ответила, и показать нечего.</summary>
    Failed
}

/// <summary>Всё, что нужно странице «Key &amp; Info» для одного периода.</summary>
public sealed class SpendReport
{
    public required SpendStatus Status { get; init; }

    public required SpendPeriod Period { get; init; }

    public IReadOnlyList<SpendPoint> Points { get; init; } = [];

    public IReadOnlyList<SpendModelRow> Models { get; init; } = [];

    public decimal TotalUsd { get; init; }

    public decimal TotalDiem { get; init; }

    /// <summary>Понятная строка, когда сеть отказала. Текста исключения человек не увидит.</summary>
    public string? Error { get; init; }

    /// <summary>Кредиты — доллары, умноженные на сто.</summary>
    public static decimal ToCredits(decimal usd) => usd * 100m;

    /// <summary>
    /// Доллары в том виде, в каком их показывает страница трат.
    /// </summary>
    /// <remarks>
    /// Четыре знака у мелких сумм: типичный день стоит сотые доли цента, и «$0.00» на всех
    /// строках не сказало бы ничего. Один метод на всю страницу и на график: разойдись они,
    /// подпись у точки не сошлась бы с итогом над ней.
    /// </remarks>
    public static string FormatUsd(decimal value) =>
        value >= 1m
            ? "$" + value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            : value > 0
                ? "$" + value.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)
                : "$0";
}

/// <summary>Границы периодов и их разбор на точки.</summary>
internal static class SpendPeriods
{
    /// <summary>
    /// Начало периода по местному времени — то окно, которое догружается у Venice.
    /// <see cref="SpendPeriod.All"/> здесь означает потолок хранения: спросить журнал дальше
    /// всё равно нельзя. Левый край графика считает <see cref="ChartStart"/>, и для «Всё время»
    /// он другой.
    /// </summary>
    public static DateTime Start(SpendPeriod period, DateTime today) => period switch
    {
        SpendPeriod.Day => today.Date,
        SpendPeriod.Week => today.Date.AddDays(-6),
        SpendPeriod.Month => today.Date.AddDays(-29),
        SpendPeriod.Quarter => today.Date.AddDays(-89),
        SpendPeriod.Year => today.Date.AddDays(-364),
        _ => today.Date.AddDays(-(SpendHistoryStore.MaxDays - 1))
    };

    /// <summary>
    /// Левый край графика. Для <see cref="SpendPeriod.All"/> — день первой траты, а не потолок
    /// хранения.
    /// </summary>
    /// <remarks>
    /// Отдельно от <see cref="Start"/> намеренно: та задаёт окно выкачки у Venice, и для «Всё
    /// время» оно обязано остаться во весь потолок хранения — иначе журнал не догрузится. А на
    /// графике те же 399 дней означали, что «Всё время» всегда показывает год: сотни пустых дней
    /// перед первой тратой сжимали линию в правый край и врали о том, когда человек начал тратить.
    /// <para>
    /// Считается по дням с деньгами, а не по наличию записи: день в журнале заводится и нулевым,
    /// и начинать с него значило бы снова показать пустоту.
    /// </para>
    /// </remarks>
    public static DateTime ChartStart(SpendHistoryFile file, SpendPeriod period, DateTime today)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (period != SpendPeriod.All)
        {
            return Start(period, today);
        }

        DateTime? earliest = null;
        foreach (var day in file.Days)
        {
            if ((day.Usd > 0 || day.Diem > 0) && (earliest is null || day.Date < earliest))
            {
                earliest = day.Date;
            }
        }

        // Трат нет вовсе — показываем привычную недельную сетку: поверх неё всё равно ляжет
        // объяснение, почему пусто, а одинокая точка под ним выглядела бы поломкой.
        if (earliest is not { } first)
        {
            return Start(SpendPeriod.Week, today);
        }

        // Нижняя граница — страховка от правленого руками файла: сам Trim старше потолка ничего
        // не хранит. Верхняя — от даты из будущего, на которой цикл точек не сделал бы ни шага.
        var floor = today.Date.AddDays(-(SpendHistoryStore.MaxDays - 1));
        return first < floor ? floor : first > today.Date ? today.Date : first;
    }

    public static string LabelKey(SpendPeriod period) => period switch
    {
        SpendPeriod.Day => "S.Spend.Period.Day",
        SpendPeriod.Month => "S.Spend.Period.Month",
        SpendPeriod.Quarter => "S.Spend.Period.Quarter",
        SpendPeriod.Year => "S.Spend.Period.Year",
        SpendPeriod.All => "S.Spend.Period.All",
        _ => "S.Spend.Period.Week"
    };

    /// <summary>
    /// Собирает отчёт из суточных корзин.
    /// </summary>
    /// <remarks>
    /// Точки идут подряд, включая дни без трат: пропуск пустого дня сжал бы ось и соврал бы
    /// о том, когда именно тратили. Точка на каждый день периода и для года тоже — человек
    /// просил уметь навестись и увидеть, сколько ушло в этот день.
    /// </remarks>
    public static SpendReport Build(
        SpendHistoryFile file,
        SpendPeriod period,
        DateTime today,
        IReadOnlyList<VeniceModelInfo>? models,
        SpendStatus status = SpendStatus.Ready,
        string? error = null)
    {
        ArgumentNullException.ThrowIfNull(file);

        var from = ChartStart(file, period, today);
        var days = file.Days
            .Where(day => day.Date >= from && day.Date <= today.Date)
            .ToDictionary(day => day.Date);

        var points = new List<SpendPoint>();
        for (var date = from; date <= today.Date; date = date.AddDays(1))
        {
            points.Add(days.TryGetValue(date, out var day)
                ? new SpendPoint(date, day.Usd, day.Diem, day.Skus.Sum(sku => sku.Requests))
                : new SpendPoint(date, 0m, 0m, 0));
        }

        return new SpendReport
        {
            Status = status,
            Period = period,
            Points = points,
            Models = BuildRows(days.Values, models),
            TotalUsd = points.Sum(point => point.Usd),
            TotalDiem = points.Sum(point => point.Diem),
            Error = error
        };
    }

    /// <summary>
    /// Разбивка по моделям. Вход и выход одной модели сходятся в одну строку — иначе у человека
    /// с единственной моделью вышло бы две, дающие в сумме итог.
    /// </summary>
    private static List<SpendModelRow> BuildRows(
        IEnumerable<SpendDay> days,
        IReadOnlyList<VeniceModelInfo>? models)
    {
        var rows = new Dictionary<string, (string Title, SortedSet<string> Skus, decimal Usd, decimal Diem, int Requests)>(
            StringComparer.Ordinal);

        foreach (var bucket in days.SelectMany(day => day.Skus))
        {
            var identity = VeniceSku.Resolve(
                string.Equals(bucket.Sku, SpendFold.OtherSku, StringComparison.Ordinal) ? null : bucket.Sku,
                models);
            var key = VeniceSku.GroupKey(identity);
            var title = VeniceSku.Title(identity);

            rows.TryGetValue(key, out var row);
            row.Title = title;
            row.Skus ??= new SortedSet<string>(StringComparer.Ordinal);
            if (identity.Sku.Length > 0)
            {
                row.Skus.Add(identity.Sku);
            }

            row.Usd += bucket.Usd;
            row.Diem += bucket.Diem;
            row.Requests += bucket.Requests;
            rows[key] = row;
        }

        return [.. rows.Values
            .Select(row => new SpendModelRow(
                row.Title,

                // Сырой sku — в подсказку строки: по нему человек и поймёт, за что списали,
                // если наш разбор промахнулся, и сможет переслать строку как есть.
                string.Join("\n", row.Skus),
                row.Usd,
                row.Diem,
                row.Requests))
            .OrderByDescending(row => row.Usd)
            .ThenByDescending(row => row.Diem)];
    }
}
