using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>Путь в интернет: адаптер, его адрес, роутер и DNS-серверы.</summary>
internal sealed record NetworkPath(string Adapter, Guid Id, bool Wireless, IPAddress? Address, IPAddress? Gateway, IReadOnlyList<IPAddress> Dns);

/// <summary>
/// Качество связи за минуту: задержки и потери до роутера, DNS и интернета, сигнал и
/// занятость каналов Wi-Fi — чтобы ответить, виноват Wi-Fi или провайдер.
/// </summary>
/// <remarks>
/// <para>
/// Цели выбирает инструмент, а не модель: роутер и DNS-серверы самого подключения и два
/// публичных anycast-адреса, которые отвечают почти отовсюду. Хост от модели превратил бы замер
/// в способ стучаться куда угодно.
/// </para>
/// <para>
/// DNS меряется прямым запросом к каждому серверу, а не через кэш Windows: из кэша ответ
/// приходит мгновенно и о сервере ничего не говорит.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class NetworkQualityTool : ITool
{
    public const string ToolName = "network_quality";
    public const int DefaultSeconds = 60;
    public const int MinSeconds = 10;
    public const int MaxSeconds = 120;

    /// <summary>Имя для пробных запросов DNS — то же, что проверяет сама Windows, когда ищет интернет.</summary>
    internal const string DnsProbeName = "www.msftconnecttest.com";

    /// <summary>Публичные адреса Cloudflare и Google: anycast, ближайший узел почти у любого провайдера.</summary>
    private static readonly IPAddress[] InternetTargets = [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")];

    private readonly Func<NetworkPath?> _path;
    private readonly Func<IPAddress, CancellationToken, Task<long?>> _ping;
    private readonly Func<IPAddress, CancellationToken, Task<long?>> _dns;
    private readonly Func<IPAddress, CancellationToken, Task<bool>> _tcp;
    private readonly Func<Guid, WifiState?> _wifi;
    private readonly TimeProvider _time;

    public NetworkQualityTool()
        : this(CurrentPath, PingAsync, DnsAsync, TcpAsync, Wifi, TimeProvider.System)
    {
    }

    /// <summary>Пробы и часы подставляются в тестах: настоящий замер шёл бы минуту по настоящей сети.</summary>
    internal NetworkQualityTool(
        Func<NetworkPath?> path,
        Func<IPAddress, CancellationToken, Task<long?>> ping,
        Func<IPAddress, CancellationToken, Task<long?>> dns,
        Func<IPAddress, CancellationToken, Task<bool>> tcp,
        Func<Guid, WifiState?> wifi,
        TimeProvider time)
    {
        _path = path;
        _ping = ping;
        _dns = dns;
        _tcp = tcp;
        _wifi = wifi;
        _time = time;
    }

    public string Name => ToolName;

    public string Description =>
        "Measures the connection for a while: loss, delay and jitter to the router, to each DNS server and to the " +
        "internet, the Wi-Fi signal, channel and neighbouring networks, and names the likely cause: Wi-Fi or the " +
        "local network, the provider, DNS, or none. The targets are chosen by the tool. The call waits for the " +
        "whole duration.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "seconds": {
              "type": "integer",
              "description": "How long to measure: 10-120 (default 60). The call waits this long"
            }
          }
        }
        """);

    /// <summary>Длительность из аргументов — её же показывает строка ожидания агента.</summary>
    public static int Seconds(JsonElement arguments) =>
        Math.Clamp(FileToolPaths.Int(arguments, "seconds") ?? DefaultSeconds, MinSeconds, MaxSeconds);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        Task.Run(() => MeasureAsync(Seconds(arguments), cancellationToken), cancellationToken);

    private async Task<ToolResult> MeasureAsync(int seconds, CancellationToken cancellationToken)
    {
        if (_path() is not { } path)
        {
            return ToolResult.Fail(Loc.Get("S.Tool.NetQuality.NoNetwork"));
        }

        if (path.Wireless && path.Id != Guid.Empty)
        {
            WlanInfo.Scan(path.Id);
        }

        var tcp = (await Task.WhenAll(InternetTargets.Select(target => _tcp(target, cancellationToken))).ConfigureAwait(false)).Any(ok => ok);

        var gateway = new List<long?>();
        var internet = InternetTargets.ToDictionary(target => target, _ => new List<long?>());
        var dnsTasks = path.Dns.ToDictionary(server => server, _ => new List<Task<long?>>());
        var start = _time.GetUtcNow();
        for (var second = 0; second < seconds; second++)
        {
            if (second % 5 == 0)
            {
                // Ответа DNS не ждём здесь: таймаут в две секунды растягивал бы каждую пятую секунду.
                foreach (var (server, tasks) in dnsTasks)
                {
                    tasks.Add(_dns(server, cancellationToken));
                }
            }

            var round = new List<Task>();
            Task<long?>? gatewayPing = path.Gateway is { } router ? _ping(router, cancellationToken) : null;
            if (gatewayPing is not null)
            {
                round.Add(gatewayPing);
            }

            var internetPings = InternetTargets.ToDictionary(target => target, target => _ping(target, cancellationToken));
            round.AddRange(internetPings.Values);
            await Task.WhenAll(round).ConfigureAwait(false);

            if (gatewayPing is not null)
            {
                gateway.Add(gatewayPing.Result);
            }

            foreach (var (target, ping) in internetPings)
            {
                internet[target].Add(ping.Result);
            }

            var wait = start + TimeSpan.FromSeconds(second + 1) - _time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            }
        }

        var dns = new List<ProbeStats>();
        foreach (var (server, tasks) in dnsTasks)
        {
            dns.Add(NetworkQuality.Summarize(server.ToString(), await Task.WhenAll(tasks).ConfigureAwait(false)));
        }

        var wifi = path.Wireless ? _wifi(path.Id) : null;
        var measurement = new QualityMeasurement(
            path.Wireless,
            path.Gateway is { } address ? NetworkQuality.Summarize(address.ToString(), gateway) : null,
            dns,
            [.. internet.Select(pair => NetworkQuality.Summarize(pair.Key.ToString(), pair.Value))],
            tcp,
            wifi?.Link,
            wifi?.Neighbors);
        return ToolResult.Ok(Report(seconds, path, measurement, wifi));
    }

    internal static string Report(int seconds, NetworkPath path, QualityMeasurement measurement, WifiState? wifi)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Measured {seconds} s through \"{path.Adapter}\" ({(path.Wireless ? "Wi-Fi" : "wired")}{(path.Address is { } address ? ", " + address : "")}).");
        sb.AppendLine(measurement.Gateway is { } gateway ? NetworkQuality.Describe(gateway, "Router") : "Router: this connection has no IPv4 gateway.");
        foreach (var dns in measurement.Dns)
        {
            sb.AppendLine(NetworkQuality.Describe(dns, "DNS"));
        }

        foreach (var internet in measurement.Internet)
        {
            sb.AppendLine(NetworkQuality.Describe(internet, "Internet"));
        }

        sb.AppendLine($"TCP connection to the internet: {(measurement.InternetTcp ? "works" : "fails")}");
        if (path.Wireless)
        {
            if (wifi?.Link is { } link)
            {
                sb.AppendLine($"Wi-Fi: {(link.Ssid.Length > 0 ? $"\"{link.Ssid}\"" : "network name hidden by Windows")}, signal {link.SignalPercent}%" +
                              $"{(link.Rssi is { } rssi ? $" ({rssi} dBm)" : "")}, channel {link.Channel} ({link.Band}), {link.Standard}, link {link.RxMbps}/{link.TxMbps} Mbps");
                if (wifi.Neighbors is { } neighbors)
                {
                    sb.AppendLine($"Strong networks on the same or an overlapping channel: {NetworkQuality.Crowding(link, neighbors)} of {neighbors.Count} heard");
                    sb.AppendLine("Channel load: " + NetworkQuality.ChannelLoad(neighbors));
                }
            }
            else
            {
                sb.AppendLine("Wi-Fi: the adapter reports no connection details.");
            }

            if (wifi?.NeighborsUnavailable == "location")
            {
                sb.AppendLine(Loc.Get("S.Tool.NetQuality.Location"));
            }
            else if (wifi?.NeighborsUnavailable is { } reason)
            {
                sb.AppendLine($"Neighbouring networks: not available ({reason}).");
            }
        }

        var (cause, reasons) = NetworkQuality.Verdict(measurement);
        sb.AppendLine($"Likely cause: {cause}");
        foreach (var reason in reasons)
        {
            sb.AppendLine("  - " + reason);
        }

        return sb.ToString().TrimEnd();
    }

    private static NetworkPath? CurrentPath()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                          nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .ToList();

        // Тот адаптер, через который Windows сама пошла бы в интернет.
        NetworkInterface? chosen = null;
        if (GetBestInterface(BitConverter.ToUInt32(InternetTargets[0].GetAddressBytes(), 0), out var index) == 0)
        {
            chosen = interfaces.FirstOrDefault(nic => Ipv4Index(nic) == index);
        }

        chosen ??= interfaces.FirstOrDefault(nic => Gateway(nic) is not null);
        if (chosen is null)
        {
            return null;
        }

        var properties = chosen.GetIPProperties();
        return new NetworkPath(
            chosen.Name,
            Guid.TryParse(chosen.Id, out var id) ? id : Guid.Empty,
            chosen.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
            properties.UnicastAddresses.Select(item => item.Address).FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork),
            Gateway(chosen),
            [.. properties.DnsAddresses.Where(item => item.AddressFamily == AddressFamily.InterNetwork).Distinct()]);
    }

    private static IPAddress? Gateway(NetworkInterface nic) =>
        nic.GetIPProperties().GatewayAddresses
            .Select(item => item.Address)
            .FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork && !item.Equals(IPAddress.Any));

    private static int? Ipv4Index(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GetIPv4Properties()?.Index;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static async Task<long?> PingAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(1), null, null, cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException)
        {
            return null;
        }
    }

    private static async Task<long?> DnsAsync(IPAddress server, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            udp.Connect(server, 53);
            var id = (ushort)Random.Shared.Next(ushort.MaxValue);
            var watch = Stopwatch.StartNew();
            _ = await udp.SendAsync(NetworkQuality.DnsQuery(id, DnsProbeName), timeout.Token).ConfigureAwait(false);
            while (true)
            {
                var answer = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (NetworkQuality.IsDnsAnswer(answer.Buffer, id))
                {
                    return watch.ElapsedMilliseconds;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static async Task<bool> TcpAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, 443, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static WifiState? Wifi(Guid id) => id == Guid.Empty ? null : WlanInfo.Read(id);

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destination, out uint index);
}
