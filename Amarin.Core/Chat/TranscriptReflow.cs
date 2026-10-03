namespace Amarin.Core;

/// <summary>
/// Что перекладывать в ленте, пока окно меняет размер, и в каком порядке отпускать остальное.
/// </summary>
/// <remarks>
/// <para>
/// Каждое построенное сообщение ленты — свой документ, и смена ширины окна перекладывала их все
/// разом: на чате в 1200 сообщений шаг перетаскивания края стоил 1,7 секунды, разворот — полторы.
/// Пока окно тянут или разворачивают, лента перекладывает только полосу вокруг видимого, а
/// остальное держит прежнюю ширину и отпускается порциями, когда размер устоялся.
/// </para>
/// <para>
/// Полоса не симметрична. Если человек читает середину чата, всё выше видимого стоит на месте:
/// сообщения над глазом не меняют высоты, и текст под ним никуда не уезжает. Если лента прижата
/// к низу, растущее окно открывает текст сверху — туда полоса и тянется.
/// </para>
/// </remarks>
public static class TranscriptReflow
{
    /// <summary>Полоса ленты, которая перекладывается сразу, в координатах ленты.</summary>
    /// <param name="viewportTop">Верх видимой области.</param>
    /// <param name="viewportHeight">Высота видимой области.</param>
    /// <param name="reach">
    /// Насколько видимая область может вырасти, пока размер меняется. Больше экрана она не станет.
    /// </param>
    /// <param name="stuckToBottom">Лента у низа и останется у низа.</param>
    public static (double From, double To) LiveBand(double viewportTop, double viewportHeight, double reach, bool stuckToBottom)
    {
        var bottom = viewportTop + Math.Max(0, viewportHeight);
        var extra = Math.Max(0, reach);
        return stuckToBottom ? (viewportTop - extra, bottom) : (viewportTop, bottom + extra);
    }

    /// <summary>
    /// Первое и последнее сообщение, которые задевают полосу <c>[from, to)</c>. Сообщения стоят
    /// стопкой: каждое тянется от своего верха до верха следующего.
    /// </summary>
    /// <param name="count">Сколько сообщений в ленте.</param>
    /// <param name="top">Верх сообщения по номеру; не убывает.</param>
    /// <returns>Номера крайних задетых; <c>(0, -1)</c> — не задето ничего.</returns>
    /// <remarks>Двоичным поиском: на перетаскивании края это зовётся на каждый шаг.</remarks>
    public static (int First, int Last) Overlapping(int count, Func<int, double> top, double from, double to)
    {
        ArgumentNullException.ThrowIfNull(top);
        if (count <= 0 || to <= from)
        {
            return (0, -1);
        }

        var last = LastBefore(count, top, to, inclusive: false);
        if (last < 0)
        {
            return (0, -1);
        }

        var first = Math.Max(0, LastBefore(count, top, from, inclusive: true));
        return (first, last);
    }

    /// <summary>
    /// В каком порядке отпускать сообщения вне полосы: от неё наружу, ближние первыми, ниже и
    /// выше по очереди. Ближнее к глазу раньше всего может понадобиться, если сразу листнуть.
    /// </summary>
    public static IEnumerable<int> Outward(int count, int first, int last)
    {
        var below = Math.Max(0, last + 1);
        var above = Math.Min(count - 1, first - 1);
        while (below < count || above >= 0)
        {
            if (below < count)
            {
                yield return below++;
            }

            if (above >= 0)
            {
                yield return above--;
            }
        }
    }

    /// <summary>Последний номер, чей верх выше <paramref name="y"/> (или на нём); -1 — таких нет.</summary>
    private static int LastBefore(int count, Func<int, double> top, double y, bool inclusive)
    {
        int low = 0, high = count - 1, found = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var value = top(middle);
            if (inclusive ? value <= y : value < y)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }
}
