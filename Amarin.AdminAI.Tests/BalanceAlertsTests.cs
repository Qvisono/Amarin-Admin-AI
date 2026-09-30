using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Пороги остатка (E4): уровни, переходы и чтение старых настроек.</summary>
public sealed class BalanceAlertsTests
{
    private static readonly BalanceThresholds Defaults = new();

    private static BalanceTotal Total(decimal usd, int unknown = 0) => new(usd, null, unknown);

    [Theory]
    [InlineData(5, "Ok")]
    [InlineData(0.99, "Low")]
    [InlineData(0.24, "Critical")]
    public void Levels_follow_the_thresholds(double usd, string expected)
    {
        Assert.Equal(Enum.Parse<BalanceLevel>(expected), BalanceWatch.Level((decimal)usd, 1.00m, 0.25m));
    }

    [Fact]
    public void Old_settings_keep_the_former_badge_thresholds()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}", AppJson.Options)!;

        Assert.Equal(1.00m, settings.BalanceThresholds.LowUsd);
        Assert.Equal(0.25m, settings.BalanceThresholds.CriticalUsd);
        Assert.Null(settings.SpendLimits.DayUsd);
    }

    [Fact]
    public void Only_a_drop_across_a_threshold_warns_and_the_first_reading_is_a_baseline()
    {
        var watch = new BalanceWatch();

        Assert.Empty(watch.Observe(Defaults, Total(0.5m), []));
        Assert.Empty(watch.Observe(Defaults, Total(0.4m), []));

        var critical = Assert.Single(watch.Observe(Defaults, Total(0.2m), []));
        Assert.Equal(BalanceLevel.Critical, critical.Level);
        Assert.Equal(0.25m, critical.Threshold);
        Assert.Null(critical.KeyLabel);

        // Пополнили и снова потратили — снова переход.
        Assert.Empty(watch.Observe(Defaults, Total(10m), []));
        Assert.Equal(BalanceLevel.Low, Assert.Single(watch.Observe(Defaults, Total(0.9m), [])).Level);
    }

    [Fact]
    public void A_total_with_silent_keys_does_not_warn()
    {
        var watch = new BalanceWatch();
        watch.Observe(Defaults, Total(5m), []);

        Assert.Empty(watch.Observe(Defaults, Total(0.1m, unknown: 1), []));
    }

    [Fact]
    public void A_key_warns_by_its_own_threshold_once_even_if_listed_twice()
    {
        var thresholds = new BalanceThresholds { LowUsd = null, CriticalUsd = null, Keys = { ["fp"] = 2m } };
        var watch = new BalanceWatch();
        BalanceRow Row(decimal usd) => new("Work", LlmProvider.Venice, usd, null, "fp");

        watch.Observe(thresholds, Total(3m), [Row(3m)]);
        var alert = Assert.Single(watch.Observe(thresholds, Total(1.5m), [Row(1.5m), Row(1.5m)]));

        Assert.Equal("Work", alert.KeyLabel);
        Assert.Equal(2m, alert.Threshold);
    }
}
