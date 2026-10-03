namespace Amarin.Core;

/// <summary>
/// Пороги остатка (E4): когда сказать «денег мало» и «почти кончились». Раньше это были
/// константы плашки ($1 и $0.25) — у человека, пополняющего счёт на $500, и у того, кто кладёт
/// по $5, «мало» разное.
/// </summary>
public sealed class BalanceThresholds
{
    /// <summary>Сумма по всем ключам, ниже которой — «мало». Null — не предупреждать.</summary>
    public decimal? LowUsd { get; set; } = 1.00m;

    /// <summary>Сумма по всем ключам, ниже которой — «почти кончились». Null — не предупреждать.</summary>
    public decimal? CriticalUsd { get; set; } = 0.25m;

    /// <summary>Порог «мало» у отдельных ключей: отпечаток секрета → доллары.</summary>
    public Dictionary<string, decimal> Keys { get; set; } = new(StringComparer.Ordinal);
}

internal enum BalanceLevel
{
    Ok,
    Low,
    Critical
}

/// <summary>Остаток перешёл порог.</summary>
/// <param name="KeyLabel">Название ключа; null — сумма по всем.</param>
internal sealed record BalanceAlert(string? KeyLabel, decimal Usd, decimal Threshold, BalanceLevel Level);

/// <summary>
/// Следит за переходами порогов. Предупреждает о переходе вниз, а не о состоянии: остаток,
/// который уже был ниже порога на запуске, человек видит на плашке, а напоминание на каждом
/// ответе превратилось бы в шум.
/// </summary>
internal sealed class BalanceWatch
{
    private const string TotalScope = "";

    private readonly Dictionary<string, BalanceLevel> _last = new(StringComparer.Ordinal);

    public static BalanceLevel Level(decimal? usd, decimal? low, decimal? critical) =>
        usd is not { } value ? BalanceLevel.Ok
        : critical is > 0m && value < critical ? BalanceLevel.Critical
        : low is > 0m && value < low ? BalanceLevel.Low
        : BalanceLevel.Ok;

    /// <summary>Новое показание. Возвращает переходы вниз с прошлого показания.</summary>
    public IReadOnlyList<BalanceAlert> Observe(BalanceThresholds? thresholds, BalanceTotal total, IReadOnlyList<BalanceRow> rows)
    {
        var alerts = new List<BalanceAlert>();
        if (thresholds is null)
        {
            return alerts;
        }

        // Сумма с неответившими ключами занижена — по ней «мало» было бы ложной тревогой.
        if (total.Usd is { } sum && total.Unknown == 0)
        {
            var level = Level(sum, thresholds.LowUsd, thresholds.CriticalUsd);
            if (Worse(TotalScope, level))
            {
                if ((level == BalanceLevel.Critical ? thresholds.CriticalUsd : thresholds.LowUsd) is { } threshold)
                {
                    alerts.Add(new BalanceAlert(null, sum, threshold, level));
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Fingerprint is not { } fingerprint || !seen.Add(fingerprint) || row.Usd is not { } usd)
            {
                continue;
            }

            thresholds.Keys.TryGetValue(fingerprint, out var low);
            var level = Level(usd, low, null);
            if (Worse(fingerprint, level))
            {
                alerts.Add(new BalanceAlert(row.Label, usd, low, level));
            }
        }

        return alerts;
    }

    /// <summary>Хуже ли стало. Первое показание — отправная точка, а не переход.</summary>
    private bool Worse(string scope, BalanceLevel level)
    {
        var known = _last.TryGetValue(scope, out var previous);
        _last[scope] = level;
        return known && level > previous;
    }
}
