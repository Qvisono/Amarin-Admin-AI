using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Amarin.Core;

/// <summary>
/// Когда загрузилась Windows, когда начался нынешний вход в неё и когда запущена программа.
/// </summary>
/// <remarks>
/// «При следующем включении компьютера» нельзя узнать по времени загрузки: при быстром запуске
/// (он включён по умолчанию) «Завершение работы» — это сон ядра, и время работы в Диспетчере задач
/// идёт дальше, через выключение. А вход в Windows после такого выключения — новый, и его время
/// (<c>WTSQuerySessionInformation</c>) честно отвечает на вопрос «включали ли компьютер».
/// </remarks>
internal static class SystemSession
{
    private static readonly Lazy<DateTime> AppStarted = new(() =>
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return DateTime.UtcNow;
        }
    });

    /// <summary>Время работы Windows — то же, что в Диспетчере задач.</summary>
    public static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static DateTime BootUtc => DateTime.UtcNow - Uptime;

    public static DateTime AppStartedUtc => AppStarted.Value;

    /// <summary>Начало нынешнего входа в Windows; не узнать — время загрузки.</summary>
    public static DateTime SessionStartedUtc
    {
        get
        {
            var boot = BootUtc;
            return LogonUtc() is { } logon && logon > boot ? logon : boot;
        }
    }

    private static DateTime? LogonUtc()
    {
        try
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, CurrentSession, WtsSessionInfo, out var buffer, out _) || buffer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var info = Marshal.PtrToStructure<WtsInfo>(buffer);
                return info.LogonTime > 0 ? DateTime.FromFileTimeUtc(info.LogonTime) : null;
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private const int CurrentSession = -1;
    private const int WtsSessionInfo = 24;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsInfo
    {
        public int State;
        public int SessionId;
        public int IncomingBytes;
        public int OutgoingBytes;
        public int IncomingFrames;
        public int OutgoingFrames;
        public int IncomingCompressedBytes;
        public int OutgoingCompressedBytes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string WinStationName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)]
        public string Domain;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)]
        public string UserName;

        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long LogonTime;
        public long CurrentTime;
    }
}
