using System.Globalization;

namespace Amarin.Fox;

/// <summary>Дружба с Розой: уровни, глаза и то, как растёт и тает привязанность.</summary>
internal static class FoxFriendship
{
    /// <summary>Глаза синеют, когда привязанность ниже этого.</summary>
    public const double ColdBelow = -30;

    /// <summary>И розовеют обратно только выше этого: запас, чтобы не мигали туда-обратно.</summary>
    public const double WarmAbove = -10;

    /// <summary>Уровни дружбы по верхней границе привязанности — ключи подписей <c>S.Fox.Level.*</c>.</summary>
    private static readonly (double UpTo, string Name)[] Levels =
    [
        (-60, "Hiss"),
        (-30, "Prickly"),
        (0, "Wary"),
        (30, "Acquaintance"),
        (60, "Pal"),
        (85, "Friend"),
        (FoxState.MaxAffection, "Best")
    ];

    public static IEnumerable<string> LevelNames => Levels.Select(level => level.Name);

    /// <summary>Ключ подписи уровня дружбы.</summary>
    public static string LevelKey(double affection)
    {
        foreach (var (upTo, name) in Levels)
        {
            if (affection <= upTo)
            {
                return "S.Fox.Level." + name;
            }
        }

        return "S.Fox.Level.Best";
    }

    /// <summary>
    /// Прибавка с убыванием за день: первое поглаживание греет сильнее сотого, и дружбу нельзя
    /// накрутить, водя мышью по Розе полчаса.
    /// </summary>
    public static double Gain(double amount, int timesToday) => amount * Math.Pow(0.85, Math.Max(0, timesToday));

    public static void Change(FoxState state, double delta) =>
        state.Affection = Math.Clamp(state.Affection + delta, FoxState.MinAffection, FoxState.MaxAffection);

    public static void Resent(FoxState state, double delta) =>
        state.Resentment = Math.Clamp(state.Resentment + delta, 0, 100);

    /// <summary>Переключает глаза с запасом. <c>true</c> — глаза сменились.</summary>
    public static bool UpdateEyes(FoxState state)
    {
        var cold = state.Cold ? state.Affection <= WarmAbove : state.Affection < ColdBelow;
        if (cold == state.Cold)
        {
            return false;
        }

        state.Cold = cold;
        return true;
    }

    /// <summary>Сколько дней вместе — для «Нашей дружбы».</summary>
    public static int DaysTogether(FoxState state, DateTime now) =>
        state.MetAt is { } met ? Math.Max(1, (int)(now.Date - met.Date).TotalDays + 1) : 0;
}

/// <summary>Смена дней: вчерашний день уходит в дневник, забвение копится.</summary>
internal static class FoxDays
{
    /// <summary>Дневник догоняет не больше трёх прошедших дней.</summary>
    public const int MaxUnwritten = 3;

    public static string Key(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Начинает новый день, если он наступил. <c>true</c> — день сменился.
    /// </summary>
    /// <remarks>
    /// Забвение считается только по дням, когда программа была открыта: день без открытого окна
    /// пустой, и за него Роза не обижается — он «скучал». Не гладили и не играли два таких дня
    /// подряд — привязанность начинает таять.
    /// </remarks>
    public static bool Roll(FoxState state, DateTime now)
    {
        var today = Key(now);
        if (state.Today.Date == today)
        {
            return false;
        }

        var finished = state.Today;
        if (finished.Date.Length > 0 && !finished.IsEmpty)
        {
            state.Unwritten.Add(finished);
            if (state.Unwritten.Count > MaxUnwritten)
            {
                state.Unwritten.RemoveRange(0, state.Unwritten.Count - MaxUnwritten);
            }

            var attention = finished.Pets + finished.Games + finished.Treats + finished.Talks > 0;
            state.NeglectDays = attention ? 0 : state.NeglectDays + 1;
            if (state.NeglectDays >= 2)
            {
                FoxFriendship.Change(state, -3);
                FoxFriendship.Resent(state, 15);
            }
        }

        state.Today = new FoxDay { Date = today, AffectionAtStart = state.Affection };
        return true;
    }
}

/// <summary>
/// Фразы Розы: ключи словаря строк с вариантами — <c>S.Fox.Say.&lt;повод&gt;.&lt;n&gt;</c>.
/// </summary>
/// <remarks>
/// Ключами, а не текстом: фразы переводит перевод интерфейса, а число вариантов здесь сверяет тест
/// со словарями — повод без своих строк не дожил бы до показа молча.
/// </remarks>
internal static class FoxPhrases
{
    public static readonly IReadOnlyDictionary<string, int> Variants = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["FirstMeet"] = 2,
        ["Greet.Warm"] = 4,
        ["Greet.Cold"] = 3,
        ["Greet.Sulk"] = 3,
        ["Greet.Missed"] = 3,
        ["Morning"] = 2,
        ["Night"] = 2,
        ["Back.Warm"] = 2,
        ["Back.Cold"] = 2,
        ["Boop"] = 3,
        ["BoopAngry"] = 2,
        ["Bite"] = 2,
        ["Growl"] = 2,
        ["Pet"] = 3,
        ["PetSulk"] = 2,
        ["MakeUp"] = 2,
        ["Tail"] = 2,
        ["Ear"] = 2,
        ["HoldWarm"] = 2,
        ["HoldCold"] = 2,
        ["ThrowWarm"] = 2,
        ["ThrowCold"] = 2,
        ["Dizzy"] = 2,
        ["Treat"] = 3,
        ["TreatFull"] = 2,
        ["Play"] = 2,
        ["Science"] = 3,
        ["Code"] = 2,
        ["Virus"] = 3,
        ["VirusClear"] = 2,
        ["Scam"] = 3,
        ["Sad"] = 3,
        ["Happy"] = 2,
        ["Thanks"] = 2,
        ["Rude"] = 2,
        ["RudeToMe"] = 2,
        ["Praise"] = 2,
        ["Nonsense"] = 3,
        ["NonsenseMany"] = 2,
        ["WrongLayout"] = 2,
        ["Long"] = 2,
        ["Reading"] = 2,
        ["Error"] = 2,
        ["LongWait"] = 2,
        ["Agent"] = 2,
        ["Spend"] = 2,
        ["Danger"] = 2,
        ["NewChat"] = 2,
        ["DeleteOne"] = 2,
        ["DeleteMany"] = 2,
        ["Theme"] = 2,
        ["Settings"] = 2,
        ["Viewer"] = 2,
        ["LongChat"] = 2,
        ["Listen"] = 2,
        ["Attachment"] = 2,
        ["Keys"] = 2,
        ["Bored"] = 3,
        ["Attention"] = 3,
        ["Gift"] = 2,
        ["Anniversary"] = 2,
        ["Pets100"] = 1,
        ["Escape"] = 2,
        ["Return"] = 2,
        ["Hide"] = 2,
        ["Mischief"] = 3,
        ["Mute"] = 2,
        ["CallRefuse"] = 3,
        ["Found"] = 2,
        ["Caught"] = 2,
        ["FetchAsk"] = 2,
        ["FetchIgnored"] = 2,
        ["Strut"] = 2,
        ["Chase"] = 2,
        ["Tease"] = 2,
        ["Pester"] = 2,
        ["HideSeek"] = 2,
        ["Catch"] = 2,
        ["Fight"] = 2,
        ["FightWin"] = 2
    };

    /// <summary>Ключ случайного варианта фразы.</summary>
    public static string Pick(string reason, Random random) =>
        "S.Fox.Say." + reason + "." + random.Next(1, Variants[reason] + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Все ключи фраз — для сверки со словарями.</summary>
    public static IEnumerable<string> AllKeys() =>
        Variants.SelectMany(pair => Enumerable.Range(1, pair.Value)
            .Select(n => "S.Fox.Say." + pair.Key + "." + n.ToString(CultureInfo.InvariantCulture)));
}
