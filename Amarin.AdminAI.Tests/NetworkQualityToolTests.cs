using System.Net;
using System.Text.Json;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Качество связи: статистика проб, вердикт «Wi-Fi или провайдер», каналы Wi-Fi, пакет DNS и
/// замер целиком на подставных пробах — по настоящей сети тест не ходит.
/// </summary>
public sealed class NetworkQualityToolTests
{
    private static ProbeStats Stats(string label, params long?[] samples) => NetworkQuality.Summarize(label, samples);

    private static ProbeStats Good(string label) => Stats(label, 3, 4, 3, 5, 4, 3, 4, 3, 4, 3);

    private static QualityMeasurement Measurement(
        bool wireless = false,
        ProbeStats? gateway = null,
        ProbeStats? internet = null,
        ProbeStats? dns = null,
        bool tcp = true,
        WifiLink? wifi = null,
        IReadOnlyList<WifiNeighbor>? neighbors = null) =>
        new(wireless, gateway ?? Good("router"), [dns ?? Stats("dns", 20, 25)], [internet ?? Stats("internet", 20, 22, 21, 23)], tcp, wifi, neighbors);

    [Fact]
    public void Statistics_count_loss_average_p95_and_jitter()
    {
        var stats = Stats("x", 10, null, 20, 10, null);

        Assert.Equal(40, stats.LossPercent);
        Assert.Equal(13.33, stats.Average!.Value, precision: 2);
        Assert.Equal(20, stats.P95);
        Assert.Equal(10, stats.Jitter);
        Assert.Null(Stats("y", null, null).Average);
    }

    [Fact]
    public void A_clean_connection_has_no_cause()
    {
        var (cause, reasons) = NetworkQuality.Verdict(Measurement());

        Assert.StartsWith("none found", cause, StringComparison.Ordinal);
        Assert.Empty(reasons);
    }

    [Fact]
    public void Loss_to_the_router_over_wifi_blames_the_wifi()
    {
        var (cause, reasons) = NetworkQuality.Verdict(Measurement(wireless: true, gateway: Stats("router", 3, null, 4, 3, 5, 3, 4, 3, 4, 3)));

        Assert.StartsWith("Wi-Fi", cause, StringComparison.Ordinal);
        Assert.Contains(reasons, reason => reason.Contains("to the router are lost", StringComparison.Ordinal));
    }

    [Fact]
    public void A_weak_signal_or_a_crowded_channel_blames_the_wifi_even_with_a_quiet_router()
    {
        var weak = new WifiLink("home", 35, -78, 6, "2.4 GHz", "802.11n", 72, 72);
        Assert.StartsWith("Wi-Fi", NetworkQuality.Verdict(Measurement(wireless: true, wifi: weak)).Cause, StringComparison.Ordinal);

        var strong = weak with { SignalPercent = 90, Rssi = -45 };
        WifiNeighbor[] crowd = [.. Enumerable.Range(0, 5).Select(i => new WifiNeighbor($"n{i}", -60, 4 + (i % 4), "2.4 GHz"))];
        var (cause, reasons) = NetworkQuality.Verdict(Measurement(wireless: true, wifi: strong, neighbors: crowd));
        Assert.StartsWith("Wi-Fi", cause, StringComparison.Ordinal);
        Assert.Contains(reasons, reason => reason.Contains("channel 6", StringComparison.Ordinal));
    }

    [Fact]
    public void A_clean_router_and_a_lossy_internet_blame_the_provider()
    {
        var (cause, _) = NetworkQuality.Verdict(Measurement(internet: Stats("internet", 20, null, 22, null, 21, 23, 20, 22, 21, 20)));

        Assert.StartsWith("the provider", cause, StringComparison.Ordinal);
    }

    [Fact]
    public void Slow_dns_with_a_fine_connection_blames_dns()
    {
        var (cause, _) = NetworkQuality.Verdict(Measurement(dns: Stats("dns", 400, 350, 380)));

        Assert.StartsWith("DNS", cause, StringComparison.Ordinal);
    }

    [Fact]
    public void A_router_that_ignores_ping_is_not_a_fault()
    {
        var (cause, reasons) = NetworkQuality.Verdict(Measurement(gateway: Stats("router", null, null, null)));

        Assert.StartsWith("none found", cause, StringComparison.Ordinal);
        Assert.Contains(reasons, reason => reason.Contains("does not answer ping", StringComparison.Ordinal));
    }

    [Fact]
    public void Blocked_ping_with_working_connections_is_not_blamed_on_the_provider()
    {
        var (cause, reasons) = NetworkQuality.Verdict(Measurement(internet: Stats("internet", null, null), tcp: true));
        Assert.StartsWith("none found", cause, StringComparison.Ordinal);
        Assert.Contains(reasons, reason => reason.Contains("ping is blocked", StringComparison.Ordinal));

        Assert.StartsWith("the provider", NetworkQuality.Verdict(Measurement(internet: Stats("internet", null, null), tcp: false)).Cause,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2_412_000u, 1, "2.4 GHz")]
    [InlineData(2_437_000u, 6, "2.4 GHz")]
    [InlineData(2_484_000u, 14, "2.4 GHz")]
    [InlineData(5_180_000u, 36, "5 GHz")]
    [InlineData(5_955_000u, 1, "6 GHz")]
    public void Channels_come_from_the_center_frequency(uint khz, int channel, string band) =>
        Assert.Equal((channel, band), NetworkQuality.Channel(khz));

    [Fact]
    public void Channels_overlap_within_five_in_two_point_four_only()
    {
        Assert.True(NetworkQuality.Overlaps(1, "2.4 GHz", 4, "2.4 GHz"));
        Assert.False(NetworkQuality.Overlaps(1, "2.4 GHz", 6, "2.4 GHz"));
        Assert.False(NetworkQuality.Overlaps(36, "5 GHz", 40, "5 GHz"));
        Assert.True(NetworkQuality.Overlaps(36, "5 GHz", 36, "5 GHz"));
        Assert.False(NetworkQuality.Overlaps(1, "6 GHz", 1, "2.4 GHz"));
    }

    [Fact]
    public void The_dns_query_is_a_recursive_a_query_and_only_our_answer_counts()
    {
        var query = NetworkQuality.DnsQuery(0x1234, "a.example");

        Assert.Equal(new byte[] { 0x12, 0x34, 0x01, 0x00, 0x00, 0x01 }, query[..6]);
        Assert.Equal(new byte[] { 1, (byte)'a', 7 }, query[12..15]);
        Assert.Equal(new byte[] { 0, 0, 1, 0, 1 }, query[^5..]);

        var answer = (byte[])query.Clone();
        answer[2] |= 0x80;
        Assert.True(NetworkQuality.IsDnsAnswer(answer, 0x1234));
        Assert.False(NetworkQuality.IsDnsAnswer(answer, 0x4321));
        Assert.False(NetworkQuality.IsDnsAnswer(query, 0x1234));
    }

    [Fact]
    public async Task A_measurement_probes_every_second_and_dns_every_fifth()
    {
        var router = IPAddress.Parse("192.0.2.1");
        var dns = IPAddress.Parse("192.0.2.53");
        var pings = 0;
        var queries = 0;
        var tool = new NetworkQualityTool(
            () => new NetworkPath("Ethernet", Guid.Empty, false, IPAddress.Parse("192.0.2.10"), router, [dns]),
            (_, _) =>
            {
                Interlocked.Increment(ref pings);
                return Task.FromResult<long?>(2);
            },
            (_, _) =>
            {
                Interlocked.Increment(ref queries);
                return Task.FromResult<long?>(12);
            },
            (_, _) => Task.FromResult(true),
            _ => null,
            new InstantTime(new DateTime(2026, 10, 22, 12, 0, 0)));

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { seconds = 10 }));

        Assert.True(result.Success, result.Output);
        Assert.Equal(10 * 3, pings);
        Assert.Equal(2, queries);
        Assert.Contains("Router 192.0.2.1: 10 probes, lost 0%", result.Output, StringComparison.Ordinal);
        Assert.Contains("Likely cause: none found", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_route_to_the_internet_there_is_nothing_to_measure()
    {
        var tool = new NetworkQualityTool(() => null, (_, _) => Task.FromResult<long?>(1), (_, _) => Task.FromResult<long?>(1),
            (_, _) => Task.FromResult(true), _ => null, new InstantTime(DateTime.Now));

        Assert.False((await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { seconds = 10 }))).Success);
    }

    [Fact]
    public void Wifi_details_and_a_location_refusal_reach_the_report()
    {
        var path = new NetworkPath("Wi-Fi", Guid.Empty, true, null, IPAddress.Parse("192.0.2.1"), []);
        var link = new WifiLink("", 70, -55, 36, "5 GHz", "802.11ac (Wi-Fi 5)", 866, 866);
        var measurement = new QualityMeasurement(true, Good("192.0.2.1"), [], [Good("1.1.1.1")], true, link, null);

        var report = NetworkQualityTool.Report(10, path, measurement, new WifiState(link, null, "location"));

        Assert.Contains("network name hidden by Windows", report, StringComparison.Ordinal);
        Assert.Contains("channel 36 (5 GHz)", report, StringComparison.Ordinal);
        Assert.Contains("Расположение", report, StringComparison.Ordinal);
    }
}
