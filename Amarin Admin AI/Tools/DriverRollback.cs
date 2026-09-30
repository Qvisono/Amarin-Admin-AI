using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// «Откатить драйвер» — тот же вызов, что за кнопкой в диспетчере устройств
/// (<c>DiRollbackDriver</c> из newdev.dll).
/// </summary>
/// <remarks>
/// Команды для этого у Windows нет: <c>pnputil</c> умеет ставить и удалять пакеты, но выбирает
/// драйвер по рангу, и «поставить прежний» вернул бы тот же новый. Откат берёт резервную копию
/// прежнего драйвера, которую Windows хранит при обновлении; если её нет — откатывать нечего,
/// и это честный ответ модели, а не повод искать обходной путь.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class DriverRollback
{
    private const uint RollbackFlagNoUi = 0x00000001;
    private const int ErrorAccessDenied = 5;

    public static ToolResult Run(string instanceId)
    {
        var set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == InvalidHandle)
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Devices.RollbackFailed", Marshal.GetLastWin32Error()));
        }

        try
        {
            var data = new SpDevinfoData { cbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
            if (!SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref data))
            {
                return ToolResult.Fail(Loc.Format("S.Tool.Devices.NotFound", instanceId));
            }

            if (!DiRollbackDriver(set, ref data, IntPtr.Zero, RollbackFlagNoUi, out var needReboot))
            {
                var error = Marshal.GetLastWin32Error();
                return ToolResult.Fail(error == ErrorAccessDenied
                    ? Loc.Get("S.Tool.NeedsAdmin")
                    : Loc.Format("S.Tool.Devices.RollbackFailed", $"0x{error:X8}"));
            }

            return ToolResult.Ok(needReboot
                ? Loc.Format("S.Tool.Devices.RolledBackReboot", instanceId)
                : Loc.Format("S.Tool.Devices.RolledBack", instanceId));
        }
        catch (EntryPointNotFoundException)
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Devices.RollbackFailed", "newdev.dll"));
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInfo(
        IntPtr deviceInfoSet,
        string deviceInstanceId,
        IntPtr hwndParent,
        uint openFlags,
        ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DiRollbackDriver(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        IntPtr hwndParent,
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] out bool needReboot);
}
