namespace Amarin.Fox;

/// <summary>
/// Желания своей модели Розы — её инструмент <c>act</c> — и его мысли.
/// </summary>
/// <remarks>
/// <para>
/// Модель желает, мозг разрешает (<see cref="FoxBrain"/>): шалость — только там, где её разрешил
/// бы и инстинкт (<see cref="FoxMischief.Allowed"/>), иначе каприз; спрятаться и вздремнуть — не в
/// руках, не в игре и не в разговоре; движение — только с анимациями; и всё это не чаще раза в
/// <see cref="Every"/>. Набор настоящих шалостей от модели не растёт: она выбирает лишь, когда.
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
    public const string Nap = "nap";
    public const string Cuddle = "cuddle";
    public const string Strut = "strut";
    public const string Wander = "wander";

    /// <summary>Всё, что модель может пожелать; остальное отбрасывается при разборе ответа.</summary>
    public static readonly string[] All = [Sulk, Mischief, Peek, Play, Nap, Cuddle, Strut, Wander];

    /// <summary>Не чаще этого исполняется желание модели.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

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
        !situation.Holding && !situation.TurnRunning && !situation.UiBusy && situation.SinceTyping >= QuietForThought &&
        state.Today.Thoughts < MaxThoughts &&
        (state.LastThoughtAt is not { } last || now - last >= ThinkEvery);
}
