using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Amarin.Core;

/// <summary>
/// Запись в журнал «Приложение» Windows (1.37.0): копия журнала аудита для администратора.
/// </summary>
/// <remarks>
/// <para>
/// Через <c>ReportEventW</c>, а не <c>System.Diagnostics.EventLog.WriteEntry</c>: тот сперва
/// ищет источник во всех журналах, включая «Безопасность», и у обычного пользователя падает на
/// отказе в доступе, а регистрация источника требует администратора. Незарегистрированный
/// источник Windows принимает — «Просмотр событий» лишь добавляет приписку «описание не
/// найдено», которую снимает регистрация источника администратором (её строка есть в шаблоне
/// политик).
/// </para>
/// <para>
/// В событие передаётся SID пользователя: столбец «Пользователь» показывает, от чьего имени
/// работала программа, — администратору это и нужно.
/// </para>
/// </remarks>
internal static class WindowsEventLog
{
    /// <summary>Имя источника в журнале «Приложение».</summary>
    public const string Source = "Amarin Admin AI";

    private const ushort Information = 0x0004;
    private const ushort Warning = 0x0002;

    /// <summary>Пишет событие; false — Windows не приняла. Не бросает.</summary>
    public static bool Write(int eventId, bool warning, string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var handle = RegisterEventSourceW(null, Source);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            byte[]? sid = null;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (identity.User is { } user)
                {
                    sid = new byte[user.BinaryLength];
                    user.GetBinaryForm(sid, 0);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                // Без SID событие всё равно полезно: столбец «Пользователь» просто будет пуст.
            }

            return ReportEventW(
                handle,
                warning ? Warning : Information,
                0,
                (uint)eventId,
                sid,
                1,
                0,
                [message],
                IntPtr.Zero);
        }
        finally
        {
            _ = DeregisterEventSource(handle);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterEventSourceW(string? server, string source);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeregisterEventSource(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReportEventW(
        IntPtr handle,
        ushort type,
        ushort category,
        uint eventId,
        byte[]? userSid,
        ushort stringCount,
        uint dataSize,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] strings,
        IntPtr rawData);
}
