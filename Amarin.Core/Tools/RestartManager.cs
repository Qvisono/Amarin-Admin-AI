using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ComFileTime = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace Amarin.Tools;

/// <summary>Кем Restart Manager считает держателя файла.</summary>
internal enum RmAppType
{
    Unknown = 0,
    MainWindow = 1,
    OtherWindow = 2,
    Service = 3,
    Explorer = 4,
    Console = 5,

    /// <summary>Процесс, без которого Windows не работает: Restart Manager его не закрывает.</summary>
    Critical = 1000
}

/// <summary>Процесс, который держит файл открытым.</summary>
/// <param name="StartTime">Время запуска — вместе с PID отличает процесс от нового с тем же номером.</param>
internal sealed record LockHolder(int Pid, long StartTime, string App, string Service, RmAppType Type, bool Restartable);

/// <summary>
/// Restart Manager: кто держит файлы и как попросить его закрыться — тем же механизмом, которым
/// пользуются установщики и Центр обновления Windows.
/// </summary>
/// <remarks>
/// Закрытие идёт просьбой, а не завершением процесса: программа с окном получает тот же запрос,
/// что при выходе из Windows, и может сохранить работу или отказаться. Принудительно — только
/// когда так прямо попросили (<c>force</c>): иначе несохранённое пропало бы молча.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class RestartManager
{
    private const int ErrorMoreData = 234;
    private const int ErrorFailShutdown = 351;
    private const int ErrorAccessDenied = 5;
    private const int ErrorCancelled = 1223;
    private const int RmForceShutdown = 0x1;

    /// <summary>Держатели файлов. Пусто — никто; null — Restart Manager отказал (код в <paramref name="error"/>).</summary>
    public static List<LockHolder>? Holders(IReadOnlyList<string> files, out int error)
    {
        error = 0;
        if (files.Count == 0)
        {
            return [];
        }

        var key = new char[33];
        error = RmStartSession(out var session, 0, key);
        if (error != 0)
        {
            return null;
        }

        try
        {
            error = RmRegisterResources(session, (uint)files.Count, [.. files], 0, null, 0, null);
            if (error != 0)
            {
                return null;
            }

            uint needed = 0;
            uint count = 0;
            uint reasons = 0;
            RmProcessInfo[]? list = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                error = RmGetList(session, out needed, ref count, list, ref reasons);
                if (error == 0)
                {
                    break;
                }

                if (error != ErrorMoreData)
                {
                    return null;
                }

                // Между двумя вызовами держателей могло стать больше — запас на них.
                count = needed + 4;
                list = new RmProcessInfo[count];
            }

            if (error != 0)
            {
                return null;
            }

            var result = new List<LockHolder>();
            for (var i = 0; i < count && list is not null && i < list.Length; i++)
            {
                var info = list[i];
                result.Add(new LockHolder(
                    info.Process.ProcessId,
                    ((long)info.Process.StartTime.dwHighDateTime << 32) | (uint)info.Process.StartTime.dwLowDateTime,
                    info.AppName ?? "",
                    info.ServiceShortName ?? "",
                    Enum.IsDefined((RmAppType)info.ApplicationType) ? (RmAppType)info.ApplicationType : RmAppType.Unknown,
                    info.Restartable));
            }

            return result;
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    /// <summary>Итог просьбы закрыться.</summary>
    public enum ShutdownResult
    {
        Closed,

        /// <summary>Программа не закрылась: не ответила или отказалась (ждёт «сохранить?»).</summary>
        Refused,
        AccessDenied,
        Failed
    }

    /// <summary>
    /// Просит одного держателя закрыться: процесс — по PID и времени запуска, службу — по имени.
    /// Проводник после закрытия поднимается снова: без него у человека пропали бы рабочий стол и
    /// панель задач.
    /// </summary>
    public static ShutdownResult Shutdown(LockHolder holder, bool force, CancellationToken cancellationToken, out int code)
    {
        var key = new char[33];
        code = RmStartSession(out var session, 0, key);
        if (code != 0)
        {
            return ShutdownResult.Failed;
        }

        try
        {
            code = holder.Type == RmAppType.Service && holder.Service.Length > 0
                ? RmRegisterResources(session, 0, null, 0, null, 1, [holder.Service])
                : RmRegisterResources(session, 0, null, 1, [Unique(holder)], 0, null);
            if (code != 0)
            {
                return ShutdownResult.Failed;
            }

            using (cancellationToken.Register(() => _ = RmCancelCurrentTask(session)))
            {
                code = RmShutdown(session, force ? RmForceShutdown : 0, IntPtr.Zero);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (code == 0 && holder.Type == RmAppType.Explorer)
            {
                _ = RmRestart(session, 0, IntPtr.Zero);
            }

            return code switch
            {
                0 => ShutdownResult.Closed,
                ErrorFailShutdown or ErrorCancelled => ShutdownResult.Refused,
                ErrorAccessDenied => ShutdownResult.AccessDenied,
                _ => ShutdownResult.Failed
            };
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    private static RmUniqueProcess Unique(LockHolder holder) => new()
    {
        ProcessId = holder.Pid,
        StartTime = new ComFileTime { dwLowDateTime = (int)(holder.StartTime & 0xFFFFFFFF), dwHighDateTime = (int)(holder.StartTime >> 32) }
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct RmUniqueProcess
    {
        public int ProcessId;
        public ComFileTime StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, char[] sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint session, uint fileCount, string[]? files, uint processCount, [In] RmUniqueProcess[]? processes,
        uint serviceCount, string[]? services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? list, ref uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmShutdown(uint session, int flags, IntPtr status);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmRestart(uint session, int flags, IntPtr status);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmCancelCurrentTask(uint session);
}
