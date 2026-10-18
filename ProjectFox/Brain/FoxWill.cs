namespace Amarin.Fox;

/// <summary>
/// Воля Розы: чем заняться, когда его никто не трогает. Чистая — время, случай и обстановка
/// приходят параметрами, поэтому каждое правило проверяется тестом.
/// </summary>
/// <remarks>
/// <para>
/// Занятие выбирается взвешенным случаем по нуждам: скука, привязанность, обида, час. Большие
/// занятия — не чаще раза в 45–120 секунд (<see cref="FoxState.NextActivityAt"/>), и никогда, пока
/// человек печатает, читает вопрос поверх окна или работает в колонке: Роза — не помеха работе.
/// </para>
/// <para>
/// Что можно «бесить» и дразнить, решает галочка «Шалости»: дразнилки и бесячество без неё не
/// выбираются. Без анимаций остаётся только смена позы — всё прочее и есть движение.
/// </para>
/// </remarks>
internal static class FoxWill
{
    public const string Lie = "lie";
    public const string Wander = "wander";
    public const string Strut = "strut";
    public const string Chase = "chase";
    public const string Tease = "tease";
    public const string Pester = "pester";
    public const string Fetch = "fetch";
    public const string HideSeek = "hideseek";
    public const string Catch = "catch";

    /// <summary>Вздремнуть: уставший — сам, бодрый — только по желанию своей модели (<see cref="FoxActs.Nap"/>).</summary>
    public const string Nap = "nap";

    /// <summary>Драка с курсором: её затевает не воля, а курсор, дразнящий под носом (<see cref="FoxSignalKind.Teased"/>).</summary>
    public const string Fight = "fight";

    /// <summary>Сколько человек должен не печатать, чтобы Роза занялся своим.</summary>
    public static readonly TimeSpan QuietTyping = TimeSpan.FromSeconds(10);

    /// <summary>Бесит, только если внимания не было дольше этого.</summary>
    public static readonly TimeSpan PesterAfter = TimeSpan.FromMinutes(15);

    /// <summary>Занятие на этот тик или null — заняться нечем или нельзя.</summary>
    public static string? Choose(FoxState state, FoxSituation situation, DateTime now, Random random)
    {
        // В переписке он слушает: своё занятие увело бы его, а с ним и панель, из-под руки человека.
        if (!situation.WindowActive || situation.Covered || situation.Away || situation.UiBusy || situation.Chatting ||
            situation.SinceTyping < QuietTyping || (state.NextActivityAt is { } next && now < next))
        {
            return null;
        }

        state.NextActivityAt = now.AddSeconds(45 + random.Next(76));
        var options = Weights(state, situation, now).Where(option => option.Weight > 0).ToList();
        var total = options.Sum(option => option.Weight);
        if (total <= 0)
        {
            return null;
        }

        var roll = random.NextDouble() * total;
        foreach (var (activity, weight) in options)
        {
            roll -= weight;
            if (roll < 0)
            {
                return activity;
            }
        }

        return options[^1].Activity;
    }

    /// <summary>Ниже этих сил Роза устал: лежит и засыпает охотнее, чем бегает.</summary>
    public const double Tired = 30;

    /// <summary>Выше этих сил Роза бодр: игры весят больше.</summary>
    public const double Lively = 70;

    /// <summary>Сколько сил забирает занятие; отрицательное — отдых.</summary>
    public static double Cost(string activity) => activity switch
    {
        Chase or Catch or Fetch => 12,
        Strut or Wander or HideSeek or Tease or Pester => 6,
        Lie => -4,
        _ => 0
    };

    /// <summary>Веса занятий. Без анимаций — только прилечь и, уставшим, вздремнуть.</summary>
    internal static IEnumerable<(string Activity, double Weight)> Weights(FoxState state, FoxSituation situation, DateTime now)
    {
        var night = now.Hour is >= 22 or < 7;
        var tired = state.Energy < Tired;
        yield return (Lie, 2 + (night ? 2 : 0) + (state.Boredom < 30 ? 1 : 0) + (tired ? 4 : 0));
        if (tired)
        {
            yield return (Nap, 3 + (night ? 2 : 0));
        }

        if (!situation.Motion)
        {
            yield break;
        }

        var bored = state.Boredom / 100;
        var pep = tired ? 0.3 : state.Energy > Lively ? 1.3 : 1;
        yield return (Wander, 2);
        if (!state.Cold)
        {
            yield return (Chase, (1 + (bored * 2)) * pep);
            if (state.Affection >= 0)
            {
                yield return (Strut, (1 + bored) * pep);
            }

            if (state.Affection >= 10 && state.Boredom >= 30)
            {
                yield return (Fetch, (0.6 + bored) * pep);
                yield return (HideSeek, (0.6 + bored) * pep);
                yield return (Catch, (0.6 + bored) * pep);
            }
        }

        if (!situation.MischiefEnabled)
        {
            yield break;
        }

        if (state.Cold || state.Resentment >= 20 || state.Boredom >= 50)
        {
            yield return (Tease, 1 + (state.Resentment / 40));
        }

        var forgotten = state.LastAttentionAt is not { } attended || now - attended >= PesterAfter;
        if (forgotten && state.Boredom >= 30)
        {
            yield return (Pester, 1 + (bored * 2) + (state.NeglectDays > 0 ? 1 : 0));
        }
    }
}
