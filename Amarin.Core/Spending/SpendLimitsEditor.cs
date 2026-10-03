using System.Globalization;

namespace Amarin.Core;

/// <summary>Строка ключа в таблицах лимитов и порогов: одна на секрет.</summary>
internal sealed record SpendKeyRow(string Fingerprint, string Label, LlmProvider Provider);

/// <summary>Итог правки поля суммы: что оставить в поле и что сохранить.</summary>
/// <param name="Text">Текст поля после ухода фокуса.</param>
/// <param name="Value">Сумма, которую поле теперь означает; null — «лимита нет».</param>
/// <param name="Changed">Сумма отличается от прежней — пора сохранять.</param>
internal readonly record struct MoneyFieldEdit(string Text, decimal? Value, bool Changed);

/// <summary>
/// Правка лимитов трат (E1) и порогов остатка (E4) — без окна.
/// </summary>
/// <remarks>
/// <para>
/// До 1.30.0 — код блока настроек <c>SpendLimitsBlock</c>, и проверить правила можно было только
/// оконным тестом. Блок теперь строит поля и передаёт сюда введённое.
/// </para>
/// <para>
/// Правка подменяет объект лимитов целиком, а не правит словарь на месте: его читает
/// <see cref="SpendGuard"/> из потока хода, и словарь, меняющийся под чтением, мог бы бросить.
/// </para>
/// </remarks>
/// <param name="settings">
/// Настройки в памяти — правда о лимитах, поля её только отражают. Функцией: импорт данных и
/// смена профиля подставляют службам новый объект настроек, и правка обязана попасть в него.
/// </param>
/// <param name="save">Записать настройки.</param>
internal sealed class SpendLimitsEditor(Func<AppSettings> settings, Action<AppSettings> save)
{
    /// <summary>Заводская доля лимита, с которой предупреждать.</summary>
    public const int DefaultWarnPercent = 80;

    /// <summary>Лимит или порог поменялся — строка-ссылка на странице перечитывает своё значение.</summary>
    public event Action? Changed;

    /// <summary>Лимиты как есть. Старый файл с <c>null</c> на этом месте читается как «всё выключено».</summary>
    public SpendLimits Limits => settings().SpendLimits ?? new SpendLimits();

    /// <summary>Пороги остатка как есть.</summary>
    public BalanceThresholds Thresholds => settings().BalanceThresholds ?? new BalanceThresholds();

    public void SetProfileDay(decimal? usd) => Change(copy => copy.DayUsd = usd);

    public void SetProfileMonth(decimal? usd) => Change(copy => copy.MonthUsd = usd);

    public void SetTurn(decimal? usd) => Change(copy => copy.TurnUsd = usd);

    public void SetKeyDay(string fingerprint, decimal? usd) => ChangeKey(fingerprint, key => key.DayUsd = usd);

    public void SetKeyMonth(string fingerprint, decimal? usd) => ChangeKey(fingerprint, key => key.MonthUsd = usd);

    /// <summary>
    /// Доля лимита для предупреждения из поля. Возвращает то, что теперь стоит в настройках: не
    /// число от 1 до 99 — прежнее значение, его поле и покажет.
    /// </summary>
    public int SetWarnPercent(string text)
    {
        var current = Limits.WarnPercent;
        var percent = AcceptWarnPercent(text, current);
        if (percent != current)
        {
            Change(copy => copy.WarnPercent = percent);
        }

        return percent;
    }

    public void SetBalanceLow(decimal? usd) => ChangeBalance(copy => copy.LowUsd = usd);

    public void SetBalanceCritical(decimal? usd) => ChangeBalance(copy => copy.CriticalUsd = usd);

    /// <summary>Порог «мало» у ключа. Null убирает строку ключа из настроек, а не пишет пустую.</summary>
    public void SetKeyBalanceLow(string fingerprint, decimal? usd) =>
        ChangeBalance(copy =>
        {
            if (usd is { } set)
            {
                copy.Keys[fingerprint] = set;
            }
            else
            {
                copy.Keys.Remove(fingerprint);
            }
        });

    /// <summary>
    /// Строки ключей для таблиц: по одной на секрет, без ключей, которые не расшифровать. Ключ
    /// окружения и его копия в <c>keys.json</c> — один счёт, как и в журналах трат.
    /// </summary>
    public static IReadOnlyList<SpendKeyRow> KeyRows(IEnumerable<ApiKeyEntry> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var rows = new List<SpendKeyRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in keys)
        {
            if (entry.IsBroken)
            {
                continue;
            }

            var fingerprint = ApiKeyStore.Fingerprint(entry.Secret);
            if (seen.Add(fingerprint))
            {
                rows.Add(new SpendKeyRow(fingerprint, entry.Label, entry.Provider));
            }
        }

        return rows;
    }

    /// <summary>
    /// Поле суммы потеряло фокус. Непонятное число не сохраняется «как нет лимита» молча: поле
    /// возвращает прежнее значение. «0» — осознанное «без лимита», а не опечатка.
    /// </summary>
    public static MoneyFieldEdit AcceptMoney(string? text, decimal? current)
    {
        var trimmed = (text ?? "").Trim();
        var parsed = SpendRules.ParseUsd(trimmed);
        if (trimmed.Length > 0 && parsed is null && !IsZeroOrLess(trimmed))
        {
            return new MoneyFieldEdit(SpendRules.FormatField(current), current, Changed: false);
        }

        return new MoneyFieldEdit(SpendRules.FormatField(parsed), parsed, parsed != current);
    }

    /// <summary>Доля для предупреждения: целое от 1 до 99, иначе остаётся прежняя.</summary>
    public static int AcceptWarnPercent(string? text, int current) =>
        int.TryParse((text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= 99
            ? value
            : current;

    public static SpendLimits Copy(SpendLimits source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new SpendLimits
        {
            DayUsd = source.DayUsd,
            MonthUsd = source.MonthUsd,
            TurnUsd = source.TurnUsd,
            WarnPercent = source.WarnPercent,
            Keys = source.Keys.ToDictionary(
                pair => pair.Key,
                pair => new KeySpendLimit { DayUsd = pair.Value.DayUsd, MonthUsd = pair.Value.MonthUsd },
                StringComparer.Ordinal)
        };
    }

    public static BalanceThresholds Copy(BalanceThresholds source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new BalanceThresholds
        {
            LowUsd = source.LowUsd,
            CriticalUsd = source.CriticalUsd,
            Keys = new Dictionary<string, decimal>(source.Keys, StringComparer.Ordinal)
        };
    }

    private static bool IsZeroOrLess(string text) =>
        decimal.TryParse(text.TrimStart('$').Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value <= 0m;

    /// <summary>Лимит ключа без обеих сумм убирается из словаря: пустая запись — не «без лимита», а мусор.</summary>
    private void ChangeKey(string fingerprint, Action<KeySpendLimit> change) =>
        Change(copy =>
        {
            var key = copy.Keys.TryGetValue(fingerprint, out var existing)
                ? new KeySpendLimit { DayUsd = existing.DayUsd, MonthUsd = existing.MonthUsd }
                : new KeySpendLimit();
            change(key);
            if (key.DayUsd is null && key.MonthUsd is null)
            {
                copy.Keys.Remove(fingerprint);
            }
            else
            {
                copy.Keys[fingerprint] = key;
            }
        });

    private void Change(Action<SpendLimits> change)
    {
        var target = settings();
        var copy = Copy(target.SpendLimits ?? new SpendLimits());
        change(copy);
        target.SpendLimits = copy;
        save(target);
        Changed?.Invoke();
    }

    private void ChangeBalance(Action<BalanceThresholds> change)
    {
        var target = settings();
        var copy = Copy(target.BalanceThresholds ?? new BalanceThresholds());
        change(copy);
        target.BalanceThresholds = copy;
        save(target);
        Changed?.Invoke();
    }
}
