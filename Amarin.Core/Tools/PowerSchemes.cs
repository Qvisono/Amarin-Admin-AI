using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Amarin.Tools;

/// <summary>Схема электропитания: идентификатор, название на языке системы и активна ли.</summary>
internal sealed record PowerScheme(Guid Id, string Name, bool Active);

/// <summary>Сейчас: от сети ли, заряд и есть ли батарея вообще.</summary>
internal sealed record PowerStatus(bool HasBattery, bool? OnAc, int? Percent);

/// <summary>
/// Схемы электропитания через powrprof, а не разбором <c>powercfg /list</c>: его текст на языке
/// системы, а GUID и признак активной схемы нужны точно.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PowerSchemes
{
    private const uint AccessScheme = 16;

    public static List<PowerScheme> List()
    {
        var active = Active();
        var result = new List<PowerScheme>();
        for (uint index = 0; index < 64; index++)
        {
            var id = Guid.Empty;
            var size = (uint)Marshal.SizeOf<Guid>();
            if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, ref id, ref size) != 0)
            {
                break;
            }

            result.Add(new PowerScheme(id, FriendlyName(id), id == active));
        }

        return result;
    }

    public static Guid? Active()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            _ = LocalFree(pointer);
        }
    }

    /// <returns>Код Windows: 0 — схема включена.</returns>
    public static uint SetActive(Guid id) => PowerSetActiveScheme(IntPtr.Zero, ref id);

    public static PowerStatus Status()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            return new PowerStatus(false, null, null);
        }

        // BatteryFlag 128 — «системной батареи нет». 255 («неизвестно») батарею не исключает:
        // отчёт всё равно спросится и скажет сам.
        var hasBattery = status.BatteryFlag != 128;
        return new PowerStatus(
            hasBattery,
            status.AcLineStatus switch { 0 => false, 1 => true, _ => null },
            hasBattery && status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null);
    }

    private static string FriendlyName(Guid id)
    {
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) != 0 || size == 0)
        {
            return "";
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
                ? Marshal.PtrToStringUni(buffer) ?? ""
                : "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(
        IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroup, uint accessFlags, uint index, ref Guid buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(
        IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroup, IntPtr powerSetting, IntPtr buffer, ref uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
