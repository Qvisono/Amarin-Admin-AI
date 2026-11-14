using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Признак оценки цены сохраняется вместе с исторической суммой, без повторного пересчёта.</summary>
public sealed class PolzaSpendTests : IDisposable
{
    private const string Key = "polza-spending-test-key";
    private const string Model = "polza:openai/gpt-test";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-polza-spend-" + Guid.NewGuid().ToString("N"));

    public PolzaSpendTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static SpendDay Day(DateTime date, decimal usd, bool estimated, string sku = Model, int requests = 1) => new()
    {
        Date = date,
        Usd = usd,
        Estimated = estimated,
        Skus = [new SpendSkuBucket { Sku = sku, Usd = usd, Requests = requests, Estimated = estimated }]
    };

    private static SpendHistoryFile FileWith(params SpendDay[] days) => new() { Days = [.. days] };

    private static VeniceCost Cost(decimal usd, bool estimated) => new() { Usd = usd, HasData = true, Estimated = estimated };

    [Fact]
    public void Estimates_survive_later_exact_charges_disk_and_an_independent_copy()
    {
        var ledger = new SpendLedger(_root);
        ledger.Record(Key, Cost(2m, true), Model);
        ledger.Record(Key, Cost(1m, false), Model);
        ledger.Record(Key, Cost(4m, false), "grok-4-6");
        ledger.Flush();

        var restored = new SpendLedger(_root);
        var day = Assert.Single(restored.Read(Key).Days);
        Assert.Equal(7m, day.Usd);
        Assert.True(day.Estimated);
        Assert.True(day.Skus.Single(bucket => bucket.Sku == Model).Estimated);
        Assert.False(day.Skus.Single(bucket => bucket.Sku == "grok-4-6").Estimated);

        var copy = Assert.Single(restored.Copy(Key).Days);
        Assert.True(copy.Estimated);
        var copiedBucket = copy.Skus.Single(bucket => bucket.Sku == Model);
        Assert.True(copiedBucket.Estimated);
        copiedBucket.Estimated = false;
        Assert.True(day.Skus.Single(bucket => bucket.Sku == Model).Estimated);
    }

    [Fact]
    public void Older_journals_default_to_exact_without_a_migration()
    {
        var file = JsonSerializer.Deserialize<SpendHistoryFile>("""
            {"days":[{"date":"2026-11-01T00:00:00","usd":2,"skus":[{"sku":"model","usd":2,"requests":1}]}]}
            """, AppJson.Options)!;

        var day = Assert.Single(file.Days);
        Assert.False(day.Estimated);
        Assert.False(Assert.Single(day.Skus).Estimated);
    }

    [Fact]
    public void Import_preserves_estimates_even_when_the_selected_money_snapshot_is_exact()
    {
        var date = DateTime.Today;
        var mine = FileWith(Day(date, 2m, false, requests: 2));
        var incoming = FileWith(Day(date, 1m, true));

        Assert.Equal(1, SpendLedger.MergeInto(mine, incoming));
        Assert.Equal(2m, Assert.Single(mine.Days).Usd);
        Assert.True(mine.Days[0].Estimated);
        Assert.True(Assert.Single(mine.Days[0].Skus).Estimated);

        var richer = FileWith(Day(date, 3m, false, requests: 3));
        Assert.Equal(1, SpendLedger.MergeInto(mine, richer));
        Assert.Equal(3m, Assert.Single(mine.Days).Usd);
        Assert.True(mine.Days[0].Estimated);
        Assert.True(Assert.Single(mine.Days[0].Skus).Estimated);
        Assert.False(Assert.Single(richer.Days[0].Skus).Estimated);
    }

    [Fact]
    public void Combining_keys_and_trimming_small_skus_keep_the_estimate()
    {
        var date = DateTime.Today;
        var estimated = Day(date, 1m, true);
        var combined = SpendFold.Combine([FileWith(Day(date, 2m, false)), FileWith(estimated)]);
        var combinedDay = Assert.Single(combined.Days);
        Assert.Equal(3m, combinedDay.Usd);
        Assert.True(combinedDay.Estimated);
        Assert.True(Assert.Single(combinedDay.Skus).Estimated);

        var many = new SpendDay
        {
            Date = date,
            Skus = [.. Enumerable.Range(1, 24).Select(index => new SpendSkuBucket { Sku = "exact-" + index, Usd = index + 1m })]
        };
        many.Skus.Add(new SpendSkuBucket { Sku = Model, Usd = 0.1m, Estimated = true });
        many.Usd = many.Skus.Sum(bucket => bucket.Usd);
        SpendHistoryStore.Trim(FileWith(many));
        Assert.True(many.Estimated);
        Assert.True(many.Skus.Single(bucket => bucket.Sku == SpendFold.OtherSku).Estimated);
    }

    [Fact]
    public void The_report_marks_only_the_selected_period_points_and_model_rows()
    {
        var today = DateTime.Today;
        var file = FileWith(Day(today.AddDays(-1), 2m, true), Day(today, 1m, false, "grok-4-6"));

        var day = SpendPeriods.Build(file, SpendPeriod.Day, today, null);
        Assert.False(day.Estimated);
        Assert.Equal(new SpendPoint(today, 1m, 0m, 1), Assert.Single(day.Points));
        Assert.False(Assert.Single(day.Models).Estimated);

        var week = SpendPeriods.Build(file, SpendPeriod.Week, today, null);
        Assert.Equal(3m, week.TotalUsd);
        Assert.True(week.Estimated);
        Assert.True(week.Points.Single(point => point.Date == today.AddDays(-1)).Estimated);
        Assert.False(week.Points.Single(point => point.Date == today).Estimated);
        Assert.True(week.Models.Single(row => row.Detail.Contains(Model, StringComparison.Ordinal)).Estimated);
        Assert.False(week.Models.Single(row => row.Detail.Contains("grok-4-6", StringComparison.Ordinal)).Estimated);
    }

    [Fact]
    public void Backfill_and_duplicate_repair_keep_the_source_flag_of_a_mixed_sku()
    {
        var ledger = new SpendLedger(_root);
        var session = new ChatSession { Id = "estimated-chat" };
        session.Messages.Add(new ChatDisplayMessage
        {
            Role = "assistant",
            CreatedAt = DateTime.Today,
            ResolvedModelId = Model,
            Cost = Cost(2m, true)
        });
        Assert.True(ledger.Backfill("original-key", [session]));
        ledger.Read("original-key").BackfilledAt = DateTime.Today.AddTicks(1);
        Assert.True(ledger.Backfill(Key, [session]));
        ledger.Record(Key, Cost(5m, false), Model);

        Assert.Equal(1, ledger.RepairDuplicateBackfills([session]));

        var remaining = Assert.Single(ledger.Read(Key).Days);
        Assert.Equal(5m, remaining.Usd);
        Assert.True(remaining.Estimated);
        Assert.True(Assert.Single(remaining.Skus).Estimated);
        Assert.True(Assert.Single(ledger.Read("original-key").Days).Estimated);
    }

    [Fact]
    public void Approximate_formatting_preserves_the_existing_amount_format()
    {
        Assert.Equal("$2.00", SpendReport.FormatUsd(2m));
        Assert.Equal("≈$2.00", SpendReport.FormatUsd(2m, true));
        Assert.Equal("≈$0.1250", SpendReport.FormatUsd(0.125m, true));
        Assert.Equal("$0", SpendReport.FormatUsd(0m));
        Assert.Equal("≈$0", SpendReport.FormatUsd(0m, true));
    }
}
