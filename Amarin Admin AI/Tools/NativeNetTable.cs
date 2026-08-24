using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace Amarin.Tools;

internal static class NativeNetTable
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const uint ErrorInsufficientBuffer = 122;

    internal readonly record struct Row(
        string Protocol,
        string LocalAddress,
        int LocalPort,
        string RemoteAddress,
        int RemotePort,
        string State,
        int Pid);

    public static List<Row> GetTcpRows()
    {
        var rows = new List<Row>(256);
        CollectTcp(AfInet, rows);
        CollectTcp(AfInet6, rows);
        return rows;
    }

    public static List<Row> GetUdpRows()
    {
        var rows = new List<Row>(128);
        CollectUdp(AfInet, rows);
        CollectUdp(AfInet6, rows);
        return rows;
    }

    public static Dictionary<int, string> ProcessNames()
    {
        var map = new Dictionary<int, string>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                map.TryAdd(process.Id, process.ProcessName);
            }
            catch
            {
                // ignore
            }
            finally
            {
                process.Dispose();
            }
        }

        return map;
    }

    public static string LookupProcess(IReadOnlyDictionary<int, string> names, int pid) =>
        pid <= 0 ? "-" : names.TryGetValue(pid, out var name) ? name : "?";

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tableClass, uint reserved);

    private static void CollectTcp(int family, List<Row> rows)
    {
        if (!ReadTable((IntPtr buffer, ref int size) => GetExtendedTcpTable(buffer, ref size, true, family, TcpTableOwnerPidAll, 0), out var buffer, out var count))
        {
            return;
        }

        try
        {
            var offset = 4;
            if (family == AfInet)
            {
                for (var i = 0; i < count; i++)
                {
                    var state = (int)ReadUInt32(buffer, offset);
                    var localAddr = ReadUInt32(buffer, offset + 4);
                    var localPort = PortFromDword(ReadUInt32(buffer, offset + 8));
                    var remoteAddr = ReadUInt32(buffer, offset + 12);
                    var remotePort = PortFromDword(ReadUInt32(buffer, offset + 16));
                    var pid = (int)ReadUInt32(buffer, offset + 20);
                    offset += 24;
                    rows.Add(new Row(
                        "TCP",
                        FormatIpv4(localAddr),
                        localPort,
                        FormatIpv4(remoteAddr),
                        remotePort,
                        TcpStateName(state),
                        pid));
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    var localBytes = new byte[16];
                    Marshal.Copy(buffer + offset, localBytes, 0, 16);
                    var localPort = PortFromDword(ReadUInt32(buffer, offset + 20));
                    var remoteBytes = new byte[16];
                    Marshal.Copy(buffer + offset + 24, remoteBytes, 0, 16);
                    var remotePort = PortFromDword(ReadUInt32(buffer, offset + 44));
                    var state = (int)ReadUInt32(buffer, offset + 48);
                    var pid = (int)ReadUInt32(buffer, offset + 52);
                    offset += 56;
                    rows.Add(new Row(
                        "TCP",
                        FormatIpv6(localBytes),
                        localPort,
                        FormatIpv6(remoteBytes),
                        remotePort,
                        TcpStateName(state),
                        pid));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void CollectUdp(int family, List<Row> rows)
    {
        if (!ReadTable((IntPtr buffer, ref int size) => GetExtendedUdpTable(buffer, ref size, true, family, UdpTableOwnerPid, 0), out var buffer, out var count))
        {
            return;
        }

        try
        {
            var offset = 4;
            if (family == AfInet)
            {
                for (var i = 0; i < count; i++)
                {
                    var localAddr = ReadUInt32(buffer, offset);
                    var localPort = PortFromDword(ReadUInt32(buffer, offset + 4));
                    var pid = (int)ReadUInt32(buffer, offset + 8);
                    offset += 12;
                    rows.Add(new Row("UDP", FormatIpv4(localAddr), localPort, "*", 0, "Listen", pid));
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    var localBytes = new byte[16];
                    Marshal.Copy(buffer + offset, localBytes, 0, 16);
                    var localPort = PortFromDword(ReadUInt32(buffer, offset + 20));
                    var pid = (int)ReadUInt32(buffer, offset + 24);
                    offset += 28;
                    rows.Add(new Row("UDP", FormatIpv6(localBytes), localPort, "*", 0, "Listen", pid));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private delegate uint TableQuery(IntPtr buffer, ref int size);

    private static bool ReadTable(TableQuery query, out IntPtr buffer, out int count)
    {
        buffer = IntPtr.Zero;
        count = 0;
        var size = 0;
        var error = query(IntPtr.Zero, ref size);
        if (error != ErrorInsufficientBuffer && error != 0)
        {
            return false;
        }

        buffer = Marshal.AllocHGlobal(size);
        error = query(buffer, ref size);
        if (error != 0)
        {
            Marshal.FreeHGlobal(buffer);
            buffer = IntPtr.Zero;
            return false;
        }

        count = Marshal.ReadInt32(buffer);
        return true;
    }

    private static uint ReadUInt32(IntPtr buffer, int offset) =>
        unchecked((uint)Marshal.ReadInt32(buffer, offset));

    private static int PortFromDword(uint value) =>
        (int)unchecked((ushort)IPAddress.NetworkToHostOrder(unchecked((short)(value >> 16))));

    private static string FormatIpv4(uint addr) =>
        new IPAddress(BitConverter.GetBytes(addr)).ToString();

    private static string FormatIpv6(byte[] bytes)
    {
        try
        {
            return new IPAddress(bytes).ToString();
        }
        catch
        {
            return "::";
        }
    }

    private static string TcpStateName(int state) => state switch
    {
        1 => "Closed",
        2 => "Listen",
        3 => "SynSent",
        4 => "SynReceived",
        5 => "Established",
        6 => "FinWait1",
        7 => "FinWait2",
        8 => "CloseWait",
        9 => "Closing",
        10 => "LastAck",
        11 => "TimeWait",
        12 => "DeleteTcb",
        _ => state.ToString()
    };
}
