using System.Runtime.InteropServices;

namespace Amarin.Core;

/// <summary>Программа с окном на экране: где лежит её exe и, у приложений из Магазина, как её запустить.</summary>
/// <param name="AppUserModelId">Идентификатор приложения из Магазина; его запускают через оболочку, а не exe.</param>
internal sealed record OpenProgram(string Path, string Name, string? AppUserModelId = null);

/// <summary>
/// Программы, у которых сейчас есть окно на рабочем столе, — то, что человек называет «открытыми
/// программами».
/// </summary>
/// <remarks>
/// Окна, а не процессы: фоновых процессов сотни, и вернуть «всё, что работало», значило бы запустить
/// службы, обновлятели и помощники. Берутся видимые окна верхнего уровня без владельца, не
/// инструментальные и не скрытые оболочкой (у приложений из Магазина спрятанное окно остаётся
/// «видимым»). Перечисление — доли секунды, поэтому годится и в последние мгновения перед выключением.
/// </remarks>
internal static class OpenWindows
{
    /// <summary>Оболочка Windows и её помощники: их «открыла» система, а не человек.</summary>
    private static readonly HashSet<string> Shell = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "applicationframehost.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe",
        "searchhost.exe", "searchapp.exe", "textinputhost.exe", "lockapp.exe", "systemsettings.exe",
        "widgets.exe", "gamebar.exe", "taskmgr.exe", "dwm.exe", "sihost.exe", "ctfmon.exe"
    };

    public static List<OpenProgram> Capture()
    {
        var processes = new HashSet<int>();
        try
        {
            EnumWindows((window, _) =>
            {
                if (IsCandidate(window) && GetWindowThreadProcessId(window, out var pid) != 0)
                {
                    processes.Add((int)pid);
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }

        var own = Environment.ProcessId;
        var programs = new List<OpenProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in processes)
        {
            if (pid == own || Describe(pid) is not { } program || Shell.Contains(program.Name) || !seen.Add(program.AppUserModelId ?? program.Path))
            {
                continue;
            }

            programs.Add(program);
        }

        return programs;
    }

    private static bool IsCandidate(IntPtr window)
    {
        if (!IsWindowVisible(window) || GetWindow(window, GwOwner) != IntPtr.Zero || GetWindowTextLength(window) == 0)
        {
            return false;
        }

        if ((GetWindowLongPtr(window, GwlExStyle).ToInt64() & WsExToolWindow) != 0)
        {
            return false;
        }

        return DwmGetWindowAttribute(window, DwmwaCloaked, out var cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    private static OpenProgram? Describe(int pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            var length = buffer.Length;
            if (!QueryFullProcessImageNameW(process, 0, buffer, ref length))
            {
                return null;
            }

            // Запущенное от администратора при возврате подняло бы окно UAC прямо при входе в Windows.
            if (IsElevated(process))
            {
                return null;
            }

            var path = new string(buffer, 0, length);
            return new OpenProgram(path, System.IO.Path.GetFileName(path), AppUserModelId(process));
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    /// <summary>Работает ли процесс от администратора; не узнать — считаем, что да, и не трогаем.</summary>
    private static bool IsElevated(IntPtr process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            return true;
        }

        try
        {
            return !GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) || elevated != 0;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    private const int TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    private static string? AppUserModelId(IntPtr process)
    {
        try
        {
            var length = 512u;
            var buffer = new char[length];
            return GetApplicationUserModelId(process, ref length, buffer) == 0 && length > 1 ? new string(buffer, 0, (int)length - 1) : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private const int GwOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const int DwmwaCloaked = 14;
    private const int ProcessQueryLimitedInformation = 0x1000;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, [Out] char[] name, ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, [Out] char[] id);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
