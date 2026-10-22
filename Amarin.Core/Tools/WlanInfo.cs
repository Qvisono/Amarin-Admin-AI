using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Amarin.Tools;

/// <summary>Состояние Wi-Fi интерфейса: подключение и соседние сети, либо почему их не видно.</summary>
internal sealed record WifiState(WifiLink? Link, IReadOnlyList<WifiNeighbor>? Neighbors, string? NeighborsUnavailable);

/// <summary>
/// Wi-Fi через Native Wi-Fi API (wlanapi), а не разбором <c>netsh wlan</c>: его текст на языке
/// системы, а нужны числа — сигнал, канал, соседи.
/// </summary>
/// <remarks>
/// <para>
/// Поля структур читаются по смещениям из заголовков Windows (wlanapi.h, windot11.h): там
/// массивы внутри структур и выравнивание по восьми, и смещение надёжнее описания маршалинга.
/// </para>
/// <para>
/// С Windows 11 24H2 список соседних сетей и имя своей отдаются программам только при доступе к
/// местоположению. Отказ здесь — не поломка, а ответ: человеку говорится, какую настройку
/// включить, а качество связи меряется и без этого.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WlanInfo
{
    private const int InterfaceInfoSize = 532;
    private const int BssEntrySize = 360;
    private const int OpcodeCurrentConnection = 7;
    private const int OpcodeChannel = 8;
    private const int OpcodeRssi = 0x10000102;
    private const int ErrorAccessDenied = 5;

    /// <summary>Просит Windows обновить список сетей: он понадобится в конце замера.</summary>
    public static void Scan(Guid iface)
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0)
        {
            return;
        }

        try
        {
            _ = WlanScan(handle, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            _ = WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    /// <summary>Состояние интерфейса с этим GUID; null — это не Wi-Fi или служба WLAN не работает.</summary>
    public static WifiState? Read(Guid iface)
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0)
        {
            return null;
        }

        try
        {
            if (!HasInterface(handle, iface))
            {
                return null;
            }

            var (link, bssid) = Connection(handle, iface);
            var code = WlanGetNetworkBssList(handle, ref iface, IntPtr.Zero, 1, false, IntPtr.Zero, out var list);
            if (code != 0)
            {
                return new WifiState(link, null, code == ErrorAccessDenied ? "location" : $"code {code}");
            }

            try
            {
                var neighbors = Neighbors(list, bssid, out var ownBand);

                // Номера каналов 5 и 6 ГГц пересекаются: диапазон своей точки — по её частоте.
                return new WifiState(link is not null && ownBand is not null ? link with { Band = ownBand } : link, neighbors, null);
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }
        finally
        {
            _ = WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    private static bool HasInterface(IntPtr handle, Guid iface)
    {
        if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0)
        {
            return false;
        }

        try
        {
            var count = Marshal.ReadInt32(list);
            for (var i = 0; i < count; i++)
            {
                if (Marshal.PtrToStructure<Guid>(list + 8 + (i * InterfaceInfoSize)) == iface)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    /// <returns>Подключение и BSSID точки; BSSID из нулей — Windows его скрыла.</returns>
    private static (WifiLink? Link, byte[] Bssid) Connection(IntPtr handle, Guid iface)
    {
        var bssid = new byte[6];
        if (WlanQueryInterface(handle, ref iface, OpcodeCurrentConnection, IntPtr.Zero, out _, out var data, out _) != 0)
        {
            return (null, bssid);
        }

        try
        {
            // WLAN_CONNECTION_ATTRIBUTES: состояние, режим, имя профиля (256 знаков), затем
            // атрибуты связи — SSID с 520, BSSID с 560, PHY с 568, качество с 576, скорости с 580.
            if (Marshal.ReadInt32(data) != 1)
            {
                return (null, bssid);
            }

            var ssid = Ssid(data + 520);
            Marshal.Copy(data + 560, bssid, 0, 6);
            var phy = Marshal.ReadInt32(data + 568);
            var quality = Marshal.ReadInt32(data + 576);
            var rx = Marshal.ReadInt32(data + 580) / 1000;
            var tx = Marshal.ReadInt32(data + 584) / 1000;

            int? rssi = null;
            if (WlanQueryInterface(handle, ref iface, OpcodeRssi, IntPtr.Zero, out _, out var rssiData, out _) == 0)
            {
                rssi = Marshal.ReadInt32(rssiData);
                WlanFreeMemory(rssiData);
            }

            var channel = 0;
            if (WlanQueryInterface(handle, ref iface, OpcodeChannel, IntPtr.Zero, out _, out var channelData, out _) == 0)
            {
                channel = Marshal.ReadInt32(channelData);
                WlanFreeMemory(channelData);
            }

            var band = channel switch { >= 1 and <= 14 => "2.4 GHz", > 14 => "5 GHz", _ => "unknown" };
            return (new WifiLink(ssid, quality, rssi, channel, band, NetworkQuality.Standard(phy), rx, tx), bssid);
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    /// <summary>Соседние сети из списка BSS без своей точки; <paramref name="ownBand"/> — диапазон своей по частоте.</summary>
    private static List<WifiNeighbor> Neighbors(IntPtr list, byte[] bssid, out string? ownBand)
    {
        ownBand = null;
        var hidden = bssid.All(value => value == 0);
        var result = new List<WifiNeighbor>();
        var count = Marshal.ReadInt32(list + 4);
        var entryBssid = new byte[6];
        for (var i = 0; i < count; i++)
        {
            var entry = list + 8 + (i * BssEntrySize);
            var (channel, band) = NetworkQuality.Channel((uint)Marshal.ReadInt32(entry + 92));
            Marshal.Copy(entry + 40, entryBssid, 0, 6);
            if (!hidden && entryBssid.AsSpan().SequenceEqual(bssid))
            {
                ownBand = band;
                continue;
            }

            result.Add(new WifiNeighbor(Ssid(entry), Marshal.ReadInt32(entry + 56), channel, band));
        }

        return result;
    }

    private static string Ssid(IntPtr dot11Ssid)
    {
        var length = Math.Clamp(Marshal.ReadInt32(dot11Ssid), 0, 32);
        var bytes = new byte[length];
        Marshal.Copy(dot11Ssid + 4, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(
        IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int dataSize, out IntPtr data, out int valueType);

    [DllImport("wlanapi.dll")]
    private static extern int WlanGetNetworkBssList(
        IntPtr handle, ref Guid iface, IntPtr ssid, int bssType, [MarshalAs(UnmanagedType.Bool)] bool securityEnabled,
        IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern int WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}
