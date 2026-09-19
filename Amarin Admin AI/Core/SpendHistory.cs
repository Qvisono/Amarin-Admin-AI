using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amarin.Core;

/// <summary>Отрезок, за который человек смотрит траты.</summary>
public enum SpendPeriod
{
    Day,
    Week,
    Month,
    Quarter,
    Year,
    All
}

/// <summary>Сколько потратили в одной валюте.</summary>
/// <remarks>
/// Три кошелька Venice держатся врозь. <c>BUNDLED_CREDITS</c> считаются в долларах и потому
/// складываются с ними; <c>DIEM</c> — отдельная величина со своим курсом, которого программа
/// не знает, и в долларовый график он не подмешивается никогда.
/// </remarks>
public sealed class SpendAmount
{
    public decimal Usd { get; set; }

    public decimal Diem { get; set; }

    [JsonIgnore]
    public bool IsZero => Usd == 0 && Diem == 0;

    public void Add(SpendAmount other)
    {
        Usd += other.Usd;
        Diem += other.Diem;
    }
}

/// <summary>Траты по одному товару за один день.</summary>
public sealed class SpendSkuBucket
{
    public string Sku { get; set; } = "";

    public decimal Usd { get; set; }

    public decimal Diem { get; set; }

    public int Requests { get; set; }
}

/// <summary>Сутки трат — по местному времени человека.</summary>
public sealed class SpendDay
{
    /// <summary>Дата без времени: корзины дневные.</summary>
    public DateTime Date { get; set; }

    public decimal Usd { get; set; }

    public decimal Diem { get; set; }

    public List<SpendSkuBucket> Skus { get; set; } = [];
}

/// <summary>Содержимое файла <c>usage/&lt;отпечаток&gt;.json</c>.</summary>
public sealed class SpendHistoryFile
{
    /// <summary>
    /// Пояс, в котором собраны корзины. Разошёлся с текущим — кэш выбрасывается: пересчитать
    /// сутки из дневных корзин уже нельзя, а показывать чужие «вчера» нельзя тем более.
    /// </summary>
    public string? TimeZoneId { get; set; }

    /// <summary>
    /// Самые ранние сутки, за которые журнал вообще спрашивали. Без этой отметки нельзя
    /// отличить «в тот день не тратили» от «в тот день не смотрели»: первый заход тянет неделю,
    /// а на вопрос про год ответить по такому кэшу уже нечем.
    /// </summary>
    public DateTime? CoveredFrom { get; set; }

    /// <summary>
    /// Начало последних суток, которые выкачаны целиком. Именно суток, а не времени последней
    /// записи: сегодняшний день ещё дописывается, и запомнить его как готовый значило бы
    /// потерять всё, что потратят до полуночи.
    /// </summary>
    public DateTime? CoveredThrough { get; set; }

    /// <summary>
    /// Когда в журнал перенесли цены из уже сохранённых переписок. Не <c>null</c> — перенос
    /// был, и повторять его нельзя: те же деньги легли бы в корзины дважды.
    /// </summary>
    public DateTime? BackfilledAt { get; set; }

    public List<SpendDay> Days { get; set; } = [];
}

/// <summary>
/// Журнал трат ключа на диске.
/// </summary>
/// <remarks>
/// Venice отдаёт сырые записи — по одной на каждый товар каждого запроса; за год активной
/// работы это десятки мегабайт, которые пришлось бы перечитывать при каждом открытии
/// страницы. Поэтому на диск ложатся суточные корзины: график и разбивка по моделям строятся
/// ровно из них, а файл остаётся крошечным.
/// <para>
/// Свёртка в модель делается не здесь, а при отрисовке: sku хранится как пришёл, и правило
/// разбора можно починить, не перекачивая историю заново.
/// </para>
/// </remarks>
internal sealed class SpendHistoryStore
{
    /// <summary>Год с запасом. Дальше журнал интересен разве что бухгалтеру.</summary>
    public const int MaxDays = 400;

    /// <summary>Моделей столько не бывает; остальное в дне сходится в одну строку.</summary>
    private const int MaxSkusPerDay = 24;

    private readonly string _directory;

    public SpendHistoryStore(string? root = null) =>
        _directory = root is null ? AppPaths.UsageDirectory : Path.Combine(root, "usage");

    /// <summary>
    /// Имя файла — отпечаток ключа, а не сам ключ: имена файлов видны и в проводнике,
    /// и в любом списке процессов, который читает пути.
    /// </summary>
    public string PathFor(string? secret) =>
        Path.Combine(_directory, ApiKeyStore.Fingerprint(secret) + ".json");

    /// <summary>Никогда не бросает: повреждённый или отсутствующий файл — это пустой журнал.</summary>
    public SpendHistoryFile Load(string? secret)
    {
        try
        {
            var path = PathFor(secret);
            if (!File.Exists(path))
            {
                return new SpendHistoryFile();
            }

            var file = JsonSerializer.Deserialize<SpendHistoryFile>(
                File.ReadAllText(path), AppJson.Options) ?? new SpendHistoryFile();
            file.Days ??= [];

            // Пояс сменился — «вчера» в корзинах уже не то «вчера», которое человек увидит.
            if (!string.Equals(file.TimeZoneId, TimeZoneInfo.Local.Id, StringComparison.Ordinal))
            {
                return new SpendHistoryFile();
            }

            return file;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SpendHistoryFile();
        }
    }

    /// <summary>Best-effort: непрочитанный в следующий раз журнал просто выкачается заново.</summary>
    public void Save(string? secret, SpendHistoryFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        try
        {
            file.TimeZoneId = TimeZoneInfo.Local.Id;
            Trim(file);
            Directory.CreateDirectory(_directory);
            AppDataFile.WriteAtomic(PathFor(secret), JsonSerializer.Serialize(file, AppJson.Options));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    internal static void Trim(SpendHistoryFile file)
    {
        file.Days.Sort((left, right) => left.Date.CompareTo(right.Date));
        if (file.Days.Count > MaxDays)
        {
            file.Days.RemoveRange(0, file.Days.Count - MaxDays);
        }

        foreach (var day in file.Days)
        {
            if (day.Skus.Count <= MaxSkusPerDay)
            {
                continue;
            }

            day.Skus.Sort((left, right) => right.Usd.CompareTo(left.Usd));
            var tail = day.Skus.Skip(MaxSkusPerDay).ToList();
            day.Skus.RemoveRange(MaxSkusPerDay, day.Skus.Count - MaxSkusPerDay);
            day.Skus.Add(new SpendSkuBucket
            {
                Sku = SpendFold.OtherSku,
                Usd = tail.Sum(item => item.Usd),
                Diem = tail.Sum(item => item.Diem),
                Requests = tail.Sum(item => item.Requests)
            });
        }
    }
}

/// <summary>
/// Свёртка сырых записей Venice в суточные корзины.
/// </summary>
/// <remarks>
/// Чистая арифметика без диска и без сети — потому и проверяется без них.
/// </remarks>
internal static class SpendFold
{
    /// <summary>Корзина, куда сходятся хвосты слишком дробного дня.</summary>
    public const string OtherSku = " other";

    /// <summary>
    /// Кладёт записи в корзины, замещая те дни, которые запрос покрыл целиком.
    /// </summary>
    /// <remarks>
    /// Именно замещая, а не прибавляя: окно запроса всегда берётся с перехлёстом назад — Venice
    /// иногда проводит списание задним числом, — и без замещения перехлёст удваивал бы день
    /// при каждом открытии страницы.
    /// </remarks>
    /// <param name="fromUtc">Начало окна запроса. Дни от него и дальше пересобираются с нуля.</param>
    public static void Merge(
        SpendHistoryFile file,
        IEnumerable<VeniceUsageRecord> records,
        DateTimeOffset fromUtc)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(records);

        var touched = fromUtc.ToLocalTime().Date;
        file.Days.RemoveAll(day => day.Date >= touched);

        var byDate = new Dictionary<DateTime, SpendDay>();
        foreach (var record in records)
        {
            // Пополнения и возвраты приходят положительными. В тратах им места нет: день
            // пополнения провалился бы ниже нуля, и «сколько потрачено» стало бы «сальдо».
            if (record.Amount >= 0)
            {
                continue;
            }

            var spent = -record.Amount;

            // Местные сутки, а не UTC: человек читает «вчера» по своим часам, и вечерние
            // траты не должны садиться на завтрашнюю точку.
            var date = record.Timestamp.ToLocalTime().Date;
            if (date < touched)
            {
                continue;
            }

            if (!byDate.TryGetValue(date, out var day))
            {
                day = new SpendDay { Date = date };
                byDate[date] = day;
            }

            var sku = string.IsNullOrWhiteSpace(record.Sku) ? OtherSku : record.Sku.Trim();
            var bucket = day.Skus.FirstOrDefault(
                item => string.Equals(item.Sku, sku, StringComparison.Ordinal));
            if (bucket is null)
            {
                bucket = new SpendSkuBucket { Sku = sku };
                day.Skus.Add(bucket);
            }

            bucket.Requests++;
            if (IsDiem(record.Currency))
            {
                day.Diem += spent;
                bucket.Diem += spent;
            }
            else
            {
                day.Usd += spent;
                bucket.Usd += spent;
            }
        }

        file.Days.AddRange(byDate.Values);
        file.Days.Sort((left, right) => left.Date.CompareTo(right.Date));
    }

    /// <summary>
    /// Сводит несколько журналов в один: график по всем ключам сразу.
    /// </summary>
    /// <remarks>
    /// Сводятся именно журналы, а не готовые отчёты. У отчёта левый край периода «всё время»
    /// считается по первому непустому дню своего файла (<see cref="SpendPeriods.ChartStart"/>),
    /// и у двух ключей он разный — сложив отчёты, мы получили бы две оси вместо одной. Корзины
    /// SKU по той же причине складываются здесь: иначе одна и та же модель стояла бы в разбивке
    /// двумя строками, по одной на ключ.
    /// </remarks>
    public static SpendHistoryFile Combine(IEnumerable<SpendHistoryFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var combined = new SpendHistoryFile { TimeZoneId = TimeZoneInfo.Local.Id };
        var byDate = new Dictionary<DateTime, SpendDay>();

        foreach (var file in files)
        {
            if (file is null)
            {
                continue;
            }

            combined.CoveredFrom = Earlier(combined.CoveredFrom, file.CoveredFrom);
            combined.CoveredThrough = Later(combined.CoveredThrough, file.CoveredThrough);

            foreach (var day in file.Days)
            {
                if (!byDate.TryGetValue(day.Date, out var target))
                {
                    target = new SpendDay { Date = day.Date };
                    byDate[day.Date] = target;
                }

                target.Usd += day.Usd;
                target.Diem += day.Diem;

                foreach (var bucket in day.Skus)
                {
                    var into = target.Skus.FirstOrDefault(
                        item => string.Equals(item.Sku, bucket.Sku, StringComparison.Ordinal));
                    if (into is null)
                    {
                        into = new SpendSkuBucket { Sku = bucket.Sku };
                        target.Skus.Add(into);
                    }

                    into.Usd += bucket.Usd;
                    into.Diem += bucket.Diem;
                    into.Requests += bucket.Requests;
                }
            }
        }

        combined.Days.AddRange(byDate.Values);
        combined.Days.Sort((left, right) => left.Date.CompareTo(right.Date));
        return combined;
    }

    private static DateTime? Earlier(DateTime? left, DateTime? right) =>
        left is null ? right : right is null ? left : left < right ? left : right;

    private static DateTime? Later(DateTime? left, DateTime? right) =>
        left is null ? right : right is null ? left : left > right ? left : right;

    /// <summary>
    /// <c>BUNDLED_CREDITS</c> номинированы в долларах — они идут в ту же колонку. Отдельно
    /// живёт только DIEM.
    /// </summary>
    internal static bool IsDiem(string? currency) =>
        string.Equals(currency, "DIEM", StringComparison.OrdinalIgnoreCase);
}
