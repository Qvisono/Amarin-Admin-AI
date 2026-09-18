using System.Globalization;

namespace Amarin.Core;

internal static class ChatFormat
{
    public static string Duration(TimeSpan elapsed)
    {
        var totalSeconds = Math.Max(0, (int)Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero));
        if (totalSeconds < 60)
        {
            return $"{totalSeconds}s";
        }

        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return $"{minutes}m{seconds}s";
    }

    /// <summary>Счётчик над ответом, пока модель думает.</summary>
    /// <remarks>
    /// Секунды здесь отсекаются, а не округляются: счётчик тикает каждую секунду, и округление
    /// заставило бы его показать «1» в тот же миг, когда ход только начался.
    /// <para>
    /// «3s» подставляется целиком одной подстановкой, а не собирается из числа и буквы в самой
    /// строке: иначе перевод на новый язык неминуемо тронул бы «s» — модель переводит значения
    /// как текст — и оторвал бы её от числа пробелом. Так же устроено «думал {0}».
    /// </para>
    /// </remarks>
    public static string Working(TimeSpan elapsed) =>
        Loc.Format("S.Message.Working", $"{Math.Max(0, (int)elapsed.TotalSeconds)}s");

    public static string Clock(DateTime timestamp) => timestamp.ToString("HH:mm");

    /// <summary>Шаблон даты под выбранный человеком порядок.</summary>
    /// <remarks>
    /// Точка здесь — часть выбора, а не разделитель текущей культуры: человек выбирает из
    /// четырёх подписей вида <c>DD.MM.YY</c>, и на английской или немецкой культуре
    /// <see cref="CultureInfo.CurrentCulture"/> подменил бы её слэшем или дефисом.
    /// Поэтому всюду ниже стоит <see cref="CultureInfo.InvariantCulture"/>.
    /// </remarks>
    public static string Pattern(DateFormat format) => format switch
    {
        DateFormat.MonthDayShort => "MM.dd.yy",
        DateFormat.DayMonthFull => "dd.MM.yyyy",
        DateFormat.MonthDayFull => "MM.dd.yyyy",
        _ => "dd.MM.yy"
    };

    public static string Date(DateTime timestamp, DateFormat format) =>
        timestamp.ToString(Pattern(format), CultureInfo.InvariantCulture);

    /// <summary>Дата и время до секунд — подсказка над часами ответа.</summary>
    /// <remarks>
    /// Секунды здесь есть, хотя в самих часах их нет: подсказку открывают как раз затем, чтобы
    /// узнать точнее, чем показано рядом.
    /// </remarks>
    public static string Stamp(DateTime timestamp, DateFormat format) =>
        timestamp.ToString(Pattern(format) + ", HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Дата и время до минут — строки журнала и отчёты.</summary>
    public static string DateTimeShort(DateTime timestamp, DateFormat format) =>
        timestamp.ToString(Pattern(format) + ", HH:mm", CultureInfo.InvariantCulture);

    public static string Cost(VeniceCost? cost)
    {
        if (cost is null || !cost.HasData)
        {
            return "";
        }

        var usd = cost.Usd < 0.0001m && cost.Usd > 0
            ? "<0,0001"
            : cost.Usd.ToString("0.####", CultureInfo.InvariantCulture).Replace('.', ',');
        return "$" + usd;
    }
}
