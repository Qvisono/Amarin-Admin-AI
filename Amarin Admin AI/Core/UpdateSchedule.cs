using System.Globalization;

namespace Amarin.Core;

/// <summary>
/// Расписание автообновления: когда идти в сеть, когда повторять после отказа и как назвать
/// человеку время последней проверки.
/// </summary>
/// <remarks>
/// Отдельным классом, а не полями окна: всё здесь — чистые функции от времени, и проверять их
/// надо тестами без WPF. Сама проверка живёт в <see cref="UpdateChecker"/>, загрузка и подмена —
/// в <see cref="UpdateInstaller"/>; этот класс только решает «пора или нет».
/// </remarks>
public static class UpdateSchedule
{
    /// <summary>Как часто автопроверка ходит в сеть при удачном ответе.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(5);

    /// <summary>
    /// Через сколько повторить, если проверка не удалась.
    /// </summary>
    /// <remarks>
    /// Без отдельного срока один обрыв связи откладывал бы следующую попытку на полный интервал:
    /// человек закрыл крышку ноутбука в метро — и программа молчит пять часов, хотя сеть вернулась
    /// через минуту.
    /// </remarks>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Такт таймера, который сверяет, не пора ли проверить.
    /// </summary>
    /// <remarks>
    /// Пятнадцать минут, а не один тик на пять часов: <c>DispatcherTimer</c> не досчитывает
    /// время сна и гибернации, и единственный длинный тик после пробуждения сдвинулся бы на
    /// столько, сколько машина спала. Короткий такт сверяет часы сам.
    /// </remarks>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Сколько процесс без окна доводит обновление после закрытия программы.
    /// </summary>
    /// <remarks>
    /// Загрузка семидесяти восьми мегабайт укладывается в минуты даже на медленной связи;
    /// потолок нужен на случай сети, которая повисла, не оборвавшись, — иначе невидимый процесс
    /// жил бы до перезагрузки и держал замок единственного экземпляра.
    /// </remarks>
    public static readonly TimeSpan ExitLimit = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Удачная проверка старше этого при закрытии программы повторяется — уже без окна.
    /// </summary>
    /// <remarks>
    /// Обновление ставится при закрытии, и ставить надо то, что есть на GitHub сейчас, а не
    /// то, что было там утром: программа бывает открыта сутками, а такт проверяет раз в пять
    /// часов. Без повтора проверка, сорвавшаяся при запуске (не было сети), означала бы, что при
    /// закрытии ставить нечего вовсе.
    /// </remarks>
    public static readonly TimeSpan RecheckOnExit = TimeSpan.FromMinutes(30);

    /// <summary>Проверить ли ещё раз при закрытии. <c>null</c> — удачной проверки в этом запуске не было.</summary>
    public static bool CheckDueOnExit(DateTime nowUtc, DateTime? lastSuccessUtc) =>
        lastSuccessUtc is not { } last || nowUtc - last >= RecheckOnExit || nowUtc < last;

    /// <summary>Пора ли идти в сеть. <c>null</c> — не проверяли ещё ни разу, значит пора.</summary>
    public static bool DueAt(DateTime nowUtc, DateTime? lastCheckUtc) =>
        lastCheckUtc is not { } last || nowUtc - last >= Interval;

    /// <summary>Когда проверять в следующий раз: удачную проверку ждём дольше, чем повтор.</summary>
    public static DateTime NextAfter(DateTime nowUtc, bool ok) =>
        nowUtc + (ok ? Interval : RetryAfterFailure);

    /// <summary>
    /// Подпись «Последняя проверка: …» для плашки обновлений.
    /// </summary>
    /// <remarks>
    /// Свежее суток — относительно («12 мин назад»): человеку важно, давно ли, а не в котором
    /// часу. Старше — абсолютной датой, потому что «31 ч назад» никто в уме не переводит.
    /// </remarks>
    public static string DescribeLastCheck(DateTime? lastCheckUtc, DateTime nowUtc)
    {
        if (lastCheckUtc is not { } last)
        {
            return Loc.Format("S.Updates.LastCheck", Loc.Get("S.Updates.LastCheck.Never"));
        }

        // Отрицательная разница — это переведённые назад часы, а не будущее: показывать
        // «-3 ч назад» нельзя, поэтому такой случай считается только что случившимся.
        var elapsed = nowUtc - last;
        var when = elapsed < TimeSpan.FromMinutes(1)
            ? Loc.Get("S.Updates.LastCheck.JustNow")
            : elapsed < TimeSpan.FromHours(1)
                ? Loc.Format("S.Updates.LastCheck.MinutesAgo", (int)elapsed.TotalMinutes)
                : elapsed < TimeSpan.FromDays(1)
                    ? Loc.Format("S.Updates.LastCheck.HoursAgo", (int)elapsed.TotalHours)
                    // Без своего ключа: подставлять дату в «{0}» — это и есть весь перевод,
                    // и отдельная строка на него была бы одинаковой во всех языках.
                    : last.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

        return Loc.Format("S.Updates.LastCheck", when);
    }

    /// <summary>
    /// Качать ли найденный релиз в фоне.
    /// </summary>
    /// <param name="staged">Версия, которая уже скачана и ждёт установки; <c>null</c> — нет такой.</param>
    /// <remarks>
    /// Релиз без готовой сборки для Windows пропускается: так выглядит запасной путь через
    /// редирект страницы релизов, у которого списка файлов нет вовсе, — качать оттуда нечего.
    /// </remarks>
    public static bool ShouldAutoDownload(bool autoUpdate, ReleaseInfo? latest, Version? staged) =>
        autoUpdate &&
        latest?.WindowsBuild is not null &&
        (staged is null || latest.Version > staged);
}
