using System.Runtime.InteropServices;

namespace Amarin.Core;

/// <summary>
/// Можно ли сейчас звучать: Windows сама говорит, когда человек показывает презентацию, играет в
/// полноэкранную игру или включил «тихие часы».
/// </summary>
/// <remarks>Карточка в такие минуты всё равно приходит — молчит только мелодия.</remarks>
internal static class QuietState
{
    public static bool IsBusy()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 &&
                   state is Busy or Direct3DFullScreen or Presentation or QuietTime;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private const int Busy = 2;
    private const int Direct3DFullScreen = 3;
    private const int Presentation = 4;
    private const int QuietTime = 6;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
