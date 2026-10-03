namespace Amarin.Core;

/// <summary>Заголовок и текст вопроса о лимите.</summary>
internal sealed record SpendPrompt(string Title, string Text);

/// <summary>
/// Что сказать человеку о лимитах и остатке: вопрос посреди хода, предупреждение у порога,
/// переход остатка вниз и короткое значение строки-ссылки на странице.
/// </summary>
/// <remarks>
/// До 1.30.0 эти строки собирало окно (<c>MainWindow.Limits.cs</c>, <c>SpendLimitsBlock</c>), и
/// выбор между вопросом о ходе и о периоде проверялся только оконным тестом.
/// </remarks>
internal static class SpendPrompts
{
    /// <summary>
    /// Вопрос «продолжить?». У потолка хода — сколько потратил ход; у лимита периода — сколько
    /// уже потрачено за период: человек решает по той сумме, которой лимит касается.
    /// </summary>
    public static SpendPrompt Question(SpendQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var breach = question.Breach;
        return breach.Kind == SpendLimitKind.Turn
            ? new SpendPrompt(
                Loc.Get("S.Limit.TurnTitle"),
                Loc.Format("S.Limit.TurnAsk", SpendRules.Money(question.TurnSpent), SpendRules.Money(breach.Limit)))
            : new SpendPrompt(
                Loc.Get("S.Limit.PeriodTitle"),
                Loc.Format("S.Limit.PeriodAsk", SpendRules.KindName(breach.Kind), SpendRules.Money(breach.Limit), SpendRules.Money(breach.Spent)));
    }

    /// <summary>Трата дошла до порога предупреждения.</summary>
    public static string Warning(SpendBreach breach)
    {
        ArgumentNullException.ThrowIfNull(breach);
        return Loc.Format("S.Limit.Warn", SpendRules.KindName(breach.Kind), SpendRules.Money(breach.Spent), SpendRules.Money(breach.Limit));
    }

    /// <summary>Остаток перешёл порог вниз (E4): у ключа — его название, у суммы — насколько всё плохо.</summary>
    public static string BalanceAlert(BalanceAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return alert.KeyLabel is { } key
            ? Loc.Format("S.Balance.AlertKey", key, SpendRules.Money(alert.Usd), SpendRules.Money(alert.Threshold))
            : Loc.Format(
                alert.Level == BalanceLevel.Critical ? "S.Balance.AlertCritical" : "S.Balance.AlertLow",
                SpendRules.Money(alert.Usd),
                SpendRules.Money(alert.Threshold));
    }

    /// <summary>Значение строки-ссылки «Лимиты трат»: самый заметный из лимитов коротко, или «выключено».</summary>
    public static string LimitsSummary(SpendLimits? limits) =>
        limits switch
        {
            { DayUsd: { } day } => Loc.Format("S.Limit.Short.Day", SpendReport.FormatUsd(day)),
            { MonthUsd: { } month } => Loc.Format("S.Limit.Short.Month", SpendReport.FormatUsd(month)),
            { TurnUsd: { } turn } => Loc.Format("S.Limit.Short.Turn", SpendReport.FormatUsd(turn)),
            { Keys.Count: > 0 } => Loc.Get("S.Limit.Short.Keys"),
            _ => Loc.Get("S.Common.Off")
        };
}
