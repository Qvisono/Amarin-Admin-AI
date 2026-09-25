using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Скругление углов главного окна средствами Windows 11.
/// </summary>
/// <remarks>
/// Углы рисует DWM, а не разметка: окно с собственным заголовком (<c>WindowChrome</c>) всё
/// равно остаётся обычным окном системы, и его форму задаёт она. До 1.27.0 окно числилось
/// «инструментом» (<c>WindowStyle="ToolWindow"</c>), а таким Windows 11 даёт маленькое
/// скругление; став обычным окном, оно получило крупное — поэтому вид теперь выбирается явно.
/// <para>
/// Своего радиуса система не принимает: только «маленькое», «обычное» и «без скругления».
/// Произвольный радиус потребовал бы рисовать окно целиком самим — ценой системной тени,
/// прилипания к краям экрана и заметной нагрузки на отрисовку.
/// </para>
/// <para>
/// На Windows 10 атрибута нет, вызов возвращает ошибку, и углы остаются прямыми, как у всех
/// окон там. Это не авария — ответ просто игнорируется.
/// </para>
/// </remarks>
internal static class WindowCornerStyle
{
    private const int DwmwaWindowCornerPreference = 33;

    private const int DwmwcpDoNotRound = 1;
    private const int DwmwcpRound = 2;
    private const int DwmwcpRoundSmall = 3;

    /// <summary>Значение атрибута DWM для выбора в настройках.</summary>
    internal static int PreferenceFor(WindowCorners corners) => corners switch
    {
        WindowCorners.Round => DwmwcpRound,
        WindowCorners.Square => DwmwcpDoNotRound,
        _ => DwmwcpRoundSmall
    };

    /// <returns>Приняла ли система выбор. На Windows 10 — нет.</returns>
    public static bool Apply(Window window, WindowCorners corners)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var preference = PreferenceFor(corners);
        try
        {
            return DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int)) == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
