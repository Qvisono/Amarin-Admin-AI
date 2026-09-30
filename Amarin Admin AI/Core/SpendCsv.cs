using System.Globalization;
using System.Text;

namespace Amarin.Core;

/// <summary>Журнал одного ключа для выгрузки: чей он и что в нём.</summary>
internal sealed record SpendCsvSource(string KeyLabel, LlmProvider Provider, SpendHistoryFile File);

/// <summary>
/// Выгрузка трат в CSV (E5): день, ключ, провайдер, модель или статья, запросы, доллары, Diem.
/// </summary>
/// <remarks>
/// <para>
/// Разделитель и десятичный знак — по региональным настройкам, как у Excel при «Сохранить как
/// CSV»: в русской Windows это «;» и запятая, и файл с запятыми открылся бы одной колонкой.
/// Даты — ISO (<c>2026-09-30</c>): их узнаёт любая таблица в любом языке.
/// </para>
/// <para>
/// Названия ключей пишет человек, поэтому ячейка, начинающаяся с <c>= + - @</c>, получает
/// апостроф впереди: иначе таблица приняла бы её за формулу.
/// </para>
/// </remarks>
internal static class SpendCsv
{
    public static string Build(
        IEnumerable<SpendCsvSource> sources,
        DateTime from,
        DateTime to,
        IReadOnlyList<VeniceModelInfo>? known,
        CultureInfo culture)
    {
        var separator = Separator(culture);
        var numbers = (NumberFormatInfo)culture.NumberFormat.Clone();
        numbers.NumberGroupSeparator = "";

        var rows = new List<(DateTime Date, string Key, string Provider, string Item, string Sku, int Requests, decimal Usd, decimal Diem)>();
        foreach (var source in sources)
        {
            var provider = ProviderSpec.For(source.Provider).Name;
            foreach (var day in source.File.Days)
            {
                var date = day.Date.Date;
                if (date < from.Date || date > to.Date)
                {
                    continue;
                }

                foreach (var bucket in day.Skus)
                {
                    if (bucket.Usd == 0m && bucket.Diem == 0m && bucket.Requests == 0)
                    {
                        continue;
                    }

                    var item = VeniceSku.Title(VeniceSku.Resolve(bucket.Sku, known));
                    rows.Add((date, source.KeyLabel, provider, item, bucket.Sku, bucket.Requests, bucket.Usd, bucket.Diem));
                }
            }
        }

        var text = new StringBuilder();
        text.AppendJoin(separator, "date", "key", "provider", "item", "sku", "requests", "usd", "diem").Append("\r\n");
        foreach (var row in rows
                     .OrderBy(row => row.Date)
                     .ThenBy(row => row.Key, StringComparer.CurrentCulture)
                     .ThenByDescending(row => row.Usd))
        {
            text.AppendJoin(
                    separator,
                    row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Cell(row.Key, separator),
                    Cell(row.Provider, separator),
                    Cell(row.Item, separator),
                    Cell(row.Sku, separator),
                    row.Requests.ToString(CultureInfo.InvariantCulture),
                    row.Usd.ToString("0.######", numbers),
                    row.Diem.ToString("0.######", numbers))
                .Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// Разделитель списка из культуры, если он годится: один знак и не совпадает с десятичным.
    /// </summary>
    internal static string Separator(CultureInfo culture)
    {
        var list = culture.TextInfo.ListSeparator.Trim();
        var decimals = culture.NumberFormat.NumberDecimalSeparator;
        if (list.Length == 1 && list != decimals)
        {
            return list;
        }

        return decimals == "," ? ";" : ",";
    }

    private static string Cell(string value, string separator)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        return value.Contains(separator, StringComparison.Ordinal) ||
               value.Contains('"') ||
               value.Contains('\n') ||
               value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
