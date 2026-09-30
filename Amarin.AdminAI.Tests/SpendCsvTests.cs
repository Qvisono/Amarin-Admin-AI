using System.Globalization;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Выгрузка трат в CSV (E5).</summary>
public sealed class SpendCsvTests
{
    private static SpendHistoryFile File(params (DateTime Date, string Sku, decimal Usd, int Requests)[] buckets)
    {
        var file = new SpendHistoryFile();
        foreach (var group in buckets.GroupBy(bucket => bucket.Date))
        {
            file.Days.Add(new SpendDay
            {
                Date = group.Key,
                Usd = group.Sum(bucket => bucket.Usd),
                Skus = group.Select(bucket => new SpendSkuBucket { Sku = bucket.Sku, Usd = bucket.Usd, Requests = bucket.Requests }).ToList()
            });
        }

        return file;
    }

    [Fact]
    public void Rows_are_days_in_the_period_with_iso_dates_and_invariant_numbers()
    {
        var sources = new[]
        {
            new SpendCsvSource("Work, main", LlmProvider.Venice, File(
                (new DateTime(2026, 9, 1), "grok-4-6", 0.125m, 3),
                (new DateTime(2026, 9, 29), "grok-4-6", 1.5m, 10),
                (new DateTime(2026, 9, 30), "chat-title-request", 0.001m, 1))),
            new SpendCsvSource("=cmd|evil", LlmProvider.OpenRouter, File(
                (new DateTime(2026, 9, 30), "openrouter:anthropic/claude-sonnet-4.5", 0.25m, 2)))
        };

        var csv = SpendCsv.Build(sources, new DateTime(2026, 9, 29), new DateTime(2026, 9, 30), null, CultureInfo.InvariantCulture);
        var lines = csv.TrimEnd().Split("\r\n");

        Assert.Equal("date,key,provider,item,sku,requests,usd,diem", lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("2026-09-29,\"Work, main\",Venice,", lines[1]);
        Assert.EndsWith(",grok-4-6,10,1.5,0", lines[1]);

        // Название ключа, похожее на формулу, таблица формулой не сочтёт.
        Assert.Contains(",'=cmd|evil,OpenRouter,", csv);
        Assert.Contains(",0.001,0", csv);
        Assert.DoesNotContain("2026-09-01", csv);
    }

    [Fact]
    public void A_russian_windows_gets_semicolons_and_decimal_commas()
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var sources = new[] { new SpendCsvSource("Ключ", LlmProvider.Venice, File((new DateTime(2026, 9, 30), "grok-4-6", 1.25m, 1))) };

        var csv = SpendCsv.Build(sources, new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), null, culture);

        Assert.Equal(";", SpendCsv.Separator(culture));
        Assert.StartsWith("date;key;provider;", csv);
        Assert.Contains(";Ключ;Venice;", csv);
        Assert.Contains(";1;1,25;0", csv);
    }

    [Fact]
    public void The_ledger_copy_does_not_change_with_the_ledger()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-csv-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ledger = new SpendLedger(root);
            ledger.Record("key", new VeniceCost { Usd = 1m, HasData = true }, "grok-4-6");
            var copy = ledger.Copy("key");
            ledger.Record("key", new VeniceCost { Usd = 2m, HasData = true }, "grok-4-6");

            Assert.Equal(1m, copy.Days.Single().Usd);
            Assert.Equal(1, copy.Days.Single().Skus.Single().Requests);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
