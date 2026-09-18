using System.Runtime.InteropServices;
using System.Windows;

namespace Amarin.UI;

/// <summary>
/// Запись в буфер обмена с повтором.
/// </summary>
/// <remarks>
/// Буфер — общий ресурс системы, и соседнее приложение вправе подержать его открытым долю
/// секунды; <see cref="Clipboard"/> отвечает на это <see cref="ExternalException"/>. Трёх
/// попыток хватает всегда, а если не хватило — вызывающий говорит об этом человеку, а не молчит.
/// </remarks>
internal static class ClipboardWrite
{
    private const int Attempts = 3;

    /// <summary>Сколько ждать между попытками. Меньше — и повтор попадает в ту же занятость.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(60);

    /// <summary>Выполняет запись, повторяя её, пока буфер занят. <c>false</c> — не вышло.</summary>
    public static bool Try(Action write)
    {
        ArgumentNullException.ThrowIfNull(write);

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                write();
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(Pause);
            }
        }

        return false;
    }
}
