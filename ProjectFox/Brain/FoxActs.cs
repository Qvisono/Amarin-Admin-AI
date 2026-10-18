namespace Amarin.Fox;

/// <summary>
/// Желания своей модели Розы — её инструмент <c>act</c> — и его мысли.
/// </summary>
/// <remarks>
/// <para>
/// Модель желает, мозг разрешает (<see cref="FoxBrain"/>), и правило у них одно — <see cref="Possible"/>:
/// модель получает ровно те желания, которые мозг сейчас исполнит. До 1.36.0 она видела весь список,
/// а мозг молча отказывал (в переписке — почти во всём), и Роза писал «ложусь», а сам сидел.
/// Набор настоящих шалостей от модели не растёт: она выбирает лишь, когда.
/// </para>
/// <para>
/// Мысль — свой запрос модели без текста переписки: настроение, нужды, час и события дня ключами.
/// Не чаще раза в <see cref="ThinkEvery"/>, не больше <see cref="MaxThoughts"/> в день и только при
/// галочке «Свои мысли»: каждая мысль — деньги.
/// </para>
/// </remarks>
internal static class FoxActs
{
    public const string Sulk = "sulk";
    public const string Mischief = "mischief";
    public const string Peek = "peek";
    public const string Play = "play";
    public const string Cuddle = "cuddle";

    // Эти желания — занятия воли под тем же именем: мозг исполняет их этими занятиями.
    public const string Lie = FoxWill.Lie;
    public const string Nap = FoxWill.Nap;
    public const string Strut = FoxWill.Strut;
    public const string Wander = FoxWill.Wander;

    /// <summary>Всё, что модель может пожелать; остальное отбрасывается при разборе ответа.</summary>
    public static readonly string[] All = [Sulk, Mischief, Peek, Play, Lie, Nap, Cuddle, Strut, Wander];

    /// <summary>Не чаще этого исполняется желание модели.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Что мозг исполнит прямо сейчас — и только это получает модель. Капризничать и ласкаться можно
    /// всегда; на месте (прилечь, вздремнуть) — не в руках, не в игре и не во сне, а в переписке можно;
    /// с места (спрятаться, позвать играть, пройтись) — ещё и не в переписке: панель ехала бы за ним;
    /// движение — только с анимациями; шалость — где её разрешил бы инстинкт; всё — не чаще
    /// раза в <see cref="Every"/>.
    /// </summary>
    public static IReadOnlyList<string> Possible(FoxState state, FoxSituation situation, DateTime now)
    {
        if (situation.Away || (state.LastActAt is { } last && now - last < Every))
        {
            return [];
        }

        List<string> acts = [Sulk, Cuddle];
        if (FoxMischief.Allowed(state, situation, now))
        {
            acts.Add(Mischief);
        }

        if (situation.Holding || situation.Asleep)
        {
            return acts;
        }

        acts.Add(Lie);
        acts.Add(Nap);
        if (situation.Chatting)
        {
            return acts;
        }

        if (situation.CanPeek)
        {
            acts.Add(Peek);
        }

        if (situation.Motion)
        {
            acts.AddRange([Play, Strut, Wander]);
        }

        return acts;
    }

    /// <summary>Не чаще этого своя мысль.</summary>
    public static readonly TimeSpan ThinkEvery = TimeSpan.FromMinutes(20);

    public const int MaxThoughts = 24;

    /// <summary>Сколько человек должен не печатать, чтобы Роза задумался о своём.</summary>
    public static readonly TimeSpan QuietForThought = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Пора ли подумать о своём: галочка, окно активно и ничем не закрыто, человек давно не
    /// печатал и ответа не ждёт, Роза здесь и не спит, и потолки не выбраны.
    /// </summary>
    public static bool MayThink(FoxState state, FoxSituation situation, DateTime now) =>
        situation.Thoughts && situation.WindowActive && !situation.Covered && !situation.Away && !situation.Asleep &&
        !situation.Holding && !situation.Chatting && !situation.TurnRunning && !situation.UiBusy && situation.SinceTyping >= QuietForThought &&
        state.Today.Thoughts < MaxThoughts &&
        (state.LastThoughtAt is not { } last || now - last >= ThinkEvery);
}
