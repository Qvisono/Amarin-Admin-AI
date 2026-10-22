using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Amarin.Tools;

/// <summary>Итог серии проб до одной цели: потери, задержка, разброс.</summary>
internal sealed record ProbeStats(string Label, int Sent, int Received, double? Average, long? Max, long? P95, double? Jitter)
{
    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
}

/// <summary>Подключение к Wi-Fi так, как его видит wlanapi.</summary>
/// <param name="Ssid">Пусто — Windows скрыла имя сети (нет доступа к местоположению).</param>
internal sealed record WifiLink(string Ssid, int SignalPercent, int? Rssi, int Channel, string Band, string Standard, int RxMbps, int TxMbps);

/// <summary>Соседняя сеть из списка сканирования.</summary>
internal sealed record WifiNeighbor(string Ssid, int Rssi, int Channel, string Band);

/// <summary>Всё, что намерено, — вход вердикта.</summary>
internal sealed record QualityMeasurement(
    bool Wireless,
    ProbeStats? Gateway,
    IReadOnlyList<ProbeStats> Dns,
    IReadOnlyList<ProbeStats> Internet,
    bool InternetTcp,
    WifiLink? Wifi,
    IReadOnlyList<WifiNeighbor>? Neighbors);

/// <summary>
/// Пороги и вердикт «виноват Wi-Fi или провайдер», статистика проб, каналы Wi-Fi и пакет DNS.
/// Без сети — проверяется тестами.
/// </summary>
/// <remarks>
/// <para>
/// Роутер — граница: всё, что плохо уже до него, — домашняя сеть (у беспроводной — сам Wi-Fi), а
/// если до роутера чисто, а дальше теряется или задерживается, — провайдер или то, что за ним.
/// DNS судится отдельно: имена могут не находиться при здоровой связи по адресам.
/// </para>
/// <para>
/// Пороги — заметное человеку: потеря двух пакетов из ста уже рвёт звонки, а задержка до
/// роутера по воздуху больше двадцати миллисекунд — признак плохого эфира, по кабелю — больше
/// пяти. Некоторые роутеры ping не отвечают вовсе; это не поломка, и тогда локальная сеть по
/// ping не судится.
/// </para>
/// </remarks>
internal static class NetworkQuality
{
    internal const double LossBad = 2;
    internal const double GatewayWirelessRttBad = 20;
    internal const double GatewayWiredRttBad = 5;
    internal const double GatewayJitterBad = 15;
    internal const double InternetRttBad = 100;
    internal const double InternetJitterBad = 30;
    internal const double DnsRttBad = 150;
    internal const double DnsFailuresBad = 10;
    internal const int WeakSignalPercent = 50;
    internal const int WeakRssi = -70;

    /// <summary>Соседняя сеть мешает, если слышна сильнее этого.</summary>
    internal const int NeighborRssi = -70;

    /// <summary>Столько сильных соседей на том же или перекрытом канале — эфир занят.</summary>
    internal const int CrowdedNeighbors = 4;

    public static ProbeStats Summarize(string label, IReadOnlyList<long?> samples)
    {
        var received = samples.Where(sample => sample is not null).Select(sample => sample!.Value).ToList();
        if (received.Count == 0)
        {
            return new ProbeStats(label, samples.Count, 0, null, null, null, null);
        }

        var sorted = received.Order().ToList();
        var p95 = sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * 0.95) - 1, 0, sorted.Count - 1)];
        double? jitter = received.Count < 2
            ? null
            : received.Zip(received.Skip(1), (left, right) => (double)Math.Abs(right - left)).Average();
        return new ProbeStats(label, samples.Count, received.Count, received.Average(), sorted[^1], p95, jitter);
    }

    /// <summary>Вероятная причина и доводы к ней.</summary>
    public static (string Cause, List<string> Reasons) Verdict(QualityMeasurement measurement)
    {
        var reasons = new List<string>();
        var local = false;

        var gateway = measurement.Gateway;
        var gatewayDark = gateway is not null && gateway.Received == 0;
        if (gateway is not null && !gatewayDark)
        {
            var rttLimit = measurement.Wireless ? GatewayWirelessRttBad : GatewayWiredRttBad;
            if (gateway.LossPercent >= LossBad)
            {
                local = true;
                reasons.Add(F($"{gateway.LossPercent:0.#}% of packets to the router are lost"));
            }

            if (gateway.Average >= rttLimit)
            {
                local = true;
                reasons.Add(F($"the router answers in {gateway.Average:0} ms on average, a healthy {(measurement.Wireless ? "Wi-Fi" : "wired")} link answers within {rttLimit:0} ms"));
            }

            if (gateway.Jitter >= GatewayJitterBad)
            {
                local = true;
                reasons.Add(F($"delay to the router jumps by {gateway.Jitter:0} ms between packets"));
            }
        }

        if (measurement.Wifi is { } wifi)
        {
            if (wifi.SignalPercent < WeakSignalPercent || wifi.Rssi < WeakRssi)
            {
                local = true;
                reasons.Add(F($"weak Wi-Fi signal: {wifi.SignalPercent}%{(wifi.Rssi is { } rssi ? $" ({rssi} dBm)" : "")}"));
            }

            if (measurement.Neighbors is { } neighbors && Crowding(wifi, neighbors) is var crowding && crowding >= CrowdedNeighbors)
            {
                local = true;
                reasons.Add($"{crowding} strong networks share or overlap channel {wifi.Channel}");
            }
        }

        // Лучшая из публичных целей: одна из них может резать ping, а провайдер тут ни при чём.
        var internet = measurement.Internet.Where(stats => stats.Received > 0).OrderBy(stats => stats.LossPercent).ThenBy(stats => stats.Average).FirstOrDefault();
        var internetBad = false;
        if (internet is null)
        {
            if (measurement.Internet.Count > 0)
            {
                internetBad = !measurement.InternetTcp;
                reasons.Add(measurement.InternetTcp
                    ? "the internet does not answer ping, but connections work: ping is blocked on the way, so its delay cannot be measured"
                    : "the internet is unreachable: no answer to ping and no connection");
            }
        }
        else
        {
            if (internet.LossPercent >= LossBad)
            {
                internetBad = true;
                reasons.Add(F($"{internet.LossPercent:0.#}% of packets to the internet are lost"));
            }

            if (internet.Average >= InternetRttBad)
            {
                internetBad = true;
                reasons.Add(F($"the internet answers in {internet.Average:0} ms on average"));
            }

            if (internet.Jitter >= InternetJitterBad)
            {
                internetBad = true;
                reasons.Add(F($"delay to the internet jumps by {internet.Jitter:0} ms between packets"));
            }
        }

        var dnsBad = false;
        foreach (var dns in measurement.Dns)
        {
            if (dns.LossPercent >= DnsFailuresBad)
            {
                dnsBad = true;
                reasons.Add(F($"DNS server {dns.Label} did not answer {dns.LossPercent:0}% of queries"));
            }
            else if (dns.Average >= DnsRttBad)
            {
                dnsBad = true;
                reasons.Add(F($"DNS server {dns.Label} answers in {dns.Average:0} ms on average"));
            }
        }

        if (gatewayDark)
        {
            reasons.Add("the router does not answer ping at all; many routers are set up so, and the local link is judged by the internet results");
        }

        var cause = local
            ? measurement.Wireless ? "Wi-Fi (the link between this PC and the router)" : "the local network (cable, switch or router)"
            : internetBad
                ? "the provider or the network beyond the router"
                : dnsBad
                    ? "DNS (names resolve slowly or fail while the connection itself is fine)"
                    : "none found: the connection was stable during the measurement";
        return (cause, reasons);
    }

    /// <summary>Сильные соседи на том же канале или перекрывающем его (2,4 ГГц: ближе пяти каналов).</summary>
    public static int Crowding(WifiLink link, IReadOnlyList<WifiNeighbor> neighbors) =>
        neighbors.Count(neighbor => neighbor.Rssi >= NeighborRssi && Overlaps(link.Channel, link.Band, neighbor.Channel, neighbor.Band));

    public static bool Overlaps(int channel, string band, int other, string otherBand) =>
        band == otherBand && (band == "2.4 GHz" ? Math.Abs(channel - other) < 5 : channel == other);

    /// <summary>Канал и диапазон по центральной частоте в килогерцах (так её отдаёт wlanapi).</summary>
    public static (int Channel, string Band) Channel(uint centerKhz)
    {
        var mhz = (int)(centerKhz / 1000);
        return mhz switch
        {
            2484 => (14, "2.4 GHz"),
            >= 2412 and <= 2472 => ((mhz - 2407) / 5, "2.4 GHz"),
            >= 5150 and <= 5895 => ((mhz - 5000) / 5, "5 GHz"),
            >= 5955 and <= 7115 => ((mhz - 5950) / 5, "6 GHz"),
            _ => (0, "unknown")
        };
    }

    /// <summary>Сколько сильных сетей слышно на каждом из непересекающихся каналов 2,4 ГГц и на каналах 5 ГГц.</summary>
    public static string ChannelLoad(IReadOnlyList<WifiNeighbor> neighbors)
    {
        var strong = neighbors.Where(neighbor => neighbor.Rssi >= NeighborRssi).ToList();
        var parts = new List<string>();
        var low = new[] { 1, 6, 11 }.Select(channel => $"{channel}: {strong.Count(n => Overlaps(channel, "2.4 GHz", n.Channel, n.Band))}");
        parts.Add("2.4 GHz (strong networks overlapping channels 1, 6, 11) " + string.Join(", ", low));
        var high = strong.Where(neighbor => neighbor.Band != "2.4 GHz")
            .GroupBy(neighbor => (neighbor.Band, neighbor.Channel))
            .OrderBy(group => group.Key.Band).ThenBy(group => group.Key.Channel)
            .Select(group => $"{group.Key.Channel}: {group.Count()}")
            .ToList();
        if (high.Count > 0)
        {
            parts.Add("5/6 GHz (strong networks per channel) " + string.Join(", ", high));
        }

        return string.Join("; ", parts);
    }

    public static string Standard(int phyType) => phyType switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "802.11n (Wi-Fi 4)",
        8 => "802.11ac (Wi-Fi 5)",
        10 => "802.11ax (Wi-Fi 6)",
        11 => "802.11be (Wi-Fi 7)",
        _ => "802.11"
    };

    /// <summary>Запрос A с рекурсией: заголовок, одно имя, тип A, класс IN.</summary>
    public static byte[] DnsQuery(ushort id, string name)
    {
        var bytes = new List<byte>(32);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
        bytes.AddRange(header.ToArray());
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var ascii = Encoding.ASCII.GetBytes(label);
            bytes.Add((byte)ascii.Length);
            bytes.AddRange(ascii);
        }

        bytes.AddRange([0, 0, 1, 0, 1]);
        return [.. bytes];
    }

    /// <summary>Ответ на наш запрос: тот же номер и признак ответа. Код ошибки имени — тоже ответ сервера.</summary>
    public static bool IsDnsAnswer(ReadOnlySpan<byte> response, ushort id) =>
        response.Length >= 12 && BinaryPrimitives.ReadUInt16BigEndian(response) == id && (response[2] & 0x80) != 0;

    public static string Describe(ProbeStats stats, string kind) =>
        stats.Received == 0
            ? $"{kind} {stats.Label}: no answer to {stats.Sent} probes"
            : F($"{kind} {stats.Label}: {stats.Sent} probes, lost {stats.LossPercent:0.#}%, average {stats.Average:0} ms, 95% within {stats.P95} ms, max {stats.Max} ms, jitter {stats.Jitter ?? 0:0} ms");

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
